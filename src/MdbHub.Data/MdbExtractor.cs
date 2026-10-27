using System.Data.OleDb;
using System.Runtime.InteropServices;
using MdbHub.Core;

namespace MdbHub.Data;

/// <summary>How the rows of a table are copied into the new file. Access engines differ in what they accept, so several are tried in turn.</summary>
public enum ExtractCopyMethod
{
    /// <summary>SELECT INTO the new file, run from the source, filter values as parameters.</summary>
    ParametersIntoExternal,
    /// <summary>The same, with the filter values written into the SQL (for engines that reject parameters in make-table queries).</summary>
    LiteralsIntoExternal,
    /// <summary>Run from the new file instead, reading the source through "FROM ... IN" (for engines that refuse to write from a read-only source connection).</summary>
    LiteralsFromExternal,
}

/// <param name="FilterColumns">Per extracted table, the columns actually used for filtering (one per filter, in filter order).</param>
public sealed record ExtractResult(
    IReadOnlyDictionary<string, int> ExtractedRows,
    IReadOnlyList<string> SkippedTables,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FilterColumns)
{
    /// <summary>Things that worked but not fully: a copy method had to be changed, a primary key could not be restored. Empty when all went as planned.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Tables that were extracted without their primary key (it could not be created).</summary>
    public IReadOnlyList<string> TablesWithoutKey { get; init; } = [];
}

public static class MdbExtractor
{
    /// <summary>Test hooks: called before each attempt so a test can make it fail like an engine that does not support it. Per thread.</summary>
    [ThreadStatic] internal static Action<ExtractCopyMethod, string>? BeforeCopyAttempt;
    [ThreadStatic] internal static Action<int, string>? BeforeKeyAttempt;

    private static readonly ExtractCopyMethod[] AllMethods = Enum.GetValues<ExtractCopyMethod>();

    /// <summary>
    /// Copies rows matching all <paramref name="filters"/> from every table into a new mdb at <paramref name="destPath"/>.
    /// Tables lacking any filter column are skipped.
    /// The new file is built in the temp folder and moved into place only when it is complete, so a failure never leaves a
    /// half-made file at the destination (and building never touches a network share or a folder a sync client is watching).
    /// </summary>
    /// <param name="workFolder">Where the file is built before being moved; the system temp folder when null.</param>
    /// <param name="combinations">Filters on the combined values of several columns; a table must satisfy these and <paramref name="filters"/> together.</param>
    /// <param name="methods">The copy methods to try, in order, per table (all of them by default). The one that worked is tried first for the next table.</param>
    public static ExtractResult Extract(string sourcePath, string destPath, IReadOnlyList<ColumnFilter> filters, string? workFolder = null,
        IReadOnlyList<CombinationFilter>? combinations = null, IReadOnlyList<ExtractCopyMethod>? methods = null)
    {
        combinations ??= [];
        methods ??= AllMethods;
        if (filters.Count + combinations.Count == 0
            || filters.Any(f => f.Values.Count == 0)
            || combinations.Any(c => c.Combinations is { Count: 0 }))
            throw new ArgumentException("필터 조건과 값이 최소 1개 필요합니다.", nameof(filters));
        if (methods.Count == 0) throw new ArgumentException("복사 방법이 최소 1개 필요합니다.", nameof(methods));
        if (File.Exists(destPath)) throw new IOException($"이미 존재하는 파일입니다: {destPath}");

        var step = "원본 읽기";
        var schema = MdbReader.ReadSchema(sourcePath);
        var work = Path.Combine(workFolder ?? Path.GetTempPath(), $"MdbHub_{Guid.NewGuid():N}.mdb");

        try
        {
            step = "새 파일 만들기";
            MdbFile.Create(work);

            step = "데이터 복사";
            var warnings = new List<string>();
            var (extracted, skipped, used) = CopyRows(sourcePath, work, schema, filters, combinations, methods, warnings);

            step = "키 복원";
            var withoutKey = AddPrimaryKeys(work, schema, extracted.Keys, warnings);

            step = "완성된 파일 옮기기";
            MdbFile.ReleasePool();   // every connection to the new file must be gone before it is moved
            if (File.Exists(destPath)) throw new IOException($"이미 존재하는 파일입니다: {destPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
            MoveWithRetry(work, destPath);
            return new ExtractResult(extracted, skipped, used) { Warnings = warnings, TablesWithoutKey = withoutKey };
        }
        catch (Exception ex)
        {
            MdbFile.ReleasePool();
            var cleaned = MdbFile.TryDelete(work);
            MdbFile.TryDelete(Path.ChangeExtension(work, ".ldb"));
            MdbFile.TryDelete(Path.ChangeExtension(work, ".laccdb"));

            // Say which step failed (engine errors alone don't), and report the real problem even when the leftover could not be removed.
            var leftover = cleaned ? "" : $"\n⚠ 작업 중이던 임시 파일을 지우지 못했습니다. 직접 지워 주세요: {work}";
            if (ex is OleDbException or COMException or InvalidOperationException || !cleaned)
                throw new InvalidOperationException($"추출 중 오류 ({step} 단계): {ex.Message}{leftover}", ex);
            throw;
        }
    }

    private static (Dictionary<string, int> Extracted, List<string> Skipped, Dictionary<string, IReadOnlyList<string>> Used) CopyRows(
        string sourcePath, string workPath, DatabaseSchema schema, IReadOnlyList<ColumnFilter> filters, IReadOnlyList<CombinationFilter> combinations,
        IReadOnlyList<ExtractCopyMethod> methods, List<string> warnings)
    {
        var extracted = new Dictionary<string, int>();
        var skipped = new List<string>();
        var used = new Dictionary<string, IReadOnlyList<string>>();

        // Both connections are closed again before the primary keys are added: SELECT ... INTO ... IN keeps the other file attached.
        using var sourceConn = MdbFile.Open(sourcePath, readOnly: true);
        OleDbConnection? workConn = null;
        var preferred = 0;
        var reportedFallback = false;

        try
        {
            foreach (var table in schema.Tables)
            {
                if (filters.Any(f => f.ResolveIn(table) is null) || combinations.Any(c => !c.AppliesTo(table))) { skipped.Add(table.Name); continue; }
                used[table.Name] = filters.Select(f => f.ResolveIn(table)!)
                    .Concat(combinations.Select(c => string.Join(" + ", c.ResolveIn(table)))).ToList();

                var attempts = new List<(ExtractCopyMethod Method, Exception Error)>();
                var done = false;
                for (var k = 0; k < methods.Count && !done; k++)
                {
                    var method = methods[(preferred + k) % methods.Count];
                    try
                    {
                        BeforeCopyAttempt?.Invoke(method, table.Name);
                        extracted[table.Name] = CopyTable(method, table, sourceConn, ref workConn, sourcePath, workPath, filters, combinations);
                        preferred = (preferred + k) % methods.Count;
                        done = true;
                        if (attempts.Count > 0 && !reportedFallback)
                        {
                            reportedFallback = true;
                            warnings.Add($"데이터 복사 방법 '{Describe(attempts[0].Method)}'이(가) [{table.Name}]에서 실패해 '{Describe(method)}'(으)로 진행했습니다: {attempts[0].Error.Message}");
                        }
                    }
                    catch (Exception ex) when (ex is OleDbException or NotSupportedException)
                    {
                        attempts.Add((method, ex));
                        DropWorkTableIfCreated(workPath, table.Name);
                    }
                }

                if (!done)
                    throw new InvalidOperationException(
                        $"[{table.Name}] 데이터를 복사하지 못했습니다. 시도한 방법과 오류:\n" +
                        string.Join("\n", attempts.Select(a => $"- {Describe(a.Method)}: {a.Error.Message}")),
                        attempts[0].Error);
            }
        }
        finally
        {
            workConn?.Dispose();
        }
        return (extracted, skipped, used);
    }

    private static int CopyTable(ExtractCopyMethod method, TableSchema table, OleDbConnection sourceConn, ref OleDbConnection? workConn,
        string sourcePath, string workPath, IReadOnlyList<ColumnFilter> filters, IReadOnlyList<CombinationFilter> combinations)
    {
        var t = MdbFile.Quote(table.Name);
        if (method == ExtractCopyMethod.LiteralsFromExternal)
        {
            workConn ??= MdbFile.Open(workPath);
            using var fromSource = workConn.CreateCommand();
            fromSource.CommandText = $"SELECT * INTO {t} FROM {t} IN '{sourcePath.Replace("'", "''")}'" +
                                     MdbReader.WhereClause(fromSource, table, filters, combinations, inlineValues: true);
            return fromSource.ExecuteNonQuery();
        }

        using var cmd = sourceConn.CreateCommand();
        cmd.CommandText = $"SELECT * INTO {t} IN '{workPath.Replace("'", "''")}' FROM {t}" +
                          MdbReader.WhereClause(cmd, table, filters, combinations, inlineValues: method == ExtractCopyMethod.LiteralsIntoExternal);
        return cmd.ExecuteNonQuery();
    }

    // A failed make-table query should not have created the table, but make sure a later attempt does not trip over a leftover.
    private static void DropWorkTableIfCreated(string workPath, string table)
    {
        try
        {
            using var conn = MdbFile.Open(workPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP TABLE {MdbFile.Quote(table)}";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is OleDbException or InvalidOperationException) { }   // there was nothing to drop
    }

    private static string Describe(ExtractCopyMethod method) => method switch
    {
        ExtractCopyMethod.ParametersIntoExternal => "원본에서 실행(값은 매개변수)",
        ExtractCopyMethod.LiteralsIntoExternal => "원본에서 실행(값을 SQL에 직접)",
        _ => "새 파일에서 실행(원본을 IN으로 읽기)",
    };

    // SELECT INTO copies columns and data but not keys, so restore them to keep the schema identical to the source.
    // A key that cannot be created is a warning, not a failure: the data is intact and the key can come from the config later.
    private static List<string> AddPrimaryKeys(string path, DatabaseSchema source, IEnumerable<string> tables, List<string> warnings)
    {
        var withoutKey = new List<string>();
        using var conn = MdbFile.Open(path);
        foreach (var name in tables)
        {
            var pk = source.Find(name)!.PrimaryKey;
            if (pk.Count == 0) continue;

            var q = MdbFile.Quote(name);
            var columns = string.Join(", ", pk.Select(MdbFile.Quote));
            string[] statements =
            [
                $"ALTER TABLE {q} ADD CONSTRAINT {MdbFile.Quote("PK_" + name)} PRIMARY KEY ({columns})",
                $"CREATE UNIQUE INDEX {MdbFile.Quote("PK_" + name)} ON {q} ({columns}) WITH PRIMARY",
            ];

            string? firstError = null;
            var created = false;
            for (var i = 0; i < statements.Length && !created; i++)
            {
                try
                {
                    BeforeKeyAttempt?.Invoke(i, name);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = statements[i];
                    cmd.ExecuteNonQuery();
                    created = true;
                }
                catch (Exception ex) when (ex is OleDbException or NotSupportedException)
                {
                    firstError ??= ex.Message;
                }
            }

            if (created) continue;
            withoutKey.Add(name);
            warnings.Add($"[{name}] 키({string.Join(", ", pk)})를 만들지 못해 키 없이 추출했습니다: {firstError}");
        }
        return withoutKey;
    }

    // A virus scanner or sync client may hold the finished file for a moment; try a few times before giving up.
    private static void MoveWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to);
                return;
            }
            catch (IOException) when (attempt < 6 && !File.Exists(to))
            {
                Thread.Sleep(200);
            }
        }
    }
}


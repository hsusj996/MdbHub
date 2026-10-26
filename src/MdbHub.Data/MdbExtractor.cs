using System.Data.OleDb;
using MdbHub.Core;

namespace MdbHub.Data;

/// <param name="FilterColumns">Per extracted table, the columns actually used for filtering (one per filter, in filter order).</param>
public sealed record ExtractResult(
    IReadOnlyDictionary<string, int> ExtractedRows,
    IReadOnlyList<string> SkippedTables,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FilterColumns);

public static class MdbExtractor
{
    /// <summary>
    /// Copies rows matching all <paramref name="filters"/> from every table into a new mdb at <paramref name="destPath"/>.
    /// Tables lacking any filter column are skipped.
    /// The new file is built in the temp folder and moved into place only when it is complete, so a failure never leaves a
    /// half-made file at the destination (and building never touches a network share or a folder a sync client is watching).
    /// </summary>
    /// <param name="workFolder">Where the file is built before being moved; the system temp folder when null.</param>
    public static ExtractResult Extract(string sourcePath, string destPath, IReadOnlyList<ColumnFilter> filters, string? workFolder = null)
    {
        if (filters.Count == 0 || filters.Any(f => f.Values.Count == 0))
            throw new ArgumentException("필터 조건과 값이 최소 1개 필요합니다.", nameof(filters));
        if (File.Exists(destPath)) throw new IOException($"이미 존재하는 파일입니다: {destPath}");

        var schema = MdbReader.ReadSchema(sourcePath);
        var work = Path.Combine(workFolder ?? Path.GetTempPath(), $"MdbHub_{Guid.NewGuid():N}.mdb");

        try
        {
            MdbFile.Create(work);
            var (extracted, skipped, used) = CopyRows(sourcePath, work, schema, filters);
            AddPrimaryKeys(work, schema, extracted.Keys);

            OleDbConnection.ReleaseObjectPool();   // every connection to the new file must be gone before it is moved
            if (File.Exists(destPath)) throw new IOException($"이미 존재하는 파일입니다: {destPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
            MoveWithRetry(work, destPath);
            return new ExtractResult(extracted, skipped, used);
        }
        catch (Exception ex)
        {
            OleDbConnection.ReleaseObjectPool();
            var cleaned = MdbFile.TryDelete(work);
            MdbFile.TryDelete(Path.ChangeExtension(work, ".ldb"));
            MdbFile.TryDelete(Path.ChangeExtension(work, ".laccdb"));

            // Report the real problem even when the leftover could not be removed.
            if (!cleaned) throw new InvalidOperationException($"{ex.Message}\n⚠ 작업 중이던 임시 파일을 지우지 못했습니다. 직접 지워 주세요: {work}", ex);
            throw;
        }
    }

    private static (Dictionary<string, int> Extracted, List<string> Skipped, Dictionary<string, IReadOnlyList<string>> Used) CopyRows(
        string sourcePath, string workPath, DatabaseSchema schema, IReadOnlyList<ColumnFilter> filters)
    {
        var extracted = new Dictionary<string, int>();
        var skipped = new List<string>();
        var used = new Dictionary<string, IReadOnlyList<string>>();

        // Closed again before the primary keys are added: SELECT ... INTO ... IN keeps the new file attached to this connection.
        using var conn = MdbFile.Open(sourcePath, readOnly: true);
        foreach (var table in schema.Tables)
        {
            if (filters.Any(f => f.ResolveIn(table) is null)) { skipped.Add(table.Name); continue; }
            used[table.Name] = filters.Select(f => f.ResolveIn(table)!).ToList();

            using var cmd = conn.CreateCommand();
            var t = MdbFile.Quote(table.Name);
            cmd.CommandText = $"SELECT * INTO {t} IN '{workPath.Replace("'", "''")}' FROM {t}{MdbReader.WhereClause(cmd, table, filters)}";
            extracted[table.Name] = cmd.ExecuteNonQuery();
        }
        return (extracted, skipped, used);
    }

    // SELECT INTO copies columns and data but not keys, so restore them to keep the schema identical to the source.
    private static void AddPrimaryKeys(string path, DatabaseSchema source, IEnumerable<string> tables)
    {
        using var conn = MdbFile.Open(path);
        foreach (var name in tables)
        {
            var pk = source.Find(name)!.PrimaryKey;
            if (pk.Count == 0) continue;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {MdbFile.Quote(name)} ADD CONSTRAINT {MdbFile.Quote("PK_" + name)} PRIMARY KEY ({string.Join(", ", pk.Select(MdbFile.Quote))})";
            cmd.ExecuteNonQuery();
        }
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

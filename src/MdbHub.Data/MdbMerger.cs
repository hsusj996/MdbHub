using System.Data.OleDb;
using System.Text;
using MdbHub.Core;

namespace MdbHub.Data;

/// <param name="Schema">The table as the merge sees it (column order and key); rows in <paramref name="Changes"/> follow it.</param>
public sealed record MergeTableChanges(TableSchema Schema, IReadOnlyList<RowChange> Changes);

public sealed record MergeFileChanges(string Path, IReadOnlyList<MergeTableChanges> Tables)
{
    public int ChangeCount => Tables.Sum(t => t.Changes.Count);
}

public sealed record MergeFileResult(string Path, int Inserts, int Updates, string BackupPath, string LogPath);

public static class MdbMerger
{
    /// <summary>
    /// Writes merge results back to their files. Before anything is written every file is checked to still hold the rows the merge
    /// started from (so someone else's changes are not overwritten) and backed up. Each file is changed in one transaction; if a later
    /// file fails, the files already saved are put back from their backups. A ".merge.csv" next to each saved file lists what changed.
    /// </summary>
    public static IReadOnlyList<MergeFileResult> Save(IReadOnlyList<MergeFileChanges> files, DateTime when)
    {
        var work = files.Where(f => f.ChangeCount > 0).ToList();
        if (work.Count == 0) return [];

        foreach (var file in work) Verify(file);

        var backups = work.ToDictionary(f => f.Path, f => MdbBackup.Create(f.Path, when));

        var written = new List<MergeFileChanges>();
        try
        {
            foreach (var file in work)
            {
                Write(file);
                written.Add(file);
            }
        }
        catch (Exception ex)
        {
            OleDbConnection.ReleaseObjectPool();   // let go of the files before putting backups over them
            var failed = new List<string>();
            foreach (var done in written)
            {
                try { File.Copy(backups[done.Path], done.Path, overwrite: true); }
                catch (Exception restore) { failed.Add($"{done.Path}: {restore.Message}"); }
            }

            var note = written.Count == 0
                ? "파일은 바뀌지 않았습니다."
                : failed.Count == 0
                    ? $"이미 저장된 파일 {written.Count}개는 백업으로 되돌렸습니다."
                    : "⚠ 이미 저장된 파일을 되돌리지 못했습니다. 백업에서 직접 복원하세요:\n" + string.Join("\n", failed);
            throw new InvalidOperationException($"저장 중 오류가 발생했습니다: {ex.Message}\n{note}", ex);
        }

        var results = new List<MergeFileResult>();
        foreach (var file in work)
        {
            var log = WriteLog(file, when);
            var changes = file.Tables.SelectMany(t => t.Changes).ToList();
            results.Add(new MergeFileResult(file.Path, changes.Count(c => c.Kind == RowChangeKind.Insert), changes.Count(c => c.Kind == RowChangeKind.Update), backups[file.Path], log));
        }
        return results;
    }

    // The file must still hold exactly what the merge started from: an updated row unchanged, an inserted row's key absent.
    private static void Verify(MergeFileChanges file)
    {
        var schema = MdbReader.ReadSchema(file.Path);
        foreach (var table in file.Tables.Where(t => t.Changes.Count > 0))
        {
            var actual = schema.Find(table.Schema.Name)
                ?? throw new InvalidOperationException($"[{file.Path}] 테이블 '{table.Schema.Name}'이(가) 없습니다.");
            var fileIndex = table.Schema.Columns.Select(c => actual.IndexOf(c.Name)).ToArray();
            if (fileIndex.Any(i => i < 0))
                throw new InvalidOperationException($"[{file.Path}] 테이블 '{table.Schema.Name}'의 컬럼이 비교할 때와 달라졌습니다. 다시 비교하세요.");

            var rows = MdbReader.ReadTable(file.Path, actual).Rows
                .ToDictionary(r => RowKey.Of(table.Schema.PrimaryKey.Select(k => r[actual.IndexOf(k)])));

            foreach (var change in table.Changes)
            {
                var key = RowKey.Of(change.Key);
                var exists = rows.TryGetValue(key, out var row);
                if (change.Kind == RowChangeKind.Insert && exists)
                    throw Stale(file, table, change, "이미 같은 키의 행이 생겼습니다");
                if (change.Kind == RowChangeKind.Update)
                {
                    if (!exists) throw Stale(file, table, change, "행이 없어졌습니다");
                    for (var i = 0; i < fileIndex.Length; i++)
                        if (!TableComparer.ValueEquals(row![fileIndex[i]], change.Original![i]))
                            throw Stale(file, table, change, $"'{table.Schema.Columns[i].Name}' 값이 비교할 때와 달라졌습니다");
                }
            }
        }
    }

    private static InvalidOperationException Stale(MergeFileChanges file, MergeTableChanges table, RowChange change, string what) =>
        new($"[{Path.GetFileName(file.Path)}] {table.Schema.Name} 키 ({string.Join(", ", change.Key)}): {what}.\n" +
            "비교한 뒤에 파일이 바뀐 것 같습니다. 아무것도 저장하지 않았으니 다시 비교하세요.");

    private static void Write(MergeFileChanges file)
    {
        using var conn = MdbFile.Open(file.Path);
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var table in file.Tables)
            {
                var columns = table.Schema.Columns.Select(c => c.Name).ToList();
                foreach (var change in table.Changes)
                {
                    if (change.Kind == RowChangeKind.Insert)
                        RowWriter.Insert(conn, tx, table.Schema.Name, columns, change.Current);
                    else
                        RowWriter.Update(conn, tx, table.Schema.Name, table.Schema.PrimaryKey, change.Key,
                            change.ChangedColumns.Select(i => columns[i]).ToList(), change.ChangedColumns.Select(i => change.Current[i]).ToList());
                }
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // One line per changed cell of an update, one line per inserted row (all its values).
    private static string WriteLog(MergeFileChanges file, DateTime when)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file.Path))!,
            $"{System.IO.Path.GetFileNameWithoutExtension(file.Path)}.{when:yyyyMMdd_HHmmss}.merge.csv");

        var sb = new StringBuilder();
        sb.AppendLine("테이블,구분,키,컬럼,이전 값,새 값");
        foreach (var table in file.Tables.OrderBy(t => t.Schema.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var change in table.Changes)
            {
                var key = string.Join(" / ", change.Key.Select(k => SideBySide.FormatValue(k)));
                if (change.Kind == RowChangeKind.Insert)
                {
                    var values = string.Join("; ", table.Schema.Columns.Select((c, i) => $"{c.Name}={SideBySide.FormatValue(change.Current[i], markNull: true)}"));
                    sb.AppendLine(EditLog.CsvLine([table.Schema.Name, "삽입", key, "(행 전체)", "", values]));
                    continue;
                }
                foreach (var i in change.ChangedColumns)
                    sb.AppendLine(EditLog.CsvLine([table.Schema.Name, "수정", key, table.Schema.Columns[i].Name,
                        SideBySide.FormatValue(change.Original![i], markNull: true), SideBySide.FormatValue(change.Current[i], markNull: true)]));
            }
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}

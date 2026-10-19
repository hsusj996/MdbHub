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
    /// Tables lacking any filter column are skipped. The destination is deleted again if anything fails.
    /// </summary>
    public static ExtractResult Extract(string sourcePath, string destPath, IReadOnlyList<ColumnFilter> filters)
    {
        if (filters.Count == 0 || filters.Any(f => f.Values.Count == 0))
            throw new ArgumentException("필터 조건과 값이 최소 1개 필요합니다.", nameof(filters));

        var schema = MdbReader.ReadSchema(sourcePath);
        MdbFile.Create(destPath);

        try
        {
            var extracted = new Dictionary<string, int>();
            var skipped = new List<string>();
            var used = new Dictionary<string, IReadOnlyList<string>>();

            using var conn = MdbFile.Open(sourcePath, readOnly: true);
            foreach (var table in schema.Tables)
            {
                if (filters.Any(f => f.ResolveIn(table) is null)) { skipped.Add(table.Name); continue; }
                used[table.Name] = filters.Select(f => f.ResolveIn(table)!).ToList();

                using var cmd = conn.CreateCommand();
                var t = MdbFile.Quote(table.Name);
                cmd.CommandText = $"SELECT * INTO {t} IN '{destPath.Replace("'", "''")}' FROM {t}{MdbReader.WhereClause(cmd, table, filters)}";
                extracted[table.Name] = cmd.ExecuteNonQuery();
            }

            AddPrimaryKeys(destPath, schema, extracted.Keys);
            return new ExtractResult(extracted, skipped, used);
        }
        catch
        {
            OleDbConnection.ReleaseObjectPool();
            if (File.Exists(destPath)) File.Delete(destPath);
            throw;
        }
    }

    // SELECT INTO copies columns and data but not keys, so restore them to keep the schema identical to the source.
    private static void AddPrimaryKeys(string destPath, DatabaseSchema source, IEnumerable<string> tables)
    {
        using var conn = MdbFile.Open(destPath);
        foreach (var name in tables)
        {
            var pk = source.Find(name)!.PrimaryKey;
            if (pk.Count == 0) continue;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {MdbFile.Quote(name)} ADD CONSTRAINT {MdbFile.Quote("PK_" + name)} PRIMARY KEY ({string.Join(", ", pk.Select(MdbFile.Quote))})";
            cmd.ExecuteNonQuery();
        }
    }
}

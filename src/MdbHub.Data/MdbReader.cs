using System.Data;
using System.Data.OleDb;
using MdbHub.Core;

namespace MdbHub.Data;

public static class MdbReader
{
    public static DatabaseSchema ReadSchema(string path)
    {
        using var conn = MdbFile.Open(path, readOnly: true);

        var pks = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Primary_Keys, null)!.Rows.Cast<DataRow>()
            .GroupBy(r => (string)r["TABLE_NAME"], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key,
                g => (IReadOnlyList<string>)g.OrderBy(r => Convert.ToInt32(r["ORDINAL"])).Select(r => (string)r["COLUMN_NAME"]).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var columns = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Columns, null)!.Rows.Cast<DataRow>()
            .GroupBy(r => (string)r["TABLE_NAME"], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => Convert.ToInt32(r["ORDINAL_POSITION"])).ToList(), StringComparer.OrdinalIgnoreCase);

        var tables = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, [null, null, null, "TABLE"])!.Rows.Cast<DataRow>()
            .Select(r => (string)r["TABLE_NAME"])
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => new TableSchema(
                name,
                columns[name].Select(c => new ColumnInfo((string)c["COLUMN_NAME"], ((OleDbType)Convert.ToInt32(c["DATA_TYPE"])).ToString())).ToList(),
                pks.GetValueOrDefault(name, [])))
            .ToList();

        return new DatabaseSchema(tables);
    }

    public static TableData ReadTable(string path, TableSchema table, IReadOnlyList<ColumnFilter>? filters = null)
    {
        using var conn = MdbFile.Open(path, readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {MdbFile.Quote(table.Name)}{WhereClause(cmd, table, filters)}";

        using var reader = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return new TableData(table, rows);
    }

    public static IReadOnlyDictionary<string, int> GetRowCounts(string path, DatabaseSchema schema)
    {
        using var conn = MdbFile.Open(path, readOnly: true);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in schema.Tables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {MdbFile.Quote(table.Name)}";
            counts[table.Name] = Convert.ToInt32(cmd.ExecuteScalar());
        }
        return counts;
    }

    public static IReadOnlyList<object> GetDistinctValues(string path, DatabaseSchema schema, string column) =>
        GetDistinctValues(path, schema, [column]);

    /// <summary>
    /// Distinct values (the check-list for the filter UI) of the alias columns: every table contributes the first alias it has,
    /// the same column an extract with this filter would read. Values equal as text (e.g. 5 as Int16 and Int32) are listed once.
    /// </summary>
    public static IReadOnlyList<object> GetDistinctValues(string path, DatabaseSchema schema, IReadOnlyList<string> columns)
    {
        var filter = new ColumnFilter(columns, []);
        using var conn = MdbFile.Open(path, readOnly: true);
        var values = new Dictionary<string, object>();
        foreach (var table in schema.Tables)
        {
            var column = filter.ResolveIn(table);
            if (column is null) continue;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT {MdbFile.Quote(column)} FROM {MdbFile.Quote(table.Name)}";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var value = reader.GetValue(0);
                values.TryAdd(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "", value);
            }
        }
        return values.Values.Order(MixedTypeComparer.Instance).ToList();
    }

    /// <summary>Builds " WHERE [c] IN (?,?) AND ..." for <paramref name="table"/> and adds the matching parameters to <paramref name="cmd"/>.</summary>
    internal static string WhereClause(OleDbCommand cmd, TableSchema table, IReadOnlyList<ColumnFilter>? filters)
    {
        if (filters is null || filters.Count == 0) return "";

        var parts = new List<string>();
        foreach (var f in filters)
        {
            var column = f.ResolveIn(table)
                ?? throw new InvalidOperationException($"테이블 '{table.Name}'에는 필터 컬럼({string.Join(", ", f.Columns)})이 없습니다.");
            parts.Add($"{MdbFile.Quote(column)} IN ({string.Join(",", f.Values.Select(_ => "?"))})");
            foreach (var v in f.Values) cmd.Parameters.AddWithValue("?", v);
        }
        return " WHERE " + string.Join(" AND ", parts);
    }

    /// <summary>Orders numbers numerically, otherwise naturally; values of unrelated types fall back to text order instead of throwing.</summary>
    private sealed class MixedTypeComparer : IComparer<object>
    {
        public static readonly MixedTypeComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (IsNumber(x) && IsNumber(y)) return Convert.ToDecimal(x).CompareTo(Convert.ToDecimal(y));
            if (x?.GetType() == y?.GetType() && x is IComparable c) return c.CompareTo(y);
            return string.CompareOrdinal(x?.ToString(), y?.ToString());
        }

        private static bool IsNumber(object? v) =>
            v is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }
}

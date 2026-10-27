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

    /// <summary>The most combinations a check-list will offer; more means the chosen columns are not a sensible grouping.</summary>
    public const int MaxCombinations = 5000;

    /// <summary>
    /// Distinct combinations of the given columns (the check-list for combination filters and apply scopes): every table that has
    /// all of them contributes. Rows with a null in any of the columns are ignored. Sorted by value.
    /// </summary>
    public static IReadOnlyList<CombinationValue> GetDistinctCombinations(string path, DatabaseSchema schema, IReadOnlyList<string> columns)
    {
        var filter = new CombinationFilter(columns, null);
        using var conn = MdbFile.Open(path, readOnly: true);
        var found = new Dictionary<string, object[]>();

        foreach (var table in schema.Tables.Where(filter.AppliesTo))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT {string.Join(", ", filter.ResolveIn(table).Select(MdbFile.Quote))} FROM {MdbFile.Quote(table.Name)}";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var values = new object[columns.Count];
                var complete = true;
                for (var i = 0; i < values.Length && complete; i++)
                {
                    if (reader.IsDBNull(i)) complete = false;
                    else values[i] = reader.GetValue(i);
                }
                if (complete) found.TryAdd(RowKey.Of(values), values);
                if (found.Count > MaxCombinations)
                    throw new InvalidOperationException($"선택한 컬럼({string.Join(", ", columns)})의 값 조합이 {MaxCombinations:N0}개를 넘습니다. 묶음 기준으로 쓰기에는 너무 세분화된 컬럼입니다.");
            }
        }

        return found.Values.OrderBy(v => (object?[])v, KeyComparer.Instance).Select(v => new CombinationValue(v)).ToList();
    }

    /// <summary>
    /// Builds " WHERE ..." for <paramref name="table"/>: every column filter as [c] IN (?,?) and every combination filter as
    /// ([a] = ? AND [b] = ?) OR (...), all joined by AND, and adds the matching parameters to <paramref name="cmd"/>.
    /// </summary>
    /// <param name="inlineValues">Write the values into the SQL instead of passing them as parameters.</param>
    internal static string WhereClause(OleDbCommand cmd, TableSchema table, IReadOnlyList<ColumnFilter>? filters,
        IReadOnlyList<CombinationFilter>? combinations = null, bool inlineValues = false)
    {
        // A value is either a "?" with a parameter, or its literal text.
        string Value(object v)
        {
            if (inlineValues) return SqlLiteral.Of(v);
            cmd.Parameters.AddWithValue("?", v);
            return "?";
        }

        var parts = new List<string>();
        foreach (var f in filters ?? [])
        {
            var column = f.ResolveIn(table)
                ?? throw new InvalidOperationException($"테이블 '{table.Name}'에는 필터 컬럼({string.Join(", ", f.Columns)})이 없습니다.");
            parts.Add($"{MdbFile.Quote(column)} IN ({string.Join(",", f.Values.Select(Value))})");
        }

        foreach (var c in combinations ?? [])
        {
            if (!c.AppliesTo(table))
                throw new InvalidOperationException($"테이블 '{table.Name}'에는 조합 필터 컬럼({string.Join(", ", c.Columns)})이 없습니다.");
            if (c.Combinations is null) continue;   // only requires the columns
            if (c.Combinations.Count == 0) { parts.Add("(1 = 0)"); continue; }   // nothing chosen: nothing matches

            var columns = c.ResolveIn(table);
            var alternatives = c.Combinations.Select(combination =>
                "(" + string.Join(" AND ", columns.Select((col, i) => $"{MdbFile.Quote(col)} = {Value(combination[i])}")) + ")");
            parts.Add("(" + string.Join(" OR ", alternatives) + ")");
        }

        return parts.Count == 0 ? "" : " WHERE " + string.Join(" AND ", parts);
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

namespace MdbHub.Core;

public sealed record ColumnInfo(string Name, string DataType);

public sealed record TableSchema(string Name, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<string> PrimaryKey)
{
    public bool HasColumn(string column) =>
        Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));

    public int IndexOf(string column) =>
        Columns.ToList().FindIndex(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
}

public sealed record DatabaseSchema(IReadOnlyList<TableSchema> Tables)
{
    public TableSchema? Find(string table) =>
        Tables.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Rows are stored in the column order of <see cref="Schema"/>; DBNull is normalized to null.</summary>
public sealed record TableData(TableSchema Schema, IReadOnlyList<object?[]> Rows);

/// <summary>
/// Keeps only rows whose column value is one of <see cref="Values"/>. <see cref="Columns"/> are aliases for the same
/// concept (e.g. CHEMICAL_NO and ID): each table uses the first alias it actually has.
/// </summary>
public sealed record ColumnFilter(IReadOnlyList<string> Columns, IReadOnlyList<object> Values)
{
    public ColumnFilter(string column, IReadOnlyList<object> values) : this([column], values)
    {
    }

    /// <summary>The column this filter reads in <paramref name="table"/>, or null when the table has none of the aliases.</summary>
    public string? ResolveIn(TableSchema table)
    {
        foreach (var alias in Columns)
        {
            var index = table.IndexOf(alias);
            if (index >= 0) return table.Columns[index].Name;
        }
        return null;
    }
}

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

/// <summary>One combination of values of several columns, e.g. (CHEMICAL_NO, SUB_CHEMICAL_NO) = (100, 1); shows as "100 | 1".</summary>
public sealed record CombinationValue(IReadOnlyList<object> Values)
{
    public override string ToString() =>
        string.Join(" | ", Values.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.CurrentCulture)));
}

/// <summary>
/// Keeps only rows whose values in <see cref="Columns"/>, taken together, form one of <see cref="Combinations"/>
/// (so choosing (100,1) and (200,2) never lets (100,2) through, as two separate filters would).
/// A table is only affected if it has every column; one that lacks any is left out of an extract.
/// With <see cref="Combinations"/> null it only requires the columns to exist and restricts nothing else.
/// </summary>
public sealed record CombinationFilter(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object>>? Combinations)
{
    public bool AppliesTo(TableSchema table) => Columns.All(table.HasColumn);

    /// <summary>The table's own spelling of each column, in the order of <see cref="Columns"/>.</summary>
    public IReadOnlyList<string> ResolveIn(TableSchema table) => Columns.Select(c => table.Columns[table.IndexOf(c)].Name).ToList();
}

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

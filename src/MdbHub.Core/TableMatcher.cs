namespace MdbHub.Core;

public enum TableMatchKind { Both, LeftOnly, RightOnly }

/// <param name="Left">Null when the table exists only on the right.</param>
/// <param name="Right">Null when the table exists only on the left.</param>
/// <param name="SchemaDifferences">For tables on both sides: why their structures (columns, types, key) differ. Empty when they match.</param>
public sealed record TableMatch(
    string Name,
    TableMatchKind Kind,
    TableSchema? Left,
    TableSchema? Right,
    IReadOnlyList<string> SchemaDifferences)
{
    public bool IsCommon => Kind == TableMatchKind.Both;
}

public static class TableMatcher
{
    /// <summary>
    /// Pairs the tables of two databases by name (case-insensitive): left tables in their order, then tables only on the right.
    /// Databases with different table sets can still be compared on the tables they share.
    /// </summary>
    public static IReadOnlyList<TableMatch> Match(DatabaseSchema left, DatabaseSchema right)
    {
        var matches = new List<TableMatch>();

        foreach (var l in left.Tables)
        {
            var r = right.Find(l.Name);
            if (r is null)
            {
                matches.Add(new TableMatch(l.Name, TableMatchKind.LeftOnly, l, null, []));
                continue;
            }

            // Compare this one table in isolation, so the table-level differences ("missing on the other side") don't appear.
            var differences = SchemaValidator.Compare(new DatabaseSchema([l]), new DatabaseSchema([r]));
            matches.Add(new TableMatch(l.Name, TableMatchKind.Both, l, r, differences));
        }

        foreach (var r in right.Tables.Where(r => left.Find(r.Name) is null))
            matches.Add(new TableMatch(r.Name, TableMatchKind.RightOnly, null, r, []));

        return matches;
    }
}

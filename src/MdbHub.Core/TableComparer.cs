namespace MdbHub.Core;

public enum RowDiffKind { Added, Removed, Changed }

/// <param name="Left">Row from the left table (null when <see cref="RowDiffKind.Added"/>), in the left column order.</param>
/// <param name="Right">Row from the right table (null when <see cref="RowDiffKind.Removed"/>), in the right column order.</param>
public sealed record RowDiff(
    RowDiffKind Kind,
    IReadOnlyList<object?> Key,
    object?[]? Left,
    object?[]? Right,
    IReadOnlyList<string> ChangedColumns);

public sealed record TableDiff(string Table, IReadOnlyList<RowDiff> Rows, int UnchangedCount)
{
    public bool IsIdentical => Rows.Count == 0;
}

public static class TableComparer
{
    /// <summary>
    /// Compares two tables by key (the left schema's PrimaryKey). Added = only in right, Removed = only in left.
    /// Throws <see cref="DuplicateKeyException"/> when either side has rows sharing a key, since rows could not be matched reliably.
    /// </summary>
    /// <param name="leftName">How the left side is called in error messages.</param>
    /// <param name="rightName">How the right side is called in error messages.</param>
    public static TableDiff Compare(TableData left, TableData right, string leftName = "왼쪽", string rightName = "오른쪽")
    {
        var (rightIndex, leftKeyIdx, rightKeyIdx) = PrepareKeys(left, right, leftName, rightName);

        var rightRows = right.Rows.ToDictionary(r => KeyOf(r, rightKeyIdx));
        var diffs = new List<RowDiff>();
        var unchanged = 0;

        foreach (var l in left.Rows)
        {
            var key = KeyOf(l, leftKeyIdx);
            if (!rightRows.Remove(key, out var r))
            {
                diffs.Add(new RowDiff(RowDiffKind.Removed, KeyValues(l, leftKeyIdx), l, null, []));
                continue;
            }

            var changed = new List<string>();
            for (var i = 0; i < l.Length; i++)
                if (!ValueEquals(l[i], r[rightIndex[i]])) changed.Add(left.Schema.Columns[i].Name);

            if (changed.Count == 0) unchanged++;
            else diffs.Add(new RowDiff(RowDiffKind.Changed, KeyValues(l, leftKeyIdx), l, r, changed));
        }

        foreach (var r in rightRows.Values)
            diffs.Add(new RowDiff(RowDiffKind.Added, KeyValues(r, rightKeyIdx), null, r, []));

        return new TableDiff(left.Schema.Name, diffs, unchanged);
    }

    /// <summary>
    /// Shared checks of <see cref="Compare"/> and <see cref="TableAligner"/>: the left schema has a key, the right table has every
    /// column, and neither side has duplicate keys. Returns, for each left column, its index in the right table, plus both key index lists.
    /// </summary>
    internal static (int[] RightIndex, int[] LeftKeyIdx, int[] RightKeyIdx) PrepareKeys(
        TableData left, TableData right, string leftName, string rightName)
    {
        var pk = left.Schema.PrimaryKey;
        if (pk.Count == 0)
            throw new InvalidOperationException($"테이블 '{left.Schema.Name}'에 키가 없어 비교할 수 없습니다.");

        var rightIndex = left.Schema.Columns.Select(c => right.Schema.IndexOf(c.Name)).ToArray();
        if (rightIndex.Any(i => i < 0) || pk.Any(k => !right.Schema.HasColumn(k)))
            throw new InvalidOperationException($"테이블 '{left.Schema.Name}'의 컬럼이 두 파일에서 다릅니다.");

        var leftKeyIdx = pk.Select(left.Schema.IndexOf).ToArray();
        var rightKeyIdx = pk.Select(right.Schema.IndexOf).ToArray();

        EnsureUniqueKeys(left, leftKeyIdx, leftName);
        EnsureUniqueKeys(right, rightKeyIdx, rightName);
        return (rightIndex, leftKeyIdx, rightKeyIdx);
    }

    private static void EnsureUniqueKeys(TableData data, int[] keyIdx, string sideName)
    {
        var seen = new HashSet<string>();
        object?[]? firstDuplicate = null;
        var extraRows = 0;
        foreach (var row in data.Rows)
        {
            if (seen.Add(KeyOf(row, keyIdx))) continue;
            extraRows++;
            firstDuplicate ??= row;
        }
        if (firstDuplicate is not null)
            throw new DuplicateKeyException(data.Schema.Name, sideName, data.Schema.PrimaryKey, KeyValues(firstDuplicate, keyIdx), extraRows);
    }

    private static string KeyOf(object?[] row, int[] idx) =>
        string.Join('\u001f', idx.Select(i => Convert.ToString(row[i], System.Globalization.CultureInfo.InvariantCulture)));

    internal static object?[] KeyValues(object?[] row, int[] idx) => idx.Select(i => row[i]).ToArray();

    public static bool ValueEquals(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        _ => a.Equals(b),
    };
}

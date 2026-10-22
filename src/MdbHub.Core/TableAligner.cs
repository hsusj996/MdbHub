namespace MdbHub.Core;

public enum AlignKind { Same, Changed, LeftOnly, RightOnly }

/// <summary>One line of a side-by-side view. Both rows are given in the left table's column order.</summary>
/// <param name="Left">Null for <see cref="AlignKind.RightOnly"/>.</param>
/// <param name="Right">Null for <see cref="AlignKind.LeftOnly"/>.</param>
/// <param name="ChangedColumns">For <see cref="AlignKind.Changed"/>: indexes (in the left column order) of the cells that differ.</param>
public sealed record AlignedRow(AlignKind Kind, object?[]? Left, object?[]? Right, IReadOnlyList<int> ChangedColumns);

public static class TableAligner
{
    /// <summary>
    /// Lines two tables up by key, like a merge tool: every key of either side appears once, in key order. A row that exists on
    /// one side only is paired with nothing on the other. Throws <see cref="DuplicateKeyException"/> when keys are not unique.
    /// </summary>
    public static IReadOnlyList<AlignedRow> Align(TableData left, TableData right, string leftName = "왼쪽", string rightName = "오른쪽")
    {
        var (rightIndex, leftKeyIdx, rightKeyIdx) = TableComparer.PrepareKeys(left, right, leftName, rightName);

        var l = Sorted(left.Rows, leftKeyIdx);
        var r = Sorted(right.Rows, rightKeyIdx);
        var result = new List<AlignedRow>(Math.Max(l.Count, r.Count));

        int i = 0, j = 0;
        while (i < l.Count || j < r.Count)
        {
            var order = i >= l.Count ? 1 : j >= r.Count ? -1 : KeyComparer.Instance.Compare(l[i].Key, r[j].Key);
            if (order < 0)
            {
                result.Add(new AlignedRow(AlignKind.LeftOnly, l[i].Row, null, []));
                i++;
            }
            else if (order > 0)
            {
                result.Add(new AlignedRow(AlignKind.RightOnly, null, InLeftOrder(r[j].Row, rightIndex), []));
                j++;
            }
            else
            {
                var leftRow = l[i].Row;
                var rightRow = InLeftOrder(r[j].Row, rightIndex);
                var changed = Enumerable.Range(0, leftRow.Length).Where(c => !TableComparer.ValueEquals(leftRow[c], rightRow[c])).ToList();
                result.Add(new AlignedRow(changed.Count == 0 ? AlignKind.Same : AlignKind.Changed, leftRow, rightRow, changed));
                i++;
                j++;
            }
        }
        return result;
    }

    private static List<(object?[] Key, object?[] Row)> Sorted(IReadOnlyList<object?[]> rows, int[] keyIdx) =>
        rows.Select(row => (Key: TableComparer.KeyValues(row, keyIdx), Row: row))
            .OrderBy(x => x.Key, KeyComparer.Instance)
            .ToList();

    private static object?[] InLeftOrder(object?[] rightRow, int[] rightIndex) =>
        rightIndex.Select(i => rightRow[i]).ToArray();
}

/// <summary>Orders keys component by component: numbers numerically, other equal types naturally, anything else by text.</summary>
internal sealed class KeyComparer : IComparer<object?[]>
{
    public static readonly KeyComparer Instance = new();

    public int Compare(object?[]? x, object?[]? y)
    {
        for (var i = 0; i < x!.Length; i++)
        {
            var c = CompareValue(x[i], y![i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private static int CompareValue(object? a, object? b)
    {
        if (a is null || b is null) return a is null ? (b is null ? 0 : -1) : 1;
        if (IsNumber(a) && IsNumber(b)) return Convert.ToDecimal(a).CompareTo(Convert.ToDecimal(b));
        if (a.GetType() == b.GetType() && a is IComparable comparable) return comparable.CompareTo(b);
        return string.CompareOrdinal(a.ToString(), b.ToString());
    }

    private static bool IsNumber(object v) =>
        v is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
}

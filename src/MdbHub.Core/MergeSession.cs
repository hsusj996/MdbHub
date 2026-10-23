namespace MdbHub.Core;

public enum MergeSide { Left, Right }

public enum RowChangeKind { Insert, Update }

/// <summary>A row that has to be written to one side's file. Rows are in the session schema's (left) column order.</summary>
/// <param name="Original">What the file holds now (null for an insert).</param>
/// <param name="ChangedColumns">Indexes of the columns that differ from <paramref name="Original"/> (all columns for an insert).</param>
public sealed record RowChange(string Table, RowChangeKind Kind, IReadOnlyList<object?> Key, object?[]? Original, object?[] Current, IReadOnlyList<int> ChangedColumns);

/// <summary>One key of a table as it exists on each side. Null rows mean "this side has no such row".</summary>
public sealed class MergeLine
{
    internal MergeLine(IReadOnlyList<object?> key, object?[]? left, object?[]? right, SideRow leftRow, SideRow rightRow, int hunk)
    {
        Key = key;
        LeftOriginal = left;
        RightOriginal = right;
        Left = (object?[]?)left?.Clone();
        Right = (object?[]?)right?.Clone();
        LeftRow = leftRow;
        RightRow = rightRow;
        Hunk = hunk;
    }

    public IReadOnlyList<object?> Key { get; }
    public object?[]? LeftOriginal { get; }
    public object?[]? RightOriginal { get; }

    /// <summary>The row as the left side would hold it after saving.</summary>
    public object?[]? Left { get; internal set; }

    /// <summary>The row as the right side would hold it after saving.</summary>
    public object?[]? Right { get; internal set; }

    public SideRow LeftRow { get; }
    public SideRow RightRow { get; }

    /// <summary>Block of neighbouring differences this line started in (1-based), or 0 if both sides were equal.</summary>
    public int Hunk { get; }
}

/// <summary>
/// The state of merging one table, like a merge tool: both sides are shown, copying a line makes the other side equal to it,
/// values can be edited, and what is written back is whatever differs from what each file holds now.
/// There is no delete: a row that exists on one side only can be copied to the other side, or left alone.
/// </summary>
public sealed class MergeSession
{
    private static readonly IReadOnlySet<int> NoColumns = new HashSet<int>();

    private readonly List<MergeLine> _lines = [];
    private readonly List<(int Start, int End)> _hunks = [];

    private MergeSession(TableSchema schema)
    {
        Schema = schema;
        KeyColumns = schema.PrimaryKey.Select(schema.IndexOf).Where(i => i >= 0).ToHashSet();
    }

    /// <summary>The left table's schema; all rows are in its column order.</summary>
    public TableSchema Schema { get; }

    public IReadOnlySet<int> KeyColumns { get; }
    public IReadOnlyList<MergeLine> Lines => _lines;
    public int HunkCount => _hunks.Count;

    /// <param name="aligned">Lines as produced by <see cref="TableAligner.Align"/> (with their keys).</param>
    public static MergeSession Create(IReadOnlyList<AlignedRow> aligned, TableSchema schema)
    {
        var session = new MergeSession(schema);
        var columns = schema.Columns.Count;
        var hunk = 0;
        var inHunk = false;

        for (var n = 0; n < aligned.Count; n++)
        {
            var row = aligned[n];
            var differs = row.Kind != AlignKind.Same;
            if (differs && !inHunk)
            {
                hunk++;
                session._hunks.Add((n, n));
            }
            else if (differs)
            {
                session._hunks[^1] = (session._hunks[^1].Start, n);
            }
            inHunk = differs;

            var key = row.Key ?? [];
            SideRow Row() => new(n + 1, SideRowKind.Same, new string[columns], NoColumns, key) { LineIndex = n, Hunk = differs ? hunk : 0 };
            var line = new MergeLine(key, row.Left, row.Right, Row(), Row(), differs ? hunk : 0);
            session._lines.Add(line);
            session.Refresh(line);
        }
        return session;
    }

    public IReadOnlyList<int> HunkLines(int hunk) =>
        hunk < 1 || hunk > _hunks.Count ? [] : Enumerable.Range(_hunks[hunk - 1].Start, _hunks[hunk - 1].End - _hunks[hunk - 1].Start + 1).ToList();

    // ---- changing the merge ----

    /// <summary>Makes the right side's row equal to the left's (creating it if missing). False when there is nothing to copy or to change.</summary>
    public bool CopyToRight(int line)
    {
        var l = _lines[line];
        if (l.Left is null || Equal(l.Left, l.Right)) return false;
        l.Right = (object?[])l.Left.Clone();
        Refresh(l);
        return true;
    }

    /// <summary>Makes the left side's row equal to the right's (creating it if missing). False when there is nothing to copy or to change.</summary>
    public bool CopyToLeft(int line)
    {
        var l = _lines[line];
        if (l.Right is null || Equal(l.Right, l.Left)) return false;
        l.Left = (object?[])l.Right.Clone();
        Refresh(l);
        return true;
    }

    /// <returns>How many lines were changed.</returns>
    public int CopyToRight(IEnumerable<int> lines) => lines.Count(CopyToRight);

    /// <returns>How many lines were changed.</returns>
    public int CopyToLeft(IEnumerable<int> lines) => lines.Count(CopyToLeft);

    /// <summary>Sets one value by hand. Key columns and sides without a row cannot be edited.</summary>
    public bool SetValue(MergeSide side, int line, int column, object? value)
    {
        if (KeyColumns.Contains(column)) return false;
        var l = _lines[line];
        var row = side == MergeSide.Left ? l.Left : l.Right;
        if (row is null) return false;

        row[column] = value;
        Refresh(l);
        return true;
    }

    /// <summary>The value the file holds now for a cell (null when the row or the value is missing).</summary>
    public object? OriginalValue(MergeSide side, int line, int column) =>
        (side == MergeSide.Left ? _lines[line].LeftOriginal : _lines[line].RightOriginal)?[column];

    /// <summary>Undoes every copy and edit of one line.</summary>
    public void Revert(int line)
    {
        var l = _lines[line];
        l.Left = (object?[]?)l.LeftOriginal?.Clone();
        l.Right = (object?[]?)l.RightOriginal?.Clone();
        Refresh(l);
    }

    public void RevertAll()
    {
        for (var i = 0; i < _lines.Count; i++)
            if (IsModified(_lines[i])) Revert(i);
    }

    // ---- what would be saved ----

    public int PendingRows(MergeSide side) => _lines.Count(l => IsModified(l, side));

    public (int LeftOnly, int RightOnly, int Changed) CurrentCounts()
    {
        int leftOnly = 0, rightOnly = 0, changed = 0;
        foreach (var l in _lines)
        {
            if (l.Left is not null && l.Right is null) leftOnly++;
            else if (l.Left is null && l.Right is not null) rightOnly++;
            else if (l.Left is not null && !Equal(l.Left, l.Right)) changed++;
        }
        return (leftOnly, rightOnly, changed);
    }

    /// <summary>The rows to write to one side's file so that it ends up as shown: new rows are inserts, edited or overwritten rows are updates.</summary>
    public IReadOnlyList<RowChange> PendingChanges(MergeSide side)
    {
        var changes = new List<RowChange>();
        foreach (var l in _lines)
        {
            var (original, current) = side == MergeSide.Left ? (l.LeftOriginal, l.Left) : (l.RightOriginal, l.Right);
            if (current is null || Equal(original, current)) continue;

            changes.Add(original is null
                ? new RowChange(Schema.Name, RowChangeKind.Insert, l.Key, null, (object?[])current.Clone(), Enumerable.Range(0, current.Length).ToList())
                : new RowChange(Schema.Name, RowChangeKind.Update, l.Key, original, (object?[])current.Clone(), DifferingColumns(original, current).ToList()));
        }
        return changes;
    }

    /// <summary>The lines as two lists for a side-by-side grid. The line objects are shared, so later copies and edits show up in them.</summary>
    public SideBySide CreateView(bool onlyDifferences)
    {
        var left = new List<SideRow>();
        var right = new List<SideRow>();
        var differences = new List<int>();
        var hunkStarts = new List<int>();
        var lastHunk = 0;

        foreach (var l in _lines)
        {
            if (onlyDifferences && l.Hunk == 0) continue;

            var index = left.Count;
            l.LeftRow.ListIndex = l.RightRow.ListIndex = index;
            left.Add(l.LeftRow);
            right.Add(l.RightRow);
            if (l.Hunk > 0)
            {
                differences.Add(index);
                if (l.Hunk != lastHunk) hunkStarts.Add(index);
            }
            lastHunk = l.Hunk;
        }

        var (leftOnly, rightOnly, changed) = CurrentCounts();
        return new SideBySide
        {
            Columns = Schema.Columns.Select(c => c.Name).ToList(),
            Left = left,
            Right = right,
            DifferenceRows = differences,
            HunkStartRows = hunkStarts,
            KeyColumns = KeyColumns,
            TotalLines = _lines.Count,
            LeftOnlyCount = leftOnly,
            RightOnlyCount = rightOnly,
            ChangedCount = changed,
        };
    }

    // ---- keeping the displayed lines in step ----

    private void Refresh(MergeLine l)
    {
        var hasLeft = l.Left is not null;
        var hasRight = l.Right is not null;
        var changed = hasLeft && hasRight ? DifferingColumns(l.Left!, l.Right!).ToHashSet() : NoColumns;
        var kinds = (hasLeft, hasRight) switch
        {
            (true, true) => changed.Count == 0 ? (SideRowKind.Same, SideRowKind.Same) : (SideRowKind.Changed, SideRowKind.Changed),
            (true, false) => (SideRowKind.LeftOnly, SideRowKind.Filler),
            _ => (SideRowKind.Filler, SideRowKind.RightOnly),
        };
        var canCopyToRight = hasLeft && (!hasRight || changed.Count > 0);
        var canCopyToLeft = hasRight && (!hasLeft || changed.Count > 0);

        l.LeftRow.Update(kinds.Item1, Show(l.Left, changed), changed, EditedColumns(l.LeftOriginal, l.Left), IsModified(l, MergeSide.Left), canCopyToRight, canCopyToLeft);
        l.RightRow.Update(kinds.Item2, Show(l.Right, changed), changed, EditedColumns(l.RightOriginal, l.Right), IsModified(l, MergeSide.Right), canCopyToRight, canCopyToLeft);
    }

    private IReadOnlyList<string> Show(object?[]? row, IReadOnlySet<int> changed) =>
        row is null
            ? Enumerable.Repeat("", Schema.Columns.Count).ToList()
            : row.Select((v, i) => SideBySide.FormatValue(v, markNull: changed.Contains(i))).ToList();

    // Cells that differ from what the file holds (a row the file does not have yet has no "original" to compare with).
    private static IEnumerable<int> EditedColumns(object?[]? original, object?[]? current) =>
        original is null || current is null ? [] : DifferingColumns(original, current);

    private static bool IsModified(MergeLine l, MergeSide side) =>
        side == MergeSide.Left ? !Equal(l.LeftOriginal, l.Left) : !Equal(l.RightOriginal, l.Right);

    private static bool IsModified(MergeLine l) => IsModified(l, MergeSide.Left) || IsModified(l, MergeSide.Right);

    private static IEnumerable<int> DifferingColumns(object?[] a, object?[] b) =>
        Enumerable.Range(0, a.Length).Where(i => !TableComparer.ValueEquals(a[i], b[i]));

    private static bool Equal(object?[]? a, object?[]? b)
    {
        if (a is null || b is null) return a is null && b is null;
        for (var i = 0; i < a.Length; i++)
            if (!TableComparer.ValueEquals(a[i], b[i])) return false;
        return true;
    }
}

using System.Globalization;

namespace MdbHub.Core;

public enum SideRowKind
{
    Same,
    Changed,
    /// <summary>Row exists on the left only (shown on the left; the right has a <see cref="Filler"/>).</summary>
    LeftOnly,
    /// <summary>Row exists on the right only (shown on the right; the left has a <see cref="Filler"/>).</summary>
    RightOnly,
    /// <summary>Blank line standing in for a row the other side has.</summary>
    Filler,
}

/// <param name="Number">1-based position among all aligned lines, so a line keeps its number when only differences are shown.</param>
/// <param name="ChangedColumns">Indexes of the cells that differ (only for <see cref="SideRowKind.Changed"/>).</param>
public sealed class SideRow(int number, SideRowKind kind, string[] cells, IReadOnlySet<int> changedColumns, IReadOnlyList<object?> key)
    : System.ComponentModel.INotifyPropertyChanged
{
    private SideRowKind _kind = kind;
    private IReadOnlySet<int> _changedColumns = changedColumns;
    private bool _canCopyToRight;
    private bool _canCopyToLeft;
    private bool _isPending;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public int Number { get; } = number;

    /// <summary>What this side's line currently is. Lines of a merge change kind as differences are resolved.</summary>
    public SideRowKind Kind => _kind;

    public string[] Cells { get; } = cells;
    public IReadOnlySet<int> ChangedColumns => _changedColumns;

    /// <summary>The row's key values (empty when unknown).</summary>
    public IReadOnlyList<object?> Key { get; } = key;

    /// <summary>
    /// Cells whose value was changed from what the file holds: edited by hand or overwritten by a copy from the other side
    /// (in the apply preview: edited by hand).
    /// </summary>
    public HashSet<int> EditedColumns { get; } = [];

    /// <summary>Position in the displayed list; the same index on both sides refers to the same line.</summary>
    public int ListIndex { get; internal set; }

    /// <summary>Position in the merge session's full list of lines (0-based); unused outside merges.</summary>
    public int LineIndex { get; internal set; }

    /// <summary>Number of the block of neighbouring differences this line belongs to (1-based), or 0 when the line never differed.</summary>
    public int Hunk { get; internal set; }

    /// <summary>The other side has a different (or no) version of this row, which copying here to there would replace (or create).</summary>
    public bool CanCopyToRight => _canCopyToRight;

    public bool CanCopyToLeft => _canCopyToLeft;

    /// <summary>This side's row will be written when the merge is saved.</summary>
    public bool IsPending => _isPending;

    public string PendingMark => _isPending ? "●" : "";

    /// <summary>Tells the grid that <see cref="Cells"/> changed so the shown text is refreshed.</summary>
    public void RefreshCells() => Raise(nameof(Cells));

    internal void Update(SideRowKind kind, IReadOnlyList<string> cells, IReadOnlySet<int> changed, IEnumerable<int> edited, bool pending, bool canCopyRight, bool canCopyLeft)
    {
        (_kind, _changedColumns, _isPending, _canCopyToRight, _canCopyToLeft) = (kind, changed, pending, canCopyRight, canCopyLeft);
        for (var i = 0; i < Cells.Length; i++) Cells[i] = cells[i];
        EditedColumns.Clear();
        EditedColumns.UnionWith(edited);

        foreach (var name in new[] { nameof(Kind), nameof(Cells), nameof(IsPending), nameof(PendingMark), nameof(CanCopyToRight), nameof(CanCopyToLeft) })
            Raise(name);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>Two equally long lists of lines: line i on the left corresponds to line i on the right.</summary>
public sealed class SideBySide
{
    private static readonly IReadOnlySet<int> NoColumns = new HashSet<int>();

    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<SideRow> Left { get; init; }
    public required IReadOnlyList<SideRow> Right { get; init; }

    /// <summary>List indexes of the lines that are not <see cref="SideRowKind.Same"/>, in order.</summary>
    public required IReadOnlyList<int> DifferenceRows { get; init; }

    /// <summary>Indexes of the key columns; editing a key would change which row is meant.</summary>
    public IReadOnlySet<int> KeyColumns { get; init; } = new HashSet<int>();

    /// <summary>List indexes where a block of neighbouring differences (a hunk) starts, in order (merge views only).</summary>
    public IReadOnlyList<int> HunkStartRows { get; init; } = [];

    public int TotalLines { get; init; }
    public int LeftOnlyCount { get; init; }
    public int RightOnlyCount { get; init; }
    public int ChangedCount { get; init; }

    public static SideBySide Build(IReadOnlyList<AlignedRow> aligned, TableSchema leftSchema, bool onlyDifferences)
    {
        var blank = new string[leftSchema.Columns.Count];
        Array.Fill(blank, "");

        var left = new List<SideRow>();
        var right = new List<SideRow>();
        var differences = new List<int>();
        int leftOnly = 0, rightOnly = 0, changed = 0;

        for (var n = 0; n < aligned.Count; n++)
        {
            var row = aligned[n];
            if (row.Kind == AlignKind.Same && onlyDifferences) continue;

            var index = left.Count;
            var key = row.Key ?? [];
            switch (row.Kind)
            {
                case AlignKind.Same:
                    left.Add(new SideRow(n + 1, SideRowKind.Same, Format(row.Left!, NoColumns), NoColumns, key));
                    right.Add(new SideRow(n + 1, SideRowKind.Same, Format(row.Right!, NoColumns), NoColumns, key));
                    break;
                case AlignKind.Changed:
                    var cols = row.ChangedColumns.ToHashSet();
                    left.Add(new SideRow(n + 1, SideRowKind.Changed, Format(row.Left!, cols), cols, key));
                    right.Add(new SideRow(n + 1, SideRowKind.Changed, Format(row.Right!, cols), cols, key));
                    changed++;
                    break;
                case AlignKind.LeftOnly:
                    left.Add(new SideRow(n + 1, SideRowKind.LeftOnly, Format(row.Left!, NoColumns), NoColumns, key));
                    right.Add(new SideRow(n + 1, SideRowKind.Filler, (string[])blank.Clone(), NoColumns, key));
                    leftOnly++;
                    break;
                default:
                    left.Add(new SideRow(n + 1, SideRowKind.Filler, (string[])blank.Clone(), NoColumns, key));
                    right.Add(new SideRow(n + 1, SideRowKind.RightOnly, Format(row.Right!, NoColumns), NoColumns, key));
                    rightOnly++;
                    break;
            }

            left[index].ListIndex = right[index].ListIndex = index;
            if (row.Kind != AlignKind.Same) differences.Add(index);
        }

        return new SideBySide
        {
            Columns = leftSchema.Columns.Select(c => c.Name).ToList(),
            Left = left,
            Right = right,
            DifferenceRows = differences,
            KeyColumns = leftSchema.PrimaryKey.Select(leftSchema.IndexOf).Where(i => i >= 0).ToHashSet(),
            TotalLines = aligned.Count,
            LeftOnlyCount = leftOnly,
            RightOnlyCount = rightOnly,
            ChangedCount = changed,
        };
    }

    // A differing null reads "(null)" so it can be told from an empty text; elsewhere null is just blank.
    private static string[] Format(object?[] row, IReadOnlySet<int> changed)
    {
        var cells = new string[row.Length];
        for (var i = 0; i < row.Length; i++) cells[i] = FormatValue(row[i], changed.Contains(i));
        return cells;
    }

    /// <summary>The text a cell shows for a value (<paramref name="markNull"/>: show null as "(null)" instead of blank).</summary>
    public static string FormatValue(object? value, bool markNull = false) => value switch
    {
        null => markNull ? CellValueParser.NullText : "",
        byte[] bytes => $"({bytes.Length} bytes)",
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        var v => v.ToString() ?? "",
    };
}

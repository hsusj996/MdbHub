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
public sealed class SideRow(int number, SideRowKind kind, string[] cells, IReadOnlySet<int> changedColumns)
{
    public int Number { get; } = number;
    public SideRowKind Kind { get; } = kind;
    public string[] Cells { get; } = cells;
    public IReadOnlySet<int> ChangedColumns { get; } = changedColumns;

    /// <summary>Position in the displayed list; the same index on both sides refers to the same line.</summary>
    public int ListIndex { get; internal set; }
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
            switch (row.Kind)
            {
                case AlignKind.Same:
                    left.Add(new SideRow(n + 1, SideRowKind.Same, Format(row.Left!, NoColumns), NoColumns));
                    right.Add(new SideRow(n + 1, SideRowKind.Same, Format(row.Right!, NoColumns), NoColumns));
                    break;
                case AlignKind.Changed:
                    var cols = row.ChangedColumns.ToHashSet();
                    left.Add(new SideRow(n + 1, SideRowKind.Changed, Format(row.Left!, cols), cols));
                    right.Add(new SideRow(n + 1, SideRowKind.Changed, Format(row.Right!, cols), cols));
                    changed++;
                    break;
                case AlignKind.LeftOnly:
                    left.Add(new SideRow(n + 1, SideRowKind.LeftOnly, Format(row.Left!, NoColumns), NoColumns));
                    right.Add(new SideRow(n + 1, SideRowKind.Filler, blank, NoColumns));
                    leftOnly++;
                    break;
                default:
                    left.Add(new SideRow(n + 1, SideRowKind.Filler, blank, NoColumns));
                    right.Add(new SideRow(n + 1, SideRowKind.RightOnly, Format(row.Right!, NoColumns), NoColumns));
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
        for (var i = 0; i < row.Length; i++)
        {
            cells[i] = row[i] switch
            {
                null => changed.Contains(i) ? "(null)" : "",
                byte[] bytes => $"({bytes.Length} bytes)",
                IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
                var v => v.ToString() ?? "",
            };
        }
        return cells;
    }
}

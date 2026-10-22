using MdbHub.Core;

namespace MdbHub.Tests;

public class CompareCoreTests
{
    private static TableSchema Table(string name, params string[] columns) =>
        new(name, columns.Select(c => new ColumnInfo(c, "Integer")).ToList(), [columns[0]]);

    private static TableData Data(TableSchema schema, params object?[][] rows) => new(schema, rows);

    [Fact]
    public void TableMatcher_PairsCommonTablesAndReportsOneSidedOnes()
    {
        var left = new DatabaseSchema([Table("A", "K", "V"), Table("B", "K", "V"), Table("ONLY_L", "K")]);
        var right = new DatabaseSchema([Table("b", "K", "V"), Table("a", "K", "V"), Table("ONLY_R", "K")]);

        var matches = TableMatcher.Match(left, right);

        Assert.Equal(["A", "B", "ONLY_L", "ONLY_R"], matches.Select(m => m.Name));   // left order, then right-only
        Assert.Equal([TableMatchKind.Both, TableMatchKind.Both, TableMatchKind.LeftOnly, TableMatchKind.RightOnly], matches.Select(m => m.Kind));
        Assert.All(matches.Where(m => m.IsCommon), m => Assert.Empty(m.SchemaDifferences));
        Assert.Null(matches.Single(m => m.Name == "ONLY_L").Right);
        Assert.Null(matches.Single(m => m.Name == "ONLY_R").Left);
    }

    [Fact]
    public void TableMatcher_CommonTableWithDifferentColumns_IsStillCommonButReportsTheDifference()
    {
        var left = new DatabaseSchema([Table("A", "K", "V")]);
        var right = new DatabaseSchema([Table("A", "K", "V", "EXTRA")]);

        var match = TableMatcher.Match(left, right).Single();

        Assert.True(match.IsCommon);
        Assert.Contains(match.SchemaDifferences, d => d.Contains("EXTRA"));
        Assert.DoesNotContain(match.SchemaDifferences, d => d.Contains("테이블 'A'"));   // no "table missing" noise
    }

    [Fact]
    public void TableMatcher_NoCommonTables_HasNoCommonMatch()
    {
        var matches = TableMatcher.Match(new DatabaseSchema([Table("A", "K")]), new DatabaseSchema([Table("B", "K")]));

        Assert.DoesNotContain(matches, m => m.IsCommon);
    }

    [Fact]
    public void TableAligner_LinesUpByKey_InKeyOrder_WithGapsForOneSidedRows()
    {
        var s = Table("T", "K", "V");
        var left = Data(s, [3, "c"], [1, "a"], [2, "b"], [5, "e"]);
        var right = Data(s, [2, "b"], [3, "X"], [4, "d"], [1, "a"]);

        var rows = TableAligner.Align(left, right);

        Assert.Equal([1, 2, 3, 4, 5], rows.Select(r => (int)(r.Left ?? r.Right)![0]!));
        Assert.Equal([AlignKind.Same, AlignKind.Same, AlignKind.Changed, AlignKind.RightOnly, AlignKind.LeftOnly], rows.Select(r => r.Kind));
        Assert.Null(rows[3].Left);
        Assert.Null(rows[4].Right);
        Assert.Equal([1], rows[2].ChangedColumns);   // the V column
    }

    [Fact]
    public void TableAligner_GivesRightRowsInLeftColumnOrder()
    {
        var left = Data(new TableSchema("T", [new("K", "Integer"), new("A", "Integer"), new("B", "Integer")], ["K"]), [1, 10, 20]);
        var right = Data(new TableSchema("T", [new("B", "Integer"), new("K", "Integer"), new("A", "Integer")], ["K"]), [20, 1, 10]);

        var row = TableAligner.Align(left, right).Single();

        Assert.Equal(AlignKind.Same, row.Kind);
        Assert.Equal([1, 10, 20], row.Right);
    }

    [Fact]
    public void TableAligner_SortsNumericKeysNumerically_NotAsText()
    {
        var s = Table("T", "K");
        var rows = TableAligner.Align(Data(s, [10], [9], [100]), Data(s));

        Assert.Equal([9, 10, 100], rows.Select(r => (int)r.Left![0]!));
    }

    [Fact]
    public void TableAligner_CompositeKey_MatchesOnAllKeyColumns()
    {
        var s = new TableSchema("T", [new("A", "Integer"), new("B", "Integer"), new("V", "Integer")], ["A", "B"]);
        var left = Data(s, [1, 1, 0], [1, 2, 0]);
        var right = Data(s, [1, 2, 0], [2, 1, 0]);

        var rows = TableAligner.Align(left, right);

        Assert.Equal([AlignKind.LeftOnly, AlignKind.Same, AlignKind.RightOnly], rows.Select(r => r.Kind));
    }

    [Fact]
    public void TableAligner_DuplicateKeys_Throws()
    {
        var s = Table("T", "K", "V");
        Assert.Throws<DuplicateKeyException>(() => TableAligner.Align(Data(s, [1, "a"], [1, "b"]), Data(s)));
    }

    [Fact]
    public void SideBySide_PutsFillersOppositeOneSidedRows_AndMarksChangedCells()
    {
        var s = Table("T", "K", "V");
        var aligned = TableAligner.Align(
            Data(s, [1, "a"], [2, "b"], [3, null], [5, "e"]),
            Data(s, [1, "a"], [2, "X"], [3, "z"], [4, "d"]));

        var view = SideBySide.Build(aligned, s, onlyDifferences: false);

        Assert.Equal(5, view.Left.Count);
        Assert.Equal(view.Left.Count, view.Right.Count);
        Assert.Equal([SideRowKind.Same, SideRowKind.Changed, SideRowKind.Changed, SideRowKind.Filler, SideRowKind.LeftOnly], view.Left.Select(r => r.Kind));
        Assert.Equal([SideRowKind.Same, SideRowKind.Changed, SideRowKind.Changed, SideRowKind.RightOnly, SideRowKind.Filler], view.Right.Select(r => r.Kind));
        Assert.Equal(["2", "b"], view.Left[1].Cells);
        Assert.Equal(["2", "X"], view.Right[1].Cells);
        Assert.Equal([1], view.Left[1].ChangedColumns);
        Assert.Equal("(null)", view.Left[2].Cells[1]);          // null vs "z" is a visible difference
        Assert.Equal(["", ""], view.Left[3].Cells);              // filler
        Assert.Equal([1, 2, 3, 4], view.DifferenceRows);
        Assert.Equal((1, 1, 2), (view.LeftOnlyCount, view.RightOnlyCount, view.ChangedCount));
        Assert.All(view.Left, r => Assert.Equal(r.ListIndex, view.Right[r.ListIndex].ListIndex));
    }

    [Fact]
    public void SideBySide_OnlyDifferences_DropsSameLinesButKeepsOriginalNumbers()
    {
        var s = Table("T", "K", "V");
        var aligned = TableAligner.Align(
            Data(s, [1, "a"], [2, "b"], [3, "c"]),
            Data(s, [1, "a"], [2, "b"], [3, "X"]));

        var view = SideBySide.Build(aligned, s, onlyDifferences: true);

        Assert.Single(view.Left);
        Assert.Equal(3, view.Left[0].Number);   // the 3rd line of the full view
        Assert.Equal(0, view.Left[0].ListIndex);
        Assert.Equal([0], view.DifferenceRows);
        Assert.Equal(3, view.TotalLines);
    }

    [Fact]
    public void TableAligner_AgreesWithTableComparerOnCounts()
    {
        var s = Table("T", "K", "V");
        var left = Data(s, [1, "a"], [2, "b"], [3, "c"], [4, "d"]);
        var right = Data(s, [2, "b"], [3, "X"], [4, "d"], [5, "e"], [6, "f"]);

        var aligned = TableAligner.Align(left, right);
        var diff = TableComparer.Compare(left, right);

        Assert.Equal(diff.UnchangedCount, aligned.Count(r => r.Kind == AlignKind.Same));
        Assert.Equal(diff.Rows.Count(r => r.Kind == RowDiffKind.Changed), aligned.Count(r => r.Kind == AlignKind.Changed));
        Assert.Equal(diff.Rows.Count(r => r.Kind == RowDiffKind.Removed), aligned.Count(r => r.Kind == AlignKind.LeftOnly));
        Assert.Equal(diff.Rows.Count(r => r.Kind == RowDiffKind.Added), aligned.Count(r => r.Kind == AlignKind.RightOnly));
    }
}

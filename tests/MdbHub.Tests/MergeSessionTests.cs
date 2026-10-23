using MdbHub.Core;

namespace MdbHub.Tests;

public class MergeSessionTests
{
    private static readonly TableSchema Schema = new("T", [new("K", "Integer"), new("V", "VarWChar")], ["K"]);

    private static TableData Data(params object?[][] rows) => new(Schema, rows);

    // keys: 1 same | 2 changed, 3 changed (hunk 1) | 4 same | 5 left only, 6 right only (hunk 2)
    private static MergeSession NewSession()
    {
        var left = Data([1, "a"], [2, "b"], [3, "c"], [4, "d"], [5, "e"]);
        var right = Data([1, "a"], [2, "X"], [3, "Y"], [4, "d"], [6, "f"]);
        return MergeSession.Create(TableAligner.Align(left, right), Schema);
    }

    [Fact]
    public void Create_GroupsNeighbouringDifferencesIntoHunks()
    {
        var s = NewSession();

        Assert.Equal(6, s.Lines.Count);
        Assert.Equal(2, s.HunkCount);
        Assert.Equal([1, 2], s.HunkLines(1));
        Assert.Equal([4, 5], s.HunkLines(2));
        Assert.Equal([0, 1, 1, 0, 2, 2], s.Lines.Select(l => l.Hunk));
        Assert.Equal((1, 1, 2), s.CurrentCounts());
        Assert.Equal(0, s.PendingRows(MergeSide.Left) + s.PendingRows(MergeSide.Right));
    }

    [Fact]
    public void CopyToRight_MakesTheRightRowsEqual_AndOnlyTheRightIsPending()
    {
        var s = NewSession();

        var copied = s.CopyToRight(s.HunkLines(1));

        Assert.Equal(2, copied);
        Assert.Equal([SideRowKind.Same, SideRowKind.Same], [s.Lines[1].LeftRow.Kind, s.Lines[1].RightRow.Kind]);
        Assert.Equal("b", s.Lines[1].RightRow.Cells[1]);
        Assert.Equal(0, s.PendingRows(MergeSide.Left));
        Assert.Equal(2, s.PendingRows(MergeSide.Right));
        Assert.All(s.PendingChanges(MergeSide.Right), c => Assert.Equal(RowChangeKind.Update, c.Kind));
        Assert.Equal([1], s.PendingChanges(MergeSide.Right)[0].ChangedColumns);          // only V differs
        Assert.Equal("X", s.PendingChanges(MergeSide.Right)[0].Original![1]);
        Assert.Equal("b", s.PendingChanges(MergeSide.Right)[0].Current[1]);
        Assert.Equal([1], s.Lines[1].RightRow.EditedColumns);                             // the right cell now differs from its file
        Assert.Empty(s.Lines[1].LeftRow.EditedColumns);
        Assert.True(s.Lines[1].RightRow.IsPending);
        Assert.False(s.Lines[1].LeftRow.IsPending);
        Assert.False(s.Lines[1].LeftRow.CanCopyToRight);                                  // nothing left to resolve
    }

    [Fact]
    public void RowsOnOneSideOnly_CanOnlyBeCopiedToTheOtherSide_NeverDeleted()
    {
        var s = NewSession();

        Assert.True(s.Lines[4].LeftRow.CanCopyToRight);     // key 5 exists on the left only
        Assert.False(s.Lines[4].LeftRow.CanCopyToLeft);
        Assert.False(s.Lines[5].LeftRow.CanCopyToRight);    // key 6 exists on the right only
        Assert.True(s.Lines[5].LeftRow.CanCopyToLeft);

        Assert.False(s.CopyToLeft(4));                      // would mean deleting the left row 5
        Assert.False(s.CopyToRight(5));                     // would mean deleting the right row 6
        Assert.True(s.CopyToRight(4));
        Assert.True(s.CopyToLeft(5));

        var right = s.PendingChanges(MergeSide.Right).Single();
        Assert.Equal(RowChangeKind.Insert, right.Kind);
        Assert.Equal([5, "e"], right.Current);
        Assert.Null(right.Original);
        var left = s.PendingChanges(MergeSide.Left).Single();
        Assert.Equal(RowChangeKind.Insert, left.Kind);
        Assert.Equal([6, "f"], left.Current);
        Assert.Equal((0, 0, 0 + 2), s.CurrentCounts());   // only the two changed rows from hunk 1 remain
    }

    [Fact]
    public void BulkCopy_SkipsLinesItCannotCopy_AndReportsHowManyChanged()
    {
        var s = NewSession();

        Assert.Equal(3, s.CopyToRight(Enumerable.Range(0, s.Lines.Count)));   // keys 2, 3 and 5; key 6 has nothing to copy, 1 and 4 are equal
        Assert.Equal(0, s.CopyToRight(Enumerable.Range(0, s.Lines.Count)));   // idempotent
        Assert.Equal(1, s.CopyToLeft(Enumerable.Range(0, s.Lines.Count)));    // key 6: the only line the right side has and the left lacks
    }

    [Fact]
    public void SetValue_EditsOneSide_ButNotKeysNorMissingRows()
    {
        var s = NewSession();

        Assert.True(s.SetValue(MergeSide.Left, 0, 1, "ZZ"));
        Assert.Equal("ZZ", s.Lines[0].LeftRow.Cells[1]);
        Assert.Equal(SideRowKind.Changed, s.Lines[0].LeftRow.Kind);                   // no longer equal to the right
        Assert.Equal([1], s.Lines[0].LeftRow.ChangedColumns);
        Assert.Equal(1, s.PendingRows(MergeSide.Left));
        Assert.Equal(0, s.PendingRows(MergeSide.Right));

        Assert.False(s.SetValue(MergeSide.Left, 0, 0, 99));                           // key column
        Assert.False(s.SetValue(MergeSide.Right, 4, 1, "x"));                         // the right side has no row 5
        Assert.Equal("a", s.OriginalValue(MergeSide.Left, 0, 1));                     // original stays available for validation
    }

    [Fact]
    public void Edits_AreCopiedAlongLikeAnyOtherValue()
    {
        var s = NewSession();
        s.SetValue(MergeSide.Left, 1, 1, "edited");   // key 2 on the left: b -> edited

        s.CopyToRight(1);

        Assert.Equal("edited", s.Lines[1].RightRow.Cells[1]);
        Assert.Equal("edited", s.PendingChanges(MergeSide.Right).Single().Current[1]);
        Assert.Equal(2, s.PendingRows(MergeSide.Left) + s.PendingRows(MergeSide.Right));   // left edited, right overwritten
    }

    [Fact]
    public void Revert_UndoesCopiesAndEdits_OfOneLineOrAll()
    {
        var s = NewSession();
        s.CopyToRight(s.HunkLines(1));
        s.CopyToRight(4);
        s.SetValue(MergeSide.Left, 0, 1, "zz");

        s.Revert(1);
        Assert.Equal("X", s.Lines[1].RightRow.Cells[1]);
        Assert.Equal(SideRowKind.Changed, s.Lines[1].LeftRow.Kind);
        Assert.Equal(3, s.PendingRows(MergeSide.Left) + s.PendingRows(MergeSide.Right));   // line 2 copy, line 5 insert, line 1 edit

        s.RevertAll();
        Assert.Equal(0, s.PendingRows(MergeSide.Left) + s.PendingRows(MergeSide.Right));
        Assert.Equal((1, 1, 2), s.CurrentCounts());
        Assert.Equal(["a"], [s.Lines[0].LeftRow.Cells[1]]);
    }

    [Fact]
    public void CreateView_SharesLinesWithTheSession_AndCanShowOnlyDifferences()
    {
        var s = NewSession();
        var all = s.CreateView(onlyDifferences: false);
        var only = s.CreateView(onlyDifferences: true);

        Assert.Equal(6, all.Left.Count);
        Assert.Equal([1, 2, 4, 5], all.DifferenceRows);
        Assert.Equal([1, 4], all.HunkStartRows);
        Assert.Equal(4, only.Left.Count);
        Assert.Equal([0, 2], only.HunkStartRows);
        Assert.Equal([2, 3, 5, 6], only.Left.Select(r => r.Number));       // original line numbers survive the filter
        Assert.Equal([0, 1, 2, 3], only.Left.Select(r => r.ListIndex));

        s.CopyToRight(1);
        Assert.Equal(SideRowKind.Same, only.Right[0].Kind);               // the earlier view sees the change: the rows are shared
        Assert.Equal([0], all.KeyColumns);   // the key column (K) is the first column
    }

    [Fact]
    public void ChangedRowsKeepTheirHunkAfterBeingResolved_SoTheyCanStillBeFound()
    {
        var s = NewSession();
        s.CopyToRight(s.HunkLines(1));

        var view = s.CreateView(onlyDifferences: true);

        Assert.Equal(4, view.Left.Count);            // resolved lines stay listed (so they can be reviewed or undone)
        Assert.Equal(2, view.HunkStartRows.Count);
    }
}

using MdbHub.Core;
using MdbHub.Data;

namespace MdbHub.Tests;

/// <summary>Saving a merge to real files. Needs the 64-bit Access Database Engine (ACE).</summary>
public class MergeSaveTests : TempDirTestBase
{
    private const string Table = "TB_SPEC_01";
    private const int NameColumn = 3;   // ITEM_NAME

    // left: 100/1 and 200/1 (3 rows each); right: 100/1 and 300/1; right's 100/1 NUM 2 has another ITEM_NAME.
    // sorted by key: 100/1/2 differs (hunk 1); 200/1/* only left + 300/1/* only right (hunk 2)
    private void CreateFiles()
    {
        SampleMdbBuilder.Create(P("left.mdb"), [(100, 1), (200, 1)], rowsPerChemical: 3);
        SampleMdbBuilder.Create(P("right.mdb"), [(100, 1), (300, 1)], rowsPerChemical: 3);
        Sql(P("right.mdb"), "UPDATE TB_SPEC_01 SET ITEM_NAME = 'R_NAME' WHERE CHEMICAL_NO = 100 AND NUM = 2");
    }

    private static void Sql(string path, string sql)
    {
        using var conn = MdbFile.Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private (MergeSession Session, TableSchema Schema) NewSession()
    {
        var left = MdbReader.ReadSchema(P("left.mdb")).Find(Table)!;
        var right = MdbReader.ReadSchema(P("right.mdb")).Find(Table)!;
        var aligned = TableAligner.Align(MdbReader.ReadTable(P("left.mdb"), left), MdbReader.ReadTable(P("right.mdb"), right));
        return (MergeSession.Create(aligned, left), left);
    }

    private IReadOnlyList<MergeFileChanges> Changes(MergeSession session, TableSchema schema) =>
    [
        new(P("left.mdb"), [new MergeTableChanges(schema, session.PendingChanges(MergeSide.Left))]),
        new(P("right.mdb"), [new MergeTableChanges(schema, session.PendingChanges(MergeSide.Right))]),
    ];

    private static List<object?[]> Rows(string path) =>
        MdbReader.ReadTable(path, MdbReader.ReadSchema(path).Find(Table)!).Rows.ToList();

    [Fact]
    public void Save_WritesBothDirections_WithBackupsAndLogs_AndLeavesEverythingElseAlone()
    {
        CreateFiles();
        var (session, schema) = NewSession();
        Assert.Equal(2, session.HunkCount);

        session.CopyToRight(session.HunkLines(1));                                     // 100/1/2: right gets the left's name
        session.CopyToRight(session.HunkLines(2));                                     // 200/1/* are created on the right
        session.CopyToLeft(session.HunkLines(2));                                      // 300/1/* are created on the left
        var firstLine = session.Lines.Select((l, i) => (l, i)).First(x => (int)x.l.Key[2]! == 1 && (int)x.l.Key[0]! == 100).i;
        session.SetValue(MergeSide.Left, firstLine, NameColumn, "EDITED_LEFT");        // and a hand edit on the left

        var results = MdbMerger.Save(Changes(session, schema), new DateTime(2026, 10, 23, 9, 30, 0));

        Assert.Equal(2, results.Count);
        var left = Rows(P("left.mdb"));
        var right = Rows(P("right.mdb"));
        Assert.Equal(9, left.Count);                                                    // 100/1 + 200/1 + 300/1, three rows each
        Assert.Equal(9, right.Count);
        Assert.Equal("EDITED_LEFT", left.Single(r => (int)r[0]! == 100 && (int)r[2]! == 1)[NameColumn]);
        Assert.Equal("ITEM_01_2", right.Single(r => (int)r[0]! == 100 && (int)r[2]! == 2)[NameColumn]);   // overwritten from the left
        Assert.Equal(3, right.Count(r => (int)r[0]! == 200));
        Assert.Equal(3, left.Count(r => (int)r[0]! == 300));

        // untouched tables are identical to before
        var other = MdbReader.ReadSchema(P("left.mdb")).Find("TB_SPEC_02")!;
        Assert.Equal(6, MdbReader.ReadTable(P("left.mdb"), other).Rows.Count);

        foreach (var r in results)
        {
            Assert.True(File.Exists(r.BackupPath));
            Assert.True(File.Exists(r.LogPath));
            Assert.Contains("20261023_093000", r.BackupPath);
        }
        var leftResult = results.Single(r => r.Path == P("left.mdb"));
        var rightResult = results.Single(r => r.Path == P("right.mdb"));
        Assert.Equal((3, 1), (leftResult.Inserts, leftResult.Updates));                 // 3 created from the right; the hand edit
        Assert.Equal((3, 1), (rightResult.Inserts, rightResult.Updates));
        Assert.Equal(6, Rows(leftResult.BackupPath).Count);                             // the backup still holds the old state (the saved file has 9)

        var log = File.ReadAllLines(leftResult.LogPath);
        Assert.Equal("테이블,구분,키,컬럼,이전 값,새 값", log[0].TrimStart('﻿'));
        Assert.Contains(log, l => l.Contains("수정") && l.Contains("ITEM_NAME") && l.Contains("EDITED_LEFT"));
        Assert.Equal(3, log.Count(l => l.Contains("삽입")));
    }

    [Fact]
    public void Save_WhenOnlyOneSideChanges_LeavesTheOtherFileAndItsBackupUntouched()
    {
        CreateFiles();
        var (session, schema) = NewSession();
        session.CopyToRight(session.HunkLines(1));                                     // right only

        var results = MdbMerger.Save(Changes(session, schema), DateTime.Now);

        var only = Assert.Single(results);
        Assert.Equal(P("right.mdb"), only.Path);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(P("x"))!, "left.*.bak.mdb"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(P("x"))!, "left.*.merge.csv"));
    }

    [Fact]
    public void Save_WithNothingPending_DoesNothing()
    {
        CreateFiles();
        var (session, schema) = NewSession();

        Assert.Empty(MdbMerger.Save(Changes(session, schema), DateTime.Now));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(P("x"))!, "*.bak.mdb"));
    }

    [Fact]
    public void Save_WhenAFileChangedSinceTheComparison_WritesNothingAndMakesNoBackup()
    {
        CreateFiles();
        var (session, schema) = NewSession();
        session.CopyToLeft(session.HunkLines(2));                                      // left gets 300/1 rows
        session.CopyToRight(session.HunkLines(1));                                     // right row 100/1/2 will be overwritten
        Sql(P("right.mdb"), "UPDATE TB_SPEC_01 SET ITEM_NAME = 'CHANGED_BY_SOMEONE' WHERE CHEMICAL_NO = 100 AND NUM = 2");   // after the comparison

        var ex = Assert.Throws<InvalidOperationException>(() => MdbMerger.Save(Changes(session, schema), DateTime.Now));

        Assert.Contains("다시 비교", ex.Message);
        Assert.Equal(6, Rows(P("left.mdb")).Count);                                    // the left file was not written either
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(P("x"))!, "*.bak.mdb"));
    }

    [Fact]
    public void Save_WhenAnInsertedKeyAppearedMeanwhile_IsRejected()
    {
        CreateFiles();
        var (session, schema) = NewSession();
        session.CopyToRight(session.HunkLines(2));                                     // would create 200/1/* on the right
        Sql(P("right.mdb"), "INSERT INTO TB_SPEC_01 VALUES (200, 1, 1, 'x', 1, #2025-01-01#, True)");

        var ex = Assert.Throws<InvalidOperationException>(() => MdbMerger.Save(Changes(session, schema), DateTime.Now));

        Assert.Contains("같은 키", ex.Message);
    }

    [Fact]
    public void Save_WhenTheSecondFileFails_PutsTheFirstFileBackFromItsBackup()
    {
        CreateFiles();
        var (session, schema) = NewSession();
        session.CopyToLeft(session.HunkLines(2));                                      // left is written first and succeeds
        var line = session.Lines.Select((l, i) => (l, i)).First(x => (int)x.l.Key[0]! == 100 && (int)x.l.Key[2]! == 1).i;
        session.SetValue(MergeSide.Right, line, NameColumn, new string('x', 300));     // too long for TEXT(50): the right write fails

        var ex = Assert.Throws<InvalidOperationException>(() => MdbMerger.Save(Changes(session, schema), DateTime.Now));

        Assert.Contains("백업으로 되돌렸습니다", ex.Message);
        Assert.Equal(6, Rows(P("left.mdb")).Count);                                    // left is back to its 6 original rows
        Assert.Equal(6, Rows(P("right.mdb")).Count);
        Assert.DoesNotContain(Rows(P("right.mdb")), r => r[NameColumn] is string s && s.Length > 100);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(P("x"))!, "*.merge.csv"));   // no log for a save that did not happen
    }
}

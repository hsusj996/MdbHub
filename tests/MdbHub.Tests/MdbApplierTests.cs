using MdbHub.Core;
using MdbHub.Data;

namespace MdbHub.Tests;

/// <summary>Needs the 64-bit Access Database Engine (ACE) installed.</summary>
public class MdbApplierTests : TempDirTestBase
{
    private static readonly (int, int)[] TargetChemicals = [(100, 1), (100, 2), (200, 1)];
    private static readonly (int, int)[] PatchChemicals = [(100, 1), (300, 1)];   // (100,1) overwrites, (300,1) is new
    private const int Rows = 5;

    private void CreateTargetAndPatch()
    {
        SampleMdbBuilder.Create(P("target.mdb"), TargetChemicals, Rows);
        SampleMdbBuilder.Create(P("patch.mdb"), PatchChemicals, Rows, valueOffset: 5);
    }

    private static IReadOnlyList<object?[]> Rows_(string path, string table, int chemical, int sub)
    {
        var schema = MdbReader.ReadSchema(path).Find(table)!;
        return MdbReader.ReadTable(path, schema, [new ColumnFilter("CHEMICAL_NO", [chemical]), new ColumnFilter("SUB_CHEMICAL_NO", [sub])]).Rows;
    }

    private static int Count(string path, string table) =>
        MdbReader.ReadTable(path, MdbReader.ReadSchema(path).Find(table)!).Rows.Count;

    [Fact]
    public void Preview_CountsInsertsAndUpdatesPerTable_WithoutChangingFiles()
    {
        CreateTargetAndPatch();
        var info = new FileInfo(P("target.mdb"));
        var (length, writtenAt) = (info.Length, info.LastWriteTimeUtc);

        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"));

        Assert.Equal(SampleMdbBuilder.KeyedTableCount, plan.Tables.Count);
        Assert.All(plan.Tables, t => { Assert.Equal(Rows, t.Inserts); Assert.Equal(Rows, t.Updates); Assert.Equal(0, t.Unchanged); });
        Assert.Equal(SampleMdbBuilder.KeyedTableCount * Rows, plan.TotalInserts);
        info.Refresh();
        Assert.Equal((length, writtenAt), (info.Length, info.LastWriteTimeUtc));
    }

    [Fact]
    public void Preview_WhenPatchTouchesFewKeysOfBigTarget_CountsOnlyPatchRows()
    {
        // The target has many more chemicals than the patch, and the patch also has a PK column (CHEMICAL_NO) over the filter cap.
        var big = Enumerable.Range(1, 250).Select(i => (i, 1)).ToArray();
        SampleMdbBuilder.Create(P("target.mdb"), big, rowsPerChemical: 2);
        SampleMdbBuilder.Create(P("patch.mdb"), big, rowsPerChemical: 2, valueOffset: 1);
        SampleMdbBuilder.Create(P("small.mdb"), [(7, 1), (300, 1)], rowsPerChemical: 2, valueOffset: 1);

        var all = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"));
        Assert.All(all.Tables, t => { Assert.Equal(500, t.Updates); Assert.Equal(0, t.Inserts); });

        var small = MdbApplier.Preview(P("small.mdb"), P("target.mdb"));
        Assert.All(small.Tables, t => { Assert.Equal(2, t.Updates); Assert.Equal(2, t.Inserts); Assert.Equal(0, t.Unchanged); });
    }

    [Fact]
    public void Apply_Upsert_OverwritesExistingInsertsNewAndLeavesOthersUntouched()
    {
        CreateTargetAndPatch();
        var untouchedBefore = Rows_(P("target.mdb"), "TB_SPEC_07", 100, 2);
        var originalValue = Rows_(P("target.mdb"), "TB_SPEC_07", 100, 1)[0][4];

        var result = MdbApplier.Apply(P("patch.mdb"), P("target.mdb"));

        const string table = "TB_SPEC_07";
        Assert.Equal((double)originalValue! + 5, (double)Rows_(P("target.mdb"), table, 100, 1)[0][4]!);   // overwritten
        Assert.Equal(Rows, Rows_(P("target.mdb"), table, 300, 1).Count);                                  // inserted
        Assert.Equal(Rows, Rows_(P("target.mdb"), table, 200, 1).Count);                                  // untouched
        Assert.Equal(untouchedBefore.Select(r => r[4]), Rows_(P("target.mdb"), table, 100, 2).Select(r => r[4]));
        Assert.Equal(4 * Rows, Count(P("target.mdb"), table));

        // backup holds the pre-change state
        Assert.NotNull(result.BackupPath);
        Assert.Equal(3 * Rows, Count(result.BackupPath!, table));
        Assert.Equal(originalValue, Rows_(result.BackupPath!, table, 100, 1)[0][4]);
    }

    [Fact]
    public void Apply_Twice_SecondRunChangesNothingAndMakesNoBackup()
    {
        CreateTargetAndPatch();
        MdbApplier.Apply(P("patch.mdb"), P("target.mdb"));

        var second = MdbApplier.Apply(P("patch.mdb"), P("target.mdb"));

        Assert.Equal(0, second.Plan.TotalInserts + second.Plan.TotalUpdates);
        Assert.Null(second.BackupPath);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(P("x"))!, "*.bak.mdb"));
    }

    [Fact]
    public void Apply_InsertOnly_WithConflictingRows_ThrowsBeforeBackupOrWrite()
    {
        CreateTargetAndPatch();

        Assert.Throws<InvalidOperationException>(() => MdbApplier.Apply(P("patch.mdb"), P("target.mdb"), ApplyMode.InsertOnly));

        Assert.Equal(3 * Rows, Count(P("target.mdb"), "TB_SPEC_01"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(P("x"))!, "*.bak.mdb"));
    }

    [Fact]
    public void Apply_InsertOnly_WithoutConflicts_Inserts()
    {
        SampleMdbBuilder.Create(P("target.mdb"), TargetChemicals, Rows);
        SampleMdbBuilder.Create(P("patch.mdb"), [(300, 1)], Rows);

        MdbApplier.Apply(P("patch.mdb"), P("target.mdb"), ApplyMode.InsertOnly);

        Assert.Equal(4 * Rows, Count(P("target.mdb"), "TB_SPEC_01"));
    }

    [Fact]
    public void Apply_SchemaMismatch_ThrowsBeforeBackupOrWrite()
    {
        CreateTargetAndPatch();
        using (var conn = MdbFile.Open(P("patch.mdb")))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE TB_SPEC_02 ADD COLUMN EXTRA TEXT(10)";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<SchemaMismatchException>(() => MdbApplier.Apply(P("patch.mdb"), P("target.mdb")));

        Assert.Contains(ex.Differences, d => d.Contains("EXTRA"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(P("x"))!, "*.bak.mdb"));
    }

    [Fact]
    public void Apply_SchemaMismatch_NamesThePatchAndTheTarget_NotLeftAndRight()
    {
        CreateTargetAndPatch();
        foreach (var (path, sql) in new[]
        {
            (P("patch.mdb"), "ALTER TABLE TB_SPEC_02 ADD COLUMN ONLY_IN_PATCH TEXT(10)"),
            (P("target.mdb"), "ALTER TABLE TB_SPEC_03 ADD COLUMN ONLY_IN_TARGET TEXT(10)"),
            (P("patch.mdb"), "CREATE TABLE TB_ONLY_PATCH (ID LONG, CONSTRAINT PK_OP PRIMARY KEY (ID))"),
        })
        {
            using var conn = MdbFile.Open(path);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<SchemaMismatchException>(() => MdbApplier.Preview(P("patch.mdb"), P("target.mdb")));

        Assert.Contains("[TB_SPEC_02] 컬럼 'ONLY_IN_PATCH'이(가) 대상 mdb에 없습니다.", ex.Differences);
        Assert.Contains("[TB_SPEC_03] 컬럼 'ONLY_IN_TARGET'이(가) 반영할 mdb에 없습니다.", ex.Differences);
        Assert.Contains("테이블 'TB_ONLY_PATCH'이(가) 대상 mdb에 없습니다.", ex.Differences);
        Assert.DoesNotContain(ex.Differences, d => d.Contains("왼쪽") || d.Contains("오른쪽"));
        Assert.StartsWith("반영할 mdb와 대상 mdb의 스키마가 달라", ex.Message);
    }

    [Fact]
    public void Apply_PatchWithSubsetOfTables_OnlyTouchesThoseTables()
    {
        SampleMdbBuilder.Create(P("target.mdb"), TargetChemicals, Rows);
        MdbExtractor.Extract(P("target.mdb"), P("patch.mdb"), [new ColumnFilter("CHEMICAL_NO", [100])]);   // TB_CODE is skipped
        Assert.DoesNotContain(UnkeyedName, MdbReader.ReadSchema(P("patch.mdb")).Tables.Select(t => t.Name));

        var result = MdbApplier.Apply(P("patch.mdb"), P("target.mdb"));

        Assert.Equal(0, result.Plan.TotalInserts + result.Plan.TotalUpdates);   // extracted rows are identical to the target
    }

    private const string UnkeyedName = SampleMdbBuilder.UnkeyedTable;

    [Fact]
    public void Apply_FailureMidWay_RollsBackEveryTable()
    {
        CreateTargetAndPatch();
        // A unique index the schema check cannot see: once the overwritten (100,1) rows get the patch's ITEM_NAMEs back,
        // the new (300,1) rows collide with them in the last table, after earlier tables were already written.
        using (var conn = MdbFile.Open(P("target.mdb")))
        {
            foreach (var sql in new[]
            {
                "UPDATE TB_SPEC_14 SET ITEM_NAME = 'U' & CHEMICAL_NO & '_' & SUB_CHEMICAL_NO & '_' & NUM",
                "CREATE UNIQUE INDEX UX_ITEM ON TB_SPEC_14 (ITEM_NAME)",
            })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
        var before = Rows_(P("target.mdb"), "TB_SPEC_01", 100, 1)[0][4];

        Assert.ThrowsAny<Exception>(() => MdbApplier.Apply(P("patch.mdb"), P("target.mdb")));

        Assert.Equal(3 * Rows, Count(P("target.mdb"), "TB_SPEC_01"));
        Assert.Equal(before, Rows_(P("target.mdb"), "TB_SPEC_01", 100, 1)[0][4]);
        Assert.Equal(3 * Rows, Count(P("target.mdb"), "TB_SPEC_14"));
    }
}

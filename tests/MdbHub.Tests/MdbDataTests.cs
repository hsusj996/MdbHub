using System.Data.OleDb;
using MdbHub.Core;
using MdbHub.Data;

namespace MdbHub.Tests;

/// <summary>Needs the 64-bit Access Database Engine (ACE) installed.</summary>
public class MdbDataTests : TempDirTestBase
{
    private static readonly (int, int)[] Chemicals = [(100, 1), (100, 2), (200, 1)];

    [Fact]
    public void ReadSchema_ReturnsTablesColumnsAndPrimaryKeys()
    {
        SampleMdbBuilder.Create(P("a.mdb"), Chemicals);

        var schema = MdbReader.ReadSchema(P("a.mdb"));

        Assert.Equal(SampleMdbBuilder.KeyedTableCount + 1, schema.Tables.Count);
        var spec = schema.Find("TB_SPEC_01")!;
        Assert.Equal(7, spec.Columns.Count);
        Assert.Equal(["CHEMICAL_NO", "SUB_CHEMICAL_NO", "NUM"], spec.PrimaryKey);
        Assert.Empty(schema.Find(SampleMdbBuilder.UnkeyedTable)!.PrimaryKey);
    }

    [Fact]
    public void GetRowCounts_ReturnsRowCountPerTable()
    {
        SampleMdbBuilder.Create(P("a.mdb"), Chemicals, rowsPerChemical: 5);
        var schema = MdbReader.ReadSchema(P("a.mdb"));

        var counts = MdbReader.GetRowCounts(P("a.mdb"), schema);

        Assert.Equal(schema.Tables.Count, counts.Count);
        Assert.Equal(15, counts["TB_SPEC_01"]);   // 3 chemicals x 5 rows
        Assert.Equal(1, counts[SampleMdbBuilder.UnkeyedTable]);
    }

    [Fact]
    public void GetDistinctValues_ReturnsSortedValuesAcrossTables()
    {
        SampleMdbBuilder.Create(P("a.mdb"), Chemicals);
        var schema = MdbReader.ReadSchema(P("a.mdb"));

        Assert.Equal([100, 200], MdbReader.GetDistinctValues(P("a.mdb"), schema, "CHEMICAL_NO"));
    }

    [Fact]
    public void Extract_CopiesOnlyMatchingRows_SkipsTablesWithoutFilterColumn_AndKeepsSchema()
    {
        SampleMdbBuilder.Create(P("src.mdb"), Chemicals, rowsPerChemical: 5);

        var result = MdbExtractor.Extract(P("src.mdb"), P("out.mdb"),
            [new ColumnFilter("CHEMICAL_NO", [100]), new ColumnFilter("SUB_CHEMICAL_NO", [1])]);

        Assert.Equal([SampleMdbBuilder.UnkeyedTable], result.SkippedTables);
        Assert.Equal(SampleMdbBuilder.KeyedTableCount, result.ExtractedRows.Count);
        Assert.All(result.ExtractedRows.Values, n => Assert.Equal(5, n));

        var src = MdbReader.ReadSchema(P("src.mdb"));
        var outSchema = MdbReader.ReadSchema(P("out.mdb"));
        var srcKeyed = new DatabaseSchema(src.Tables.Where(t => t.Name != SampleMdbBuilder.UnkeyedTable).ToList());
        Assert.Empty(SchemaValidator.Compare(srcKeyed, outSchema));

        var rows = MdbReader.ReadTable(P("out.mdb"), outSchema.Find("TB_SPEC_03")!).Rows;
        Assert.All(rows, r => { Assert.Equal(100, r[0]); Assert.Equal(1, r[1]); });
    }

    // A table that calls the chemical number ID instead of CHEMICAL_NO; ID 999 exists only here.
    private void AddTableWithIdInsteadOfChemicalNo(string path)
    {
        using var conn = MdbFile.Open(path);
        foreach (var sql in new[]
        {
            "CREATE TABLE TB_ALT (ID LONG, SUB_CHEMICAL_NO LONG, NUM LONG, V TEXT(10), CONSTRAINT PK_TB_ALT PRIMARY KEY (ID, SUB_CHEMICAL_NO, NUM))",
            "INSERT INTO TB_ALT VALUES (100, 1, 1, 'a')",
            "INSERT INTO TB_ALT VALUES (100, 1, 2, 'b')",
            "INSERT INTO TB_ALT VALUES (200, 1, 1, 'c')",
            "INSERT INTO TB_ALT VALUES (999, 1, 1, 'd')",
        })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Extract_FilterWithAliasColumns_UsesWhicheverColumnEachTableHas()
    {
        SampleMdbBuilder.Create(P("src.mdb"), Chemicals, rowsPerChemical: 5);
        AddTableWithIdInsteadOfChemicalNo(P("src.mdb"));
        ColumnFilter[] filters = [new(["CHEMICAL_NO", "ID"], [100]), new("SUB_CHEMICAL_NO", [1])];

        var result = MdbExtractor.Extract(P("src.mdb"), P("out.mdb"), filters);

        Assert.Equal(2, result.ExtractedRows["TB_ALT"]);                        // ID = 100 rows only
        Assert.Equal(5, result.ExtractedRows["TB_SPEC_01"]);
        Assert.Equal(["ID", "SUB_CHEMICAL_NO"], result.FilterColumns["TB_ALT"]);
        Assert.Equal(["CHEMICAL_NO", "SUB_CHEMICAL_NO"], result.FilterColumns["TB_SPEC_01"]);
        Assert.Equal([SampleMdbBuilder.UnkeyedTable], result.SkippedTables);
    }

    [Fact]
    public void Extract_FilterWithoutAlias_SkipsTableThatUsesAnotherName()
    {
        SampleMdbBuilder.Create(P("src.mdb"), Chemicals, rowsPerChemical: 5);
        AddTableWithIdInsteadOfChemicalNo(P("src.mdb"));

        var result = MdbExtractor.Extract(P("src.mdb"), P("out.mdb"), [new ColumnFilter("CHEMICAL_NO", [100])]);

        Assert.Contains("TB_ALT", result.SkippedTables);
    }

    [Fact]
    public void GetDistinctValues_WithAliasColumns_CombinesValuesFromEachTablesOwnColumn()
    {
        SampleMdbBuilder.Create(P("src.mdb"), Chemicals, rowsPerChemical: 5);
        AddTableWithIdInsteadOfChemicalNo(P("src.mdb"));
        var schema = MdbReader.ReadSchema(P("src.mdb"));

        Assert.Equal([100, 200], MdbReader.GetDistinctValues(P("src.mdb"), schema, "CHEMICAL_NO"));
        Assert.Equal([100, 200, 999], MdbReader.GetDistinctValues(P("src.mdb"), schema, ["CHEMICAL_NO", "ID"]));
    }

    [Fact]
    public void Create_LeavesTheNewFileClosed_SoItCanBeOpenedExclusivelyAndDeletedAtOnce()
    {
        MdbFile.Create(P("fresh.mdb"));

        // no GC, no waiting: ADOX's connection used to stay open on the new file until the garbage collector ran
        using (var exclusive = new FileStream(P("fresh.mdb"), FileMode.Open, FileAccess.Read, FileShare.None)) { }
        File.Delete(P("fresh.mdb"));
        Assert.False(File.Exists(P("fresh.mdb")));
    }

    [Fact]
    public void Extract_BuildsInTheWorkFolderAndMovesTheFinishedFile_LeavingNothingBehind()
    {
        SampleMdbBuilder.Create(P("src.mdb"), Chemicals, rowsPerChemical: 5);
        var work = Directory.CreateDirectory(P("work")).FullName;
        var dest = P(Path.Combine("new folder", "out.mdb"));   // the destination folder does not exist yet

        var result = MdbExtractor.Extract(P("src.mdb"), dest, [new ColumnFilter("CHEMICAL_NO", [100])], work);

        Assert.True(File.Exists(dest));
        Assert.Equal(SampleMdbBuilder.KeyedTableCount, result.ExtractedRows.Count);
        Assert.Empty(Directory.GetFiles(work));                                  // nothing left in the work folder
    }

    [Fact]
    public void Extract_WhenTheLastStepFails_ShowsTheRealError_AndLeavesNoFileAnywhere()
    {
        SampleMdbBuilder.Create(P("src.mdb"), Chemicals, rowsPerChemical: 5);
        var work = Directory.CreateDirectory(P("work")).FullName;
        File.WriteAllText(P("blocker"), "a file where the destination folder should be");   // the finished file cannot be put anywhere

        // everything is built successfully first; only moving the finished file into place fails
        var ex = Assert.ThrowsAny<Exception>(() =>
            MdbExtractor.Extract(P("src.mdb"), P(Path.Combine("blocker", "out.mdb")), [new ColumnFilter("CHEMICAL_NO", [100])], work));

        Assert.IsAssignableFrom<IOException>(ex);
        Assert.DoesNotContain("임시 파일을 지우지 못했습니다", ex.Message);               // the work file could be removed, so no extra warning
        Assert.False(File.Exists(P(Path.Combine("blocker", "out.mdb"))));
        Assert.Empty(Directory.GetFiles(work));                                          // the finished work file was cleaned up
    }

    [Fact]
    public void Extract_WhenTheWorkFileCannotBeCreated_FailsWithTheRealError_AndNoDestination()
    {
        SampleMdbBuilder.Create(P("src.mdb"), Chemicals, rowsPerChemical: 5);

        var ex = Assert.ThrowsAny<Exception>(() =>
            MdbExtractor.Extract(P("src.mdb"), P("out.mdb"), [new ColumnFilter("CHEMICAL_NO", [100])], P("no such work folder")));

        Assert.False(File.Exists(P("out.mdb")));
        Assert.DoesNotContain("다른 프로세스", ex.Message);
    }

    [Fact]
    public void Extract_WithNoMatchingRows_StillCreatesTables()
    {
        SampleMdbBuilder.Create(P("src.mdb"), Chemicals);

        var result = MdbExtractor.Extract(P("src.mdb"), P("out.mdb"), [new ColumnFilter("CHEMICAL_NO", [999])]);

        Assert.All(result.ExtractedRows.Values, n => Assert.Equal(0, n));
        Assert.Equal(SampleMdbBuilder.KeyedTableCount, MdbReader.ReadSchema(P("out.mdb")).Tables.Count);
    }

    [Fact]
    public void Extract_ToExistingFile_FailsAndDoesNotDeleteIt()
    {
        SampleMdbBuilder.Create(P("src.mdb"), Chemicals);
        SampleMdbBuilder.Create(P("out.mdb"), Chemicals);

        Assert.Throws<IOException>(() => MdbExtractor.Extract(P("src.mdb"), P("out.mdb"), [new ColumnFilter("CHEMICAL_NO", [100])]));
        Assert.True(File.Exists(P("out.mdb")));
    }

    [Fact]
    public void Compare_TwoSamplesWithDifferentValues_ReportsChangedRows()
    {
        SampleMdbBuilder.Create(P("a.mdb"), Chemicals, rowsPerChemical: 5);
        SampleMdbBuilder.Create(P("b.mdb"), [(100, 1), (300, 1)], rowsPerChemical: 5, valueOffset: 1);
        var sa = MdbReader.ReadSchema(P("a.mdb"));
        var sb = MdbReader.ReadSchema(P("b.mdb"));
        Assert.Empty(SchemaValidator.Compare(sa, sb));

        var table = sa.Find("TB_SPEC_01")!;
        var diff = TableComparer.Compare(MdbReader.ReadTable(P("a.mdb"), table), MdbReader.ReadTable(P("b.mdb"), sb.Find("TB_SPEC_01")!));

        Assert.Equal(5, diff.Rows.Count(r => r.Kind == RowDiffKind.Changed));   // (100,1) values differ by offset
        Assert.Equal(10, diff.Rows.Count(r => r.Kind == RowDiffKind.Removed));   // (100,2), (200,1) only in a
        Assert.Equal(5, diff.Rows.Count(r => r.Kind == RowDiffKind.Added));      // (300,1) only in b
        Assert.All(diff.Rows.Where(r => r.Kind == RowDiffKind.Changed), r => Assert.Equal(["SET_VALUE"], r.ChangedColumns));
    }

    [Fact]
    public void Backup_CopiesFileWithTimestampedName()
    {
        SampleMdbBuilder.Create(P("a.mdb"), Chemicals);

        var backup = MdbBackup.Create(P("a.mdb"), new DateTime(2026, 10, 15, 9, 30, 0));

        Assert.Equal(P("a.20261015_093000.bak.mdb"), backup);
        Assert.Equal(new FileInfo(P("a.mdb")).Length, new FileInfo(backup).Length);
    }
}

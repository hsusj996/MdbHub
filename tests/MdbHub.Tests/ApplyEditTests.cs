using MdbHub.Core;
using MdbHub.Data;

namespace MdbHub.Tests;

/// <summary>Hand edits in the apply preview. The apply tests need the 64-bit Access Database Engine (ACE).</summary>
public class ApplyEditTests : TempDirTestBase
{
    // ---- text -> typed value ----

    [Theory]
    [InlineData("Integer", 5, "42", 42)]
    [InlineData("Double", 1.5, "2.25", 2.25)]
    [InlineData("Integer", 5, " 7 ", 7)]
    public void CellValueParser_ParsesNumbersToTheSampleType(string type, object sample, string text, object expected)
    {
        Assert.True(CellValueParser.TryParse(new ColumnInfo("C", type), sample, text, out var value, out var error));
        Assert.Null(error);
        Assert.Equal(expected, value);
        Assert.IsType(sample.GetType(), value);
    }

    [Fact]
    public void CellValueParser_EmptyAndNullTextMeanNull_AndTextKeepsEverything()
    {
        var col = new ColumnInfo("C", "VarWChar");

        Assert.True(CellValueParser.TryParse(col, "x", "", out var empty, out _));
        Assert.Null(empty);
        Assert.True(CellValueParser.TryParse(col, "x", CellValueParser.NullText, out var nul, out _));
        Assert.Null(nul);
        Assert.True(CellValueParser.TryParse(col, "x", "  keep spaces, 한글 ", out var text, out _));
        Assert.Equal("  keep spaces, 한글 ", text);
    }

    [Fact]
    public void CellValueParser_RejectsBadInput_WithAReadableMessage()
    {
        var number = new ColumnInfo("QTY", "Integer");
        Assert.False(CellValueParser.TryParse(number, 1, "abc", out _, out var error));
        Assert.Contains("'abc'", error);
        Assert.False(CellValueParser.TryParse(number, 1, "99999999999", out _, out _));   // overflow
        Assert.False(CellValueParser.TryParse(new ColumnInfo("FLAG", "Boolean"), true, "maybe", out _, out _));
        Assert.False(CellValueParser.TryParse(new ColumnInfo("D", "Date"), DateTime.Now, "not a date", out _, out _));
        Assert.False(CellValueParser.TryParse(new ColumnInfo("B", "LongVarBinary"), new byte[] { 1 }, "x", out _, out var binary));
        Assert.Contains("수정할 수 없는", binary);
    }

    [Fact]
    public void CellValueParser_Booleans_AcceptCommonSpellings()
    {
        var col = new ColumnInfo("FLAG", "Boolean");
        foreach (var (text, expected) in new[] { ("true", true), ("1", true), ("예", true), ("false", false), ("0", false), ("아니오", false) })
        {
            Assert.True(CellValueParser.TryParse(col, true, text, out var v, out _));
            Assert.Equal(expected, v);
        }
    }

    // ---- applying edits ----

    private void CreateTargetAndPatch()
    {
        SampleMdbBuilder.Create(P("target.mdb"), [(100, 1)], rowsPerChemical: 5);
        SampleMdbBuilder.Create(P("patch.mdb"), [(100, 1), (300, 1)], rowsPerChemical: 5, valueOffset: 0);
        using var conn = MdbFile.Open(P("patch.mdb"));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE TB_SPEC_01 SET SET_VALUE = SET_VALUE + 100 WHERE CHEMICAL_NO = 100 AND NUM = 2";   // one overwrite; 300/1 rows are inserts
        cmd.ExecuteNonQuery();
    }

    private static AppConfig IgnoreCodeTable()
    {
        var config = new AppConfig();
        config.Tables[SampleMdbBuilder.UnkeyedTable] = new TableRule { Ignore = true };
        return config;
    }

    private (TableApplyPlan Table, RowDiff Update, RowDiff Insert) PlanRows()
    {
        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), IgnoreCodeTable());
        var table = plan.Tables.Single(t => t.Target.Name == "TB_SPEC_01");
        return (table, table.Diff.Rows.Single(r => r.Kind == RowDiffKind.Changed), table.Diff.Rows.First(r => r.Kind == RowDiffKind.Added));
    }

    private static object? PatchValue(TableApplyPlan table, RowDiff row, string column) => row.Right![table.Source.IndexOf(column)];

    [Fact]
    public void Apply_WithEdits_WritesEditedValues_LeavesThePatchFileAlone_AndLogsTheEdits()
    {
        CreateTargetAndPatch();
        var (table, update, insert) = PlanRows();
        var patchBefore = new FileInfo(P("patch.mdb")).LastWriteTimeUtc;
        CellEdit[] edits =
        [
            new("TB_SPEC_01", update.Key, "SET_VALUE", PatchValue(table, update, "SET_VALUE"), 777.5),
            new("TB_SPEC_01", insert.Key, "ITEM_NAME", PatchValue(table, insert, "ITEM_NAME"), "EDITED, with \"quotes\""),
        ];

        var result = MdbApplier.Apply(P("patch.mdb"), P("target.mdb"), config: IgnoreCodeTable(), edits: edits);

        var targetSchema = MdbReader.ReadSchema(P("target.mdb")).Find("TB_SPEC_01")!;
        var rows = MdbReader.ReadTable(P("target.mdb"), targetSchema).Rows;
        Assert.Equal(777.5, rows.Single(r => (int)r[0]! == 100 && (int)r[2]! == 2)[4]);                     // overwritten with the edited value
        Assert.Equal("EDITED, with \"quotes\"", rows.First(r => (int)r[0]! == 300)[3]);                     // inserted with the edited value

        var patchSchema = MdbReader.ReadSchema(P("patch.mdb")).Find("TB_SPEC_01")!;
        Assert.DoesNotContain(MdbReader.ReadTable(P("patch.mdb"), patchSchema).Rows, r => Equals(r[4], 777.5));   // patch file untouched
        Assert.Equal(patchBefore, new FileInfo(P("patch.mdb")).LastWriteTimeUtc);

        Assert.NotNull(result.EditLogPath);
        var log = File.ReadAllLines(result.EditLogPath!);
        Assert.Equal(3, log.Length);                                  // header + 2 edits
        Assert.Contains(log, l => l.Contains("SET_VALUE") && l.Contains("777.5"));
        Assert.Contains(log, l => l.Contains("\"EDITED, with \"\"quotes\"\"\""));   // CSV-escaped
    }

    [Fact]
    public void WithEdits_AnOverwriteEditedBackToTheTargetValue_BecomesUnchanged()
    {
        CreateTargetAndPatch();
        var (table, update, _) = PlanRows();
        var targetValue = update.Left![table.Target.IndexOf("SET_VALUE")];

        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), IgnoreCodeTable());
        var edited = plan.WithEdits([new CellEdit("TB_SPEC_01", update.Key, "SET_VALUE", PatchValue(table, update, "SET_VALUE"), targetValue)]);

        var t = edited.Tables.Single(x => x.Target.Name == "TB_SPEC_01");
        Assert.Equal(0, t.Updates);
        Assert.Equal(table.Unchanged + 1, t.Unchanged);
        Assert.Equal(plan.TotalUpdates - 1, edited.TotalUpdates);
        Assert.Equal(plan.TotalInserts, edited.TotalInserts);
    }

    [Fact]
    public void WithEdits_EditingAKeyColumn_IsRejected()
    {
        CreateTargetAndPatch();
        var (table, update, _) = PlanRows();
        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), IgnoreCodeTable());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            plan.WithEdits([new CellEdit("TB_SPEC_01", update.Key, "NUM", PatchValue(table, update, "NUM"), 99)]));

        Assert.Contains("키 컬럼", ex.Message);
    }

    [Fact]
    public void Apply_WithAStaleEdit_FailsBeforeBackupOrWrite()
    {
        CreateTargetAndPatch();
        var (table, update, _) = PlanRows();
        // the patch value the user saw was something else (e.g. the patch file was replaced after the preview)
        var stale = new CellEdit("TB_SPEC_01", update.Key, "SET_VALUE", 1.0, 5.0);

        var ex = Assert.Throws<InvalidOperationException>(() => MdbApplier.Apply(P("patch.mdb"), P("target.mdb"), config: IgnoreCodeTable(), edits: [stale]));

        Assert.Contains("달라졌습니다", ex.Message);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(P("x"))!, "*.bak.mdb"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(P("x"))!, "*.edits.csv"));
    }

    [Fact]
    public void WithEdits_ForARowOrTableNotInThePlan_IsRejected()
    {
        CreateTargetAndPatch();
        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), IgnoreCodeTable());

        Assert.Throws<InvalidOperationException>(() =>
            plan.WithEdits([new CellEdit("TB_SPEC_01", [999, 9, 9], "ITEM_NAME", "a", "b")]));          // no such row
        Assert.Throws<InvalidOperationException>(() =>
            plan.WithEdits([new CellEdit("NOPE", [1], "X", "a", "b")]));                                  // no such table
    }
}

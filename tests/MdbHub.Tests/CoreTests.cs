using MdbHub.Core;

namespace MdbHub.Tests;

public class CoreTests
{
    private static TableSchema Table(string name, params string[] cols) =>
        new(name, cols.Select(c => new ColumnInfo(c, "Integer")).ToList(), ["ID"]);

    private static TableData Data(TableSchema schema, params object?[][] rows) => new(schema, rows);

    [Fact]
    public void ColumnFilter_ResolvesFirstAliasTheTableHas_CaseInsensitive()
    {
        var filter = new ColumnFilter(["CHEMICAL_NO", "ID"], [1]);

        Assert.Equal("chemical_no", filter.ResolveIn(Table("T", "chemical_no", "ID")));   // alias order wins, table's own spelling is returned
        Assert.Equal("Id", filter.ResolveIn(Table("T", "Id", "V")));
        Assert.Null(filter.ResolveIn(Table("T", "V")));
    }

    [Fact]
    public void SchemaValidator_IdenticalSchemas_HaveNoDifferences()
    {
        var a = new DatabaseSchema([Table("T", "ID", "V")]);
        var b = new DatabaseSchema([Table("t", "V", "ID")]);   // case and column order do not matter

        Assert.Empty(SchemaValidator.Compare(a, b));
    }

    [Fact]
    public void SchemaValidator_ReportsMissingTableColumnAndTypeChange()
    {
        var a = new DatabaseSchema([Table("T", "ID", "V"), Table("ONLY_LEFT", "ID")]);
        var b = new DatabaseSchema([new TableSchema("T", [new("ID", "Integer"), new("V", "Double")], ["ID"])]);

        var diffs = SchemaValidator.Compare(a, b);

        Assert.Equal(2, diffs.Count);
        Assert.Contains(diffs, d => d.Contains("ONLY_LEFT"));
        Assert.Contains(diffs, d => d.Contains("'V'") && d.Contains("타입"));
    }

    [Fact]
    public void SchemaValidator_UsesTheGivenSideNames_InsteadOfLeftAndRight()
    {
        var patch = new DatabaseSchema(
        [
            new TableSchema("T", [new("ID", "Integer"), new("V", "Double"), new("ONLY_PATCH", "Integer")], ["ID"]),
            Table("PATCH_ONLY_TABLE", "ID"),
        ]);
        var target = new DatabaseSchema(
        [
            new TableSchema("T", [new("ID", "Integer"), new("V", "Integer"), new("ONLY_TARGET", "Integer")], ["ID", "V"]),
        ]);

        var diffs = SchemaValidator.Compare(patch, target, "반영할 mdb", "대상 mdb");

        Assert.Contains("테이블 'PATCH_ONLY_TABLE'이(가) 대상 mdb에 없습니다.", diffs);
        Assert.Contains("[T] 컬럼 'ONLY_PATCH'이(가) 대상 mdb에 없습니다.", diffs);
        Assert.Contains("[T] 컬럼 'ONLY_TARGET'이(가) 반영할 mdb에 없습니다.", diffs);
        Assert.Contains(diffs, d => d.Contains("'V' 타입 불일치") && d.Contains("반영할 mdb Double") && d.Contains("대상 mdb Integer"));
        Assert.Contains(diffs, d => d.Contains("키 불일치") && d.Contains("반영할 mdb (ID)") && d.Contains("대상 mdb (ID, V)"));
        Assert.DoesNotContain(diffs, d => d.Contains("왼쪽") || d.Contains("오른쪽"));
    }

    [Fact]
    public void SchemaMismatchException_ShowsTheFirstDifferences_AndKeepsAllOfThem()
    {
        var many = Enumerable.Range(1, 35).Select(i => $"차이 {i}").ToList();

        var ex = new SchemaMismatchException(many, "반영할 mdb와 대상 mdb");

        Assert.Equal(35, ex.Differences.Count);
        Assert.StartsWith("반영할 mdb와 대상 mdb의 스키마가 달라 진행할 수 없습니다 (35건)", ex.Message);
        Assert.Contains("차이 20", ex.Message);
        Assert.DoesNotContain("차이 21", ex.Message);
        Assert.Contains("외 15건", ex.Message);
    }

    [Fact]
    public void TableComparer_FindsAddedRemovedChangedAndUnchanged()
    {
        var s = Table("T", "ID", "V");
        var left = Data(s, [1, "a"], [2, "b"], [3, "c"]);
        var right = Data(s, [2, "b"], [3, "X"], [4, "d"]);

        var diff = TableComparer.Compare(left, right);

        Assert.Equal(1, diff.UnchangedCount);
        Assert.Equal(RowDiffKind.Removed, diff.Rows.Single(r => r.Key[0] is 1).Kind);
        Assert.Equal(RowDiffKind.Added, diff.Rows.Single(r => r.Key[0] is 4).Kind);
        var changed = diff.Rows.Single(r => r.Key[0] is 3);
        Assert.Equal(RowDiffKind.Changed, changed.Kind);
        Assert.Equal(["V"], changed.ChangedColumns);
    }

    [Fact]
    public void TableComparer_TreatsNullsAsEqualAndDetectsNullChange()
    {
        var s = Table("T", "ID", "V");
        var diff = TableComparer.Compare(
            Data(s, [1, null], [2, null]),
            Data(s, [1, null], [2, "x"]));

        Assert.Equal(1, diff.UnchangedCount);
        Assert.Equal(2, diff.Rows.Single().Key[0]);
    }

    [Fact]
    public void TableComparer_WithoutPrimaryKey_Throws()
    {
        var s = new TableSchema("T", [new("ID", "Integer")], []);
        Assert.Throws<InvalidOperationException>(() => TableComparer.Compare(Data(s), Data(s)));
    }
}

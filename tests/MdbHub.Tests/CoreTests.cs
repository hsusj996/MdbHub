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

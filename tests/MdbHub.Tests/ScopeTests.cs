using MdbHub.Core;
using MdbHub.Data;

namespace MdbHub.Tests;

/// <summary>Limiting an apply to chosen groups of rows, and combination filters for extracts. Needs the 64-bit Access Database Engine (ACE).</summary>
public class ScopeTests : TempDirTestBase
{
    private static readonly string[] Group = ["CHEMICAL_NO", "SUB_CHEMICAL_NO"];
    private const int KeyedTables = SampleMdbBuilder.KeyedTableCount;

    private static AppConfig IgnoreCodeTable()
    {
        var config = new AppConfig();
        config.Tables[SampleMdbBuilder.UnkeyedTable] = new TableRule { Ignore = true };
        return config;
    }

    private static void Sql(string path, params string[] sqls)
    {
        using var conn = MdbFile.Open(path);
        foreach (var sql in sqls)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    // target: (100,1) (100,2) (200,1) (200,2) with 3 rows each; patch: the same four, every value changed, plus a new (300,1)
    private void CreateFiles(bool withOtherKeyedTable = false)
    {
        SampleMdbBuilder.Create(P("target.mdb"), [(100, 1), (100, 2), (200, 1), (200, 2)], rowsPerChemical: 3);
        SampleMdbBuilder.Create(P("patch.mdb"), [(100, 1), (100, 2), (200, 1), (200, 2), (300, 1)], rowsPerChemical: 3, valueOffset: 1);
        if (!withOtherKeyedTable) return;

        // a table keyed by ID instead of CHEMICAL_NO
        foreach (var path in new[] { P("target.mdb"), P("patch.mdb") })
            Sql(path, "CREATE TABLE TB_ALT (ID LONG, NUM LONG, V TEXT(10), CONSTRAINT PK_ALT PRIMARY KEY (ID, NUM))",
                "INSERT INTO TB_ALT VALUES (1, 1, 'a')");
        Sql(P("patch.mdb"), "UPDATE TB_ALT SET V = 'changed'", "INSERT INTO TB_ALT VALUES (2, 1, 'new')");
    }

    private static ApplyScope Scope(IEnumerable<(int Chem, int Sub)> groups, IReadOnlyDictionary<string, TableScopeChoice>? others = null) =>
        new(Group, groups.Select(g => RowKey.Of([g.Chem, g.Sub])).ToHashSet(), others);

    private static Dictionary<(int, int), int> RowsPerGroup(string path, string table) =>
        MdbReader.ReadTable(path, MdbReader.ReadSchema(path).Find(table)!).Rows
            .GroupBy(r => ((int)r[0]!, (int)r[1]!)).ToDictionary(g => g.Key, g => g.Count());

    // ---- groups of a plan ----

    [Fact]
    public void Groups_ListsEachCombinationOnce_SortedByValue_WithTheirShareOfThePlan()
    {
        CreateFiles();
        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), IgnoreCodeTable());

        var groups = plan.Groups(Group);

        Assert.Equal(["100 | 1", "100 | 2", "200 | 1", "200 | 2", "300 | 1"], groups.Select(g => g.Value.ToString()));
        var overwritten = groups.First();
        Assert.Equal((0, 3 * KeyedTables, KeyedTables), (overwritten.Inserts, overwritten.Updates, overwritten.Tables));
        var created = groups.Last();
        Assert.Equal((3 * KeyedTables, 0, KeyedTables), (created.Inserts, created.Updates, created.Tables));
    }

    [Fact]
    public void Groups_ByASingleColumn_MergesTheCombinations()
    {
        CreateFiles();
        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), IgnoreCodeTable());

        var groups = plan.Groups(["CHEMICAL_NO"]);

        Assert.Equal(["100", "200", "300"], groups.Select(g => g.Value.ToString()));
        Assert.Equal(6 * KeyedTables, groups[0].Updates);   // (100,1) and (100,2)
    }

    [Fact]
    public void KeyColumns_AreTheColumnsATableKeyHas()
    {
        CreateFiles(withOtherKeyedTable: true);
        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), IgnoreCodeTable());

        Assert.Equal(["CHEMICAL_NO", "ID", "NUM", "SUB_CHEMICAL_NO"], plan.KeyColumns.Order(StringComparer.Ordinal));   // each name once; the order is not part of the promise
    }

    // ---- limiting the plan ----

    [Fact]
    public void WithScope_KeepsOnlyTheChosenCombinations_NotTheirCrossProduct()
    {
        CreateFiles();
        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), IgnoreCodeTable());

        var scoped = plan.WithScope(Scope([(100, 1), (200, 2)]));   // (100,2) and (200,1) must NOT come along

        Assert.Equal(2 * 3 * KeyedTables, scoped.TotalUpdates);
        Assert.Equal(0, scoped.TotalInserts);
        var groups = scoped.Groups(Group).Select(g => g.Value.ToString());
        Assert.Equal(["100 | 1", "200 | 2"], groups);
    }

    [Fact]
    public void WithScope_WithoutColumns_ChangesNothing()
    {
        CreateFiles();
        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), IgnoreCodeTable());

        Assert.Same(plan, plan.WithScope(null));
        Assert.Same(plan, plan.WithScope(new ApplyScope([], new HashSet<string>())));
    }

    [Fact]
    public void WithScope_TableWhoseKeyLacksTheColumns_NeedsADecision()
    {
        CreateFiles(withOtherKeyedTable: true);
        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), IgnoreCodeTable());
        Assert.Equal(["TB_ALT"], plan.TablesLacking(Group));

        var ex = Assert.Throws<InvalidOperationException>(() => plan.WithScope(Scope([(100, 1)])));
        Assert.Contains("TB_ALT", ex.Message);

        var excluded = plan.WithScope(Scope([(100, 1)], new Dictionary<string, TableScopeChoice> { ["TB_ALT"] = TableScopeChoice.Exclude }));
        Assert.Equal(0, excluded.Tables.Single(t => t.Target.Name == "TB_ALT").Updates + excluded.Tables.Single(t => t.Target.Name == "TB_ALT").Inserts);

        var all = plan.WithScope(Scope([(100, 1)], new Dictionary<string, TableScopeChoice> { ["tb_alt"] = TableScopeChoice.IncludeAll }));
        var alt = all.Tables.Single(t => t.Target.Name == "TB_ALT");
        Assert.Equal((1, 1), (alt.Updates, alt.Inserts));   // everything of it, whatever the group
    }

    [Fact]
    public void Contains_TellsWhetherThePlanWritesARow()
    {
        CreateFiles();
        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), IgnoreCodeTable());
        var scoped = plan.WithScope(Scope([(100, 1)]));

        Assert.True(scoped.Contains("TB_SPEC_01", [100, 1, 2]));
        Assert.False(scoped.Contains("TB_SPEC_01", [200, 1, 2]));   // outside the scope
        Assert.False(scoped.Contains("TB_SPEC_01", [999, 1, 2]));   // not in the patch at all
        Assert.True(plan.Contains("tb_spec_01", [200, 1, 2]));
    }

    // ---- applying within a scope ----

    [Fact]
    public void Apply_WithScope_WritesOnlyTheChosenGroups_AndLeavesTheOthersAsTheyWere()
    {
        CreateFiles();

        var result = MdbApplier.Apply(P("patch.mdb"), P("target.mdb"), config: IgnoreCodeTable(), scope: Scope([(100, 1), (300, 1)]));

        Assert.Equal(3 * KeyedTables, result.Plan.TotalUpdates);
        Assert.Equal(3 * KeyedTables, result.Plan.TotalInserts);
        var rows = RowsPerGroup(P("target.mdb"), "TB_SPEC_01");
        Assert.Equal(3, rows[(300, 1)]);                                // created
        Assert.False(rows.ContainsKey((300, 2)));
        var table = MdbReader.ReadSchema(P("target.mdb")).Find("TB_SPEC_01")!;
        var values = MdbReader.ReadTable(P("target.mdb"), table).Rows.ToLookup(r => ((int)r[0]!, (int)r[1]!), r => (double)r[4]!);
        var patchTable = MdbReader.ReadSchema(P("patch.mdb")).Find("TB_SPEC_01")!;
        var patchValues = MdbReader.ReadTable(P("patch.mdb"), patchTable).Rows.ToLookup(r => ((int)r[0]!, (int)r[1]!), r => (double)r[4]!);
        Assert.Equal(patchValues[(100, 1)].OrderBy(v => v), values[(100, 1)].OrderBy(v => v));   // chosen: now as in the patch
        Assert.NotEqual(patchValues[(100, 2)].OrderBy(v => v), values[(100, 2)].OrderBy(v => v)); // not chosen: still the old values
        Assert.NotEqual(patchValues[(200, 1)].OrderBy(v => v), values[(200, 1)].OrderBy(v => v));
    }

    [Fact]
    public void Apply_InsertOnly_ConflictsOutsideTheScopeDoNotBlockTheApply()
    {
        CreateFiles();

        // (100,1)...(200,2) exist and differ (conflicts for insert-only), but only the new (300,1) is chosen
        var result = MdbApplier.Apply(P("patch.mdb"), P("target.mdb"), ApplyMode.InsertOnly, config: IgnoreCodeTable(), scope: Scope([(300, 1)]));

        Assert.Equal(3 * KeyedTables, result.Plan.TotalInserts);
        Assert.Equal(0, result.Plan.TotalUpdates);

        // and choosing a conflicting group still stops it
        Assert.Throws<InvalidOperationException>(() =>
            MdbApplier.Apply(P("patch.mdb"), P("target.mdb"), ApplyMode.InsertOnly, config: IgnoreCodeTable(), scope: Scope([(100, 1)])));
    }

    [Fact]
    public void Apply_AnEditOfARowOutsideTheScope_IsRejectedBeforeAnythingIsWritten()
    {
        CreateFiles();
        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), IgnoreCodeTable());
        var table = plan.Tables.Single(t => t.Target.Name == "TB_SPEC_01");
        var row = table.Diff.Rows.First(r => (int)r.Key[0]! == 200);   // a (200,x) row, but only (100,1) is chosen
        var edit = new CellEdit("TB_SPEC_01", row.Key, "ITEM_NAME", row.Right![table.Source.IndexOf("ITEM_NAME")], "X");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MdbApplier.Apply(P("patch.mdb"), P("target.mdb"), config: IgnoreCodeTable(), edits: [edit], scope: Scope([(100, 1)])));

        Assert.Contains("미리보기에 없습니다", ex.Message);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(P("x"))!, "*.bak.mdb"));
    }

    // ---- combination filters for extracts ----

    [Fact]
    public void GetDistinctCombinations_ListsPairsFromTablesThatHaveAllTheColumns()
    {
        CreateFiles(withOtherKeyedTable: true);
        var schema = MdbReader.ReadSchema(P("patch.mdb"));

        var combos = MdbReader.GetDistinctCombinations(P("patch.mdb"), schema, Group);

        Assert.Equal(["100 | 1", "100 | 2", "200 | 1", "200 | 2", "300 | 1"], combos.Select(c => c.ToString()));
        Assert.Equal(["1", "2"], MdbReader.GetDistinctCombinations(P("patch.mdb"), schema, ["ID"]).Select(c => c.ToString()));   // only TB_ALT has ID
    }

    [Fact]
    public void Extract_WithACombinationFilter_TakesOnlyTheChosenPairs_NotTheirCrossProduct()
    {
        CreateFiles(withOtherKeyedTable: true);
        var combos = new CombinationFilter(Group, [[100, 1], [200, 2]]);

        var result = MdbExtractor.Extract(P("patch.mdb"), P("out.mdb"), [], combinations: [combos]);

        Assert.Contains("TB_ALT", result.SkippedTables);                         // no CHEMICAL_NO: left out
        Assert.Contains(SampleMdbBuilder.UnkeyedTable, result.SkippedTables);
        Assert.Equal("CHEMICAL_NO + SUB_CHEMICAL_NO", result.FilterColumns["TB_SPEC_01"].Single());
        Assert.Equal(new Dictionary<(int, int), int> { [(100, 1)] = 3, [(200, 2)] = 3 }, RowsPerGroup(P("out.mdb"), "TB_SPEC_01"));
    }

    [Fact]
    public void Extract_CombinationFilterWithoutValues_OnlyRequiresTheColumns()
    {
        CreateFiles(withOtherKeyedTable: true);

        var result = MdbExtractor.Extract(P("patch.mdb"), P("out.mdb"), [], combinations: [new CombinationFilter(Group, null)]);

        Assert.Equal(15, RowsPerGroup(P("out.mdb"), "TB_SPEC_01").Values.Sum());   // all five groups, 3 rows each
        Assert.Contains("TB_ALT", result.SkippedTables);
    }

    [Fact]
    public void Extract_CombinationAndColumnFilterTogether_MustBothMatch()
    {
        CreateFiles();

        MdbExtractor.Extract(P("patch.mdb"), P("out.mdb"), [new ColumnFilter("NUM", [2])], combinations: [new CombinationFilter(Group, [[100, 1], [100, 2], [300, 1]])]);

        var rows = MdbReader.ReadTable(P("out.mdb"), MdbReader.ReadSchema(P("out.mdb")).Find("TB_SPEC_01")!).Rows;
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(2, r[2]));
    }

    [Fact]
    public void Extract_WithNothingToFilterOn_OrAnEmptyCombinationList_IsRejected()
    {
        CreateFiles();

        Assert.Throws<ArgumentException>(() => MdbExtractor.Extract(P("patch.mdb"), P("o1.mdb"), []));
        Assert.Throws<ArgumentException>(() => MdbExtractor.Extract(P("patch.mdb"), P("o2.mdb"), [], combinations: [new CombinationFilter(Group, [])]));
    }
}

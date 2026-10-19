using MdbHub.Core;
using MdbHub.Data;

namespace MdbHub.Tests;

/// <summary>Config-defined keys with real mdb files. Needs the 64-bit Access Database Engine (ACE).</summary>
public class ConfigDataTests : TempDirTestBase
{
    private static readonly (int, int)[] Chemicals = [(100, 1), (200, 1)];

    private void CreateTargetAndPatch()
    {
        SampleMdbBuilder.Create(P("target.mdb"), Chemicals, rowsPerChemical: 3);
        SampleMdbBuilder.Create(P("patch.mdb"), Chemicals, rowsPerChemical: 3);
        using var conn = MdbFile.Open(P("patch.mdb"));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {SampleMdbBuilder.UnkeyedTable} SET DESCRIPTION = 'changed'";   // the only difference between the files
        cmd.ExecuteNonQuery();
    }

    private static AppConfig ConfigWith(string table, TableRule rule)
    {
        var config = new AppConfig();
        config.Tables[table] = rule;
        return config;
    }

    [Fact]
    public void Preview_TableWithoutAnyKey_IsReportedAsSkipped()
    {
        CreateTargetAndPatch();

        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"));

        Assert.Equal([SampleMdbBuilder.UnkeyedTable], plan.SkippedTablesWithoutPrimaryKey);
        Assert.Equal(0, plan.TotalUpdates);
    }

    [Fact]
    public void Preview_WithConfiguredKey_IncludesTheTableThatHasNoDeclaredPrimaryKey()
    {
        CreateTargetAndPatch();
        var config = ConfigWith(SampleMdbBuilder.UnkeyedTable, new TableRule { Key = ["CODE"] });

        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), config);

        Assert.Empty(plan.SkippedTablesWithoutPrimaryKey);
        var table = plan.Tables.Single(t => t.Source.Name == SampleMdbBuilder.UnkeyedTable);
        Assert.Equal(1, table.Updates);
        Assert.Equal(1, plan.TotalUpdates);   // every other table is identical
    }

    [Fact]
    public void Apply_WithConfiguredKey_UpdatesTheTableAndBacksUp()
    {
        CreateTargetAndPatch();
        var config = ConfigWith(SampleMdbBuilder.UnkeyedTable, new TableRule { Key = ["CODE"] });

        var result = MdbApplier.Apply(P("patch.mdb"), P("target.mdb"), config: config);

        var schema = MdbReader.ReadSchema(P("target.mdb")).Find(SampleMdbBuilder.UnkeyedTable)!;
        Assert.Equal("changed", MdbReader.ReadTable(P("target.mdb"), schema).Rows.Single()[1]);
        Assert.NotNull(result.BackupPath);
    }

    [Fact]
    public void Preview_IgnoredTable_IsLeftOutWithoutAWarning()
    {
        CreateTargetAndPatch();
        var config = ConfigWith(SampleMdbBuilder.UnkeyedTable, new TableRule { Ignore = true });

        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), config);

        Assert.Empty(plan.SkippedTablesWithoutPrimaryKey);
        Assert.DoesNotContain(plan.Tables, t => t.Source.Name == SampleMdbBuilder.UnkeyedTable);
    }

    [Fact]
    public void Preview_ConfiguredKeyThatIsNotUnique_ThrowsDuplicateKey()
    {
        CreateTargetAndPatch();
        var config = ConfigWith("TB_SPEC_01", new TableRule { Key = ["SUB_CHEMICAL_NO"] });   // many rows share one SUB_CHEMICAL_NO

        var ex = Assert.Throws<DuplicateKeyException>(() => MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), config));

        Assert.Equal("TB_SPEC_01", ex.Table);
    }

    [Fact]
    public void Preview_ConfiguredKeyNamingAMissingColumn_ReportsProblemAndSkipsTable()
    {
        CreateTargetAndPatch();
        var config = ConfigWith("TB_SPEC_01", new TableRule { Key = ["CHEMICAL_NO", "NOPE"] });

        var plan = MdbApplier.Preview(P("patch.mdb"), P("target.mdb"), config);

        Assert.Contains(plan.KeyProblems, p => p.Contains("TB_SPEC_01") && p.Contains("NOPE"));
        Assert.Contains("TB_SPEC_01", plan.SkippedTablesWithoutPrimaryKey);
    }
}

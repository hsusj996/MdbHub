using MdbHub.Core;

namespace MdbHub.Tests;

public class ConfigTests : TempDirTestBase
{
    private static TableSchema Table(string name, string[] columns, params string[] declaredKey) =>
        new(name, columns.Select(c => new ColumnInfo(c, "Integer")).ToList(), declaredKey);

    [Fact]
    public void LoadOrCreateDefault_WhenFileMissing_WritesDefaultConfig()
    {
        var store = new ConfigStore(P("cfg.json"));

        var config = store.LoadOrCreateDefault();

        Assert.True(File.Exists(P("cfg.json")));
        Assert.Equal(["CHEMICAL_NO", "SUB_CHEMICAL_NO"], config.Filters.Select(f => f.Name));
        Assert.Empty(config.Tables);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsRulesAndKeepsKoreanReadable()
    {
        var store = new ConfigStore(P("cfg.json"));
        var config = AppConfig.CreateDefault();
        config.Tables["TB_ALT"] = new TableRule { Key = ["ID", "SUB_CHEMICAL_NO", "NUM"] };
        config.Tables["TB_CODE"] = new TableRule { Ignore = true };
        config.Filters.Add(new FilterDefinition { Name = "약액번호", Columns = ["CHEMICAL_NO", "ID"] });

        store.Save(config);
        var loaded = store.LoadOrCreateDefault();

        Assert.Contains("약액번호", File.ReadAllText(P("cfg.json")));   // not escaped as \uXXXX
        Assert.Equal(["ID", "SUB_CHEMICAL_NO", "NUM"], loaded.Tables["tb_alt"].Key);   // table names are case-insensitive
        Assert.True(loaded.Tables["TB_CODE"].Ignore);
        Assert.Equal(["CHEMICAL_NO", "ID"], loaded.Filters.Single(f => f.Name == "약액번호").Columns);
    }

    [Fact]
    public void Save_KeepsPreviousFileAsBackup()
    {
        var store = new ConfigStore(P("cfg.json"));
        store.Save(AppConfig.CreateDefault());
        var before = File.ReadAllText(P("cfg.json"));

        var changed = AppConfig.CreateDefault();
        changed.Tables["T"] = new TableRule { Key = ["A"] };
        store.Save(changed);

        Assert.Equal(before, File.ReadAllText(P("cfg.json.bak")));
        Assert.Contains("\"T\"", File.ReadAllText(P("cfg.json")));
        Assert.False(File.Exists(P("cfg.json.tmp")));
    }

    [Fact]
    public void Save_InvalidConfig_ThrowsAndLeavesFileUntouched()
    {
        var store = new ConfigStore(P("cfg.json"));
        store.Save(AppConfig.CreateDefault());
        var before = File.ReadAllText(P("cfg.json"));

        var bad = AppConfig.CreateDefault();
        bad.Tables["T"] = new TableRule();   // neither a key nor "ignore"

        var ex = Assert.Throws<ConfigException>(() => store.Save(bad));

        Assert.Contains("'T'", ex.Message);
        Assert.Equal(before, File.ReadAllText(P("cfg.json")));
    }

    [Fact]
    public void Load_BrokenJson_ThrowsConfigExceptionNamingTheFile()
    {
        File.WriteAllText(P("cfg.json"), "{ \"tables\": { oops } }");

        var ex = Assert.Throws<ConfigException>(() => new ConfigStore(P("cfg.json")).LoadOrCreateDefault());

        Assert.Contains("cfg.json", ex.Message);
    }

    [Fact]
    public void Load_TableNamesDifferingOnlyByCase_IsRejected()
    {
        File.WriteAllText(P("cfg.json"), """{ "tables": { "T": { "key": ["A"] }, "t": { "key": ["B"] } } }""");

        Assert.Throws<ConfigException>(() => new ConfigStore(P("cfg.json")).LoadOrCreateDefault());
    }

    [Fact]
    public void Load_AcceptsCommentsAndTrailingCommas_ForHandEditing()
    {
        File.WriteAllText(P("cfg.json"), """
            {
              // keys I maintain by hand
              "tables": { "T": { "key": ["A", "B",], }, },
            }
            """);

        var config = new ConfigStore(P("cfg.json")).LoadOrCreateDefault();

        Assert.Equal(["A", "B"], config.Tables["T"].Key);
    }

    [Fact]
    public void ExampleConfigInRepository_LoadsAndIsValid()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "mdbhub.config.example.json"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var config = new ConfigStore(Path.Combine(dir.FullName, "mdbhub.config.example.json")).LoadOrCreateDefault();

        Assert.Equal(["ID", "SUB_CHEMICAL_NO", "NUM"], config.Tables["TB_ALT"].Key);
        Assert.True(config.Tables["TB_CODE"].Ignore);
        Assert.Equal(["CHEMICAL_NO", "ID"], config.Filters[0].Columns);
    }

    [Fact]
    public void Validate_FlagsDuplicateFilterNamesAndEmptyColumns()
    {
        var config = new AppConfig
        {
            Filters = [new() { Name = "A", Columns = ["X"] }, new() { Name = "a", Columns = [] }],
        };

        var problems = ConfigStore.Validate(config);

        Assert.Contains(problems, p => p.Contains("중복"));
        Assert.Contains(problems, p => p.Contains("컬럼 이름을 하나 이상"));
    }

    [Fact]
    public void KeyResolver_ConfigBeatsDeclaredKey_AndEachSourceIsReported()
    {
        var schema = new DatabaseSchema(
        [
            Table("DECLARED", ["A", "B"], "A"),
            Table("OVERRIDDEN", ["A", "B"], "A"),
            Table("NOKEY", ["A", "B"]),
            Table("SKIPPED", ["A", "B"], "A"),
            Table("BROKEN", ["A", "B"], "A"),
        ]);
        var config = new AppConfig();
        config.Tables["overridden"] = new TableRule { Key = ["b", "a"] };
        config.Tables["SKIPPED"] = new TableRule { Ignore = true };
        config.Tables["BROKEN"] = new TableRule { Key = ["A", "GONE"] };

        var resolved = KeyResolver.Resolve(schema, config);

        Assert.Equal(KeySource.Declared, resolved.Sources["DECLARED"]);
        Assert.Equal(KeySource.Config, resolved.Sources["OVERRIDDEN"]);
        Assert.Equal(["B", "A"], resolved.Schema.Find("OVERRIDDEN")!.PrimaryKey);   // table's own spelling, config's order
        Assert.Equal(KeySource.None, resolved.Sources["NOKEY"]);
        Assert.Equal(KeySource.Ignored, resolved.Sources["SKIPPED"]);
        Assert.Empty(resolved.Schema.Find("SKIPPED")!.PrimaryKey);
        Assert.Equal(["NOKEY", "BROKEN"], resolved.NeedingKey.Select(t => t.Name));   // a bad rule does not fall back to the declared key
        Assert.Contains(resolved.Problems, p => p.Contains("BROKEN") && p.Contains("GONE"));
        Assert.Equal(["SKIPPED"], resolved.IgnoredTables);
    }

    [Fact]
    public void TableComparer_DuplicateKeysOnEitherSide_ThrowsNamingTableAndSide()
    {
        var schema = new TableSchema("T", [new("K", "Integer"), new("V", "Integer")], ["K"]);
        var good = new TableData(schema, [[1, 1], [2, 2]]);
        var dup = new TableData(schema, [[1, 1], [1, 2], [2, 2]]);

        var left = Assert.Throws<DuplicateKeyException>(() => TableComparer.Compare(dup, good, "대상", "패치"));
        var right = Assert.Throws<DuplicateKeyException>(() => TableComparer.Compare(good, dup, "대상", "패치"));

        Assert.Contains("대상", left.Message);
        Assert.Contains("패치", right.Message);
        Assert.Equal("T", left.Table);
        Assert.Contains("(1)", left.Message);
    }
}

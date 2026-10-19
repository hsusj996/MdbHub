namespace MdbHub.Core;

/// <summary>Per-table rule. <see cref="Key"/> identifies a row for compare/apply; <see cref="Ignore"/> leaves the table out of them.</summary>
public sealed class TableRule
{
    public List<string> Key { get; set; } = [];
    public bool Ignore { get; set; }
}

/// <summary>A named extract filter. <see cref="Columns"/> are aliases for one concept (each table uses the first it has).</summary>
public sealed class FilterDefinition
{
    public string Name { get; set; } = "";
    public List<string> Columns { get; set; } = [];
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    /// <summary>Keyed by table name, case-insensitive.</summary>
    public Dictionary<string, TableRule> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<FilterDefinition> Filters { get; set; } = [];

    public static AppConfig CreateDefault() => new()
    {
        Filters =
        [
            new FilterDefinition { Name = "CHEMICAL_NO", Columns = ["CHEMICAL_NO"] },
            new FilterDefinition { Name = "SUB_CHEMICAL_NO", Columns = ["SUB_CHEMICAL_NO"] },
        ],
    };
}

public sealed class ConfigException(string message) : Exception(message);

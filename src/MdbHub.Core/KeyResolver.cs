namespace MdbHub.Core;

public enum KeySource
{
    /// <summary>Key comes from the config file.</summary>
    Config,
    /// <summary>Key is the primary key declared in the mdb.</summary>
    Declared,
    /// <summary>Config says to leave this table out.</summary>
    Ignored,
    /// <summary>No usable key; the table needs to be configured.</summary>
    None,
}

/// <param name="Schema">Same tables as the input, with <see cref="TableSchema.PrimaryKey"/> replaced by the effective key (empty for ignored or key-less tables).</param>
public sealed record ResolvedSchema(DatabaseSchema Schema, IReadOnlyDictionary<string, KeySource> Sources, IReadOnlyList<string> Problems)
{
    public IReadOnlyList<TableSchema> NeedingKey => Schema.Tables.Where(t => Sources[t.Name] == KeySource.None).ToList();
    public IReadOnlyList<string> IgnoredTables => Schema.Tables.Where(t => Sources[t.Name] == KeySource.Ignored).Select(t => t.Name).ToList();
}

public static class KeyResolver
{
    /// <summary>
    /// Key per table: config rule first, otherwise the declared primary key, otherwise none (nothing is guessed).
    /// A configured key naming a column the table does not have is reported as a problem and the table gets no key,
    /// instead of silently falling back to a different key.
    /// </summary>
    public static ResolvedSchema Resolve(DatabaseSchema schema, AppConfig? config)
    {
        var sources = new Dictionary<string, KeySource>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        var tables = new List<TableSchema>();

        foreach (var table in schema.Tables)
        {
            if (config is not null && config.Tables.TryGetValue(table.Name, out var rule))
            {
                if (rule.Ignore)
                {
                    sources[table.Name] = KeySource.Ignored;
                    tables.Add(table with { PrimaryKey = [] });
                    continue;
                }

                var missing = rule.Key.Where(c => !table.HasColumn(c)).ToList();
                if (missing.Count > 0)
                {
                    problems.Add($"[{table.Name}] 설정한 키 컬럼 {string.Join(", ", missing.Select(m => $"'{m}'"))}이(가) 테이블에 없습니다.");
                    sources[table.Name] = KeySource.None;
                    tables.Add(table with { PrimaryKey = [] });
                    continue;
                }

                sources[table.Name] = KeySource.Config;
                tables.Add(table with { PrimaryKey = rule.Key.Select(c => table.Columns[table.IndexOf(c)].Name).ToList() });
                continue;
            }

            sources[table.Name] = table.PrimaryKey.Count > 0 ? KeySource.Declared : KeySource.None;
            tables.Add(table);
        }

        return new ResolvedSchema(new DatabaseSchema(tables), sources, problems);
    }
}

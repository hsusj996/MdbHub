using System.Text.Encodings.Web;
using System.Text.Json;

namespace MdbHub.Core;

/// <summary>Reads and writes the JSON config file. Saving keeps the previous file as ".bak" and swaps the new one in atomically.</summary>
public sealed class ConfigStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // keep Korean readable in the file
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string Path { get; } = path;

    /// <summary>Loads the file; if it does not exist, writes and returns the default config. Throws <see cref="ConfigException"/> when it is unreadable or invalid.</summary>
    public AppConfig LoadOrCreateDefault()
    {
        if (!File.Exists(Path))
        {
            var created = AppConfig.CreateDefault();
            Save(created);
            return created;
        }

        AppConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path), Options);
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"설정 파일 형식이 올바르지 않습니다 ({Path})\n{ex.Message}");
        }
        if (config is null) throw new ConfigException($"설정 파일이 비어 있습니다 ({Path})");

        config = Normalize(config);
        var problems = Validate(config);
        if (problems.Count > 0)
            throw new ConfigException($"설정 파일 내용에 문제가 있습니다 ({Path})\n- " + string.Join("\n- ", problems));
        return config;
    }

    public void Save(AppConfig config)
    {
        var problems = Validate(config);
        if (problems.Count > 0) throw new ConfigException("설정을 저장할 수 없습니다:\n- " + string.Join("\n- ", problems));

        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
        if (File.Exists(Path)) File.Copy(Path, Path + ".bak", overwrite: true);
        File.Move(temp, Path, overwrite: true);
    }

    public static IReadOnlyList<string> Validate(AppConfig config)
    {
        var problems = new List<string>();

        foreach (var (table, rule) in config.Tables)
        {
            if (string.IsNullOrWhiteSpace(table)) problems.Add("테이블 이름이 비어 있는 규칙이 있습니다.");
            if (!rule.Ignore && rule.Key.Count == 0) problems.Add($"테이블 '{table}': 키 컬럼을 지정하거나 '사용 안 함'으로 설정하세요.");
            if (rule.Key.Any(string.IsNullOrWhiteSpace)) problems.Add($"테이블 '{table}': 비어 있는 키 컬럼 이름이 있습니다.");
            if (rule.Key.Distinct(StringComparer.OrdinalIgnoreCase).Count() != rule.Key.Count) problems.Add($"테이블 '{table}': 키 컬럼이 중복되었습니다.");
        }

        foreach (var filter in config.Filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Name)) problems.Add("이름이 없는 필터가 있습니다.");
            if (filter.Columns.Count == 0 || filter.Columns.Any(string.IsNullOrWhiteSpace))
                problems.Add($"필터 '{filter.Name}': 컬럼 이름을 하나 이상, 비어 있지 않게 지정하세요.");
        }
        foreach (var dup in config.Filters.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"필터 이름 '{dup.Key}'이(가) 중복되었습니다.");

        return problems;
    }

    // The JSON deserializer ignores the case-insensitive comparer, so rebuild the table map (and reject names that differ only by case).
    private static AppConfig Normalize(AppConfig config)
    {
        var tables = new Dictionary<string, TableRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rule) in config.Tables ?? [])
        {
            if (!tables.TryAdd(name, rule ?? new TableRule()))
                throw new ConfigException($"설정 파일에 테이블 '{name}'이(가) 대소문자만 다르게 중복되어 있습니다.");
        }
        config.Tables = tables;
        config.Filters ??= [];
        return config;
    }
}

using MdbHub.Core;

namespace MdbHub.App.Infrastructure;

/// <summary>Holds the config loaded from the file next to the exe. A broken file is reported, not overwritten, until the user saves.</summary>
public sealed class ConfigService : ObservableObject
{
    public const string FileName = "mdbhub.config.json";

    private readonly ConfigStore _store;
    private string? _loadError;

    public ConfigService(string path)
    {
        _store = new ConfigStore(path);
        Config = AppConfig.CreateDefault();
        Reload();
    }

    public static string DefaultPath => System.IO.Path.Combine(AppContext.BaseDirectory, FileName);

    public AppConfig Config { get; private set; }
    public string Path => _store.Path;

    /// <summary>Why the file could not be loaded (the app then runs on defaults), or null.</summary>
    public string? LoadError
    {
        get => _loadError;
        private set => Set(ref _loadError, value);
    }

    public event Action? Changed;

    public void Reload()
    {
        try
        {
            Config = _store.LoadOrCreateDefault();
            LoadError = null;
        }
        catch (ConfigException ex)
        {
            Config = AppConfig.CreateDefault();
            LoadError = ex.Message;
        }
        Changed?.Invoke();
    }

    public void Save(AppConfig config)
    {
        _store.Save(config);
        Config = config;
        LoadError = null;
        Changed?.Invoke();
    }

    /// <summary>Adds or replaces rules for tables and saves.</summary>
    public void SaveRules(IReadOnlyDictionary<string, TableRule> rules)
    {
        var updated = new AppConfig { Version = Config.Version, Filters = Config.Filters };
        foreach (var (name, rule) in Config.Tables) updated.Tables[name] = rule;
        foreach (var (name, rule) in rules) updated.Tables[name] = rule;
        Save(updated);
    }
}

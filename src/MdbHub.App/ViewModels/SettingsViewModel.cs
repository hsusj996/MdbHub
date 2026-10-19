using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MdbHub.App.Infrastructure;
using MdbHub.Core;

namespace MdbHub.App.ViewModels;

public sealed class TableRuleRow : ObservableObject
{
    private string _name = "";
    private string _keyText = "";
    private bool _ignore;

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>Key columns, comma separated.</summary>
    public string KeyText { get => _keyText; set => Set(ref _keyText, value); }

    public bool Ignore { get => _ignore; set => Set(ref _ignore, value); }
}

public sealed class FilterRow : ObservableObject
{
    private string _name = "";
    private string _columnsText = "";

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>Alias columns, comma separated.</summary>
    public string ColumnsText { get => _columnsText; set => Set(ref _columnsText, value); }
}

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ConfigService _config;
    private string _status = "";

    public SettingsViewModel(ConfigService config)
    {
        _config = config;
        config.Changed += Load;
        AddTableCommand = new RelayCommand(_ => TableRules.Add(new TableRuleRow()));
        RemoveTableCommand = new RelayCommand(r => TableRules.Remove((TableRuleRow)r!));
        AddFilterCommand = new RelayCommand(_ => Filters.Add(new FilterRow()));
        RemoveFilterCommand = new RelayCommand(r => Filters.Remove((FilterRow)r!));
        SaveCommand = new RelayCommand(_ => Save());
        ReloadCommand = new RelayCommand(_ => _config.Reload());
        Load();
    }

    public ObservableCollection<TableRuleRow> TableRules { get; } = [];
    public ObservableCollection<FilterRow> Filters { get; } = [];

    public ICommand AddTableCommand { get; }
    public ICommand RemoveTableCommand { get; }
    public ICommand AddFilterCommand { get; }
    public ICommand RemoveFilterCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ReloadCommand { get; }

    public string Path => _config.Path;
    public string? LoadError => _config.LoadError;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    private void Load()
    {
        TableRules.Clear();
        foreach (var (name, rule) in _config.Config.Tables.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
            TableRules.Add(new TableRuleRow { Name = name, KeyText = string.Join(", ", rule.Key), Ignore = rule.Ignore });

        Filters.Clear();
        foreach (var f in _config.Config.Filters)
            Filters.Add(new FilterRow { Name = f.Name, ColumnsText = string.Join(", ", f.Columns) });

        Raise(nameof(LoadError));
    }

    private void Save()
    {
        try
        {
            var config = new AppConfig { Version = _config.Config.Version };

            foreach (var row in TableRules)
            {
                var name = row.Name.Trim();
                if (name.Length == 0) throw new ConfigException("테이블 이름이 비어 있는 줄이 있습니다.");
                if (!config.Tables.TryAdd(name, new TableRule { Key = Split(row.KeyText), Ignore = row.Ignore }))
                    throw new ConfigException($"테이블 '{name}'이(가) 두 줄 이상 있습니다.");
            }
            foreach (var row in Filters)
                config.Filters.Add(new FilterDefinition { Name = row.Name.Trim(), Columns = Split(row.ColumnsText) });

            _config.Save(config);
            Status = $"저장했습니다 ({DateTime.Now:HH:mm:ss}). 이전 파일은 .bak으로 남겼습니다.";
        }
        catch (ConfigException ex)
        {
            Dialogs.Error(ex.Message);
        }
        catch (IOException ex)
        {
            Dialogs.Error($"설정 파일을 쓰지 못했습니다: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Dialogs.Error($"설정 파일을 쓸 권한이 없습니다: {ex.Message}\n앱을 쓰기 가능한 폴더에 두세요.");
        }
    }

    private static List<string> Split(string text) =>
        text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

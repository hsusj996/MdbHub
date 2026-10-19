using System.Collections.ObjectModel;
using MdbHub.App.Infrastructure;
using MdbHub.Core;

namespace MdbHub.App.ViewModels;

public sealed class SelectableColumn(string name, bool isSelected) : ObservableObject
{
    private bool _isSelected = isSelected;

    public string Name { get; } = name;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class KeySetupRow : ObservableObject
{
    private bool _ignore;

    public KeySetupRow(TableSchema table, ISet<string> suggestedColumns)
    {
        Table = table.Name;
        Columns = new ObservableCollection<SelectableColumn>(
            table.Columns.Select(c => new SelectableColumn(c.Name, suggestedColumns.Contains(c.Name))));
    }

    public string Table { get; }
    public ObservableCollection<SelectableColumn> Columns { get; }

    public bool Ignore
    {
        get => _ignore;
        set => Set(ref _ignore, value);
    }

    /// <summary>The rule the user chose, or null when they left this table unconfigured.</summary>
    public TableRule? ToRule()
    {
        if (Ignore) return new TableRule { Ignore = true };
        var key = Columns.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        return key.Count == 0 ? null : new TableRule { Key = key };
    }
}

public sealed class KeySetupViewModel(IReadOnlyList<TableSchema> tables, ISet<string> suggestedColumns)
{
    public ObservableCollection<KeySetupRow> Rows { get; } =
        new(tables.Select(t => new KeySetupRow(t, suggestedColumns)));

    public Dictionary<string, TableRule> ToRules()
    {
        var rules = new Dictionary<string, TableRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
            if (row.ToRule() is { } rule) rules[row.Table] = rule;
        return rules;
    }
}

using System.Collections.ObjectModel;
using MdbHub.App.Infrastructure;
using MdbHub.Data;

namespace MdbHub.App.ViewModels;

/// <summary>One table that cannot be grouped by the chosen columns, and what to do with it.</summary>
public sealed class ScopeTableRow(string table) : ObservableObject
{
    private bool _includeAll;

    public string Table { get; } = table;

    /// <summary>Apply all of the table's rows, whatever group they are in.</summary>
    public bool IncludeAll
    {
        get => _includeAll;
        set
        {
            if (!Set(ref _includeAll, value)) return;
            Raise(nameof(Exclude));
        }
    }

    /// <summary>Leave the table out (the safe default).</summary>
    public bool Exclude
    {
        get => !_includeAll;
        set => IncludeAll = !value;
    }
}

public sealed class ScopeTablesViewModel(IReadOnlyList<string> columns, IReadOnlyList<string> tables)
{
    public string ColumnsText { get; } = string.Join(" + ", columns);
    public ObservableCollection<ScopeTableRow> Rows { get; } = new(tables.Select(t => new ScopeTableRow(t)));

    public Dictionary<string, TableScopeChoice> ToChoices() =>
        Rows.ToDictionary(r => r.Table, r => r.IncludeAll ? TableScopeChoice.IncludeAll : TableScopeChoice.Exclude, StringComparer.OrdinalIgnoreCase);
}

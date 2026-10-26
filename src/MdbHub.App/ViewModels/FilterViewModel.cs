using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using MdbHub.App.Infrastructure;

namespace MdbHub.App.ViewModels;

public sealed class ValueItem(object value, Action onChanged) : ObservableObject
{
    private bool _isSelected;

    public object Value { get; } = value;
    public string Text { get; } = Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture) ?? "";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) onChanged();
        }
    }

    internal void SetSilently(bool value)
    {
        _isSelected = value;
        Raise(nameof(IsSelected));
    }
}

/// <summary>Which tables a filter applies to: <see cref="Skipped"/> tables have none of the filter's columns.</summary>
public sealed record FilterCoverage(int Applied, int Total, IReadOnlyList<string> Skipped);

/// <summary>
/// One filter. Its columns are aliases for the same concept (e.g. CHEMICAL_NO and ID): each table uses the first one it has.
/// The checked values are shared by all of them (like an Excel auto-filter over the combined values).
/// </summary>
public sealed class FilterViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<string>, Task<IReadOnlyList<object>>> _loadValues;
    private readonly Func<IReadOnlyList<string>, FilterCoverage> _coverage;
    private string? _columnToAdd;
    private string _searchText = "";
    private bool _isLoading;
    private bool _suppress;
    private int _loadVersion;
    private ObservableCollection<ValueItem> _values = [];
    private ICollectionView _view;

    public FilterViewModel(
        string name,
        IReadOnlyList<string> availableColumns,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<object>>> loadValues,
        Func<IReadOnlyList<string>, FilterCoverage> coverage,
        IEnumerable<string> initialColumns,
        bool isCombination = false)
    {
        IsCombination = isCombination;
        Name = name;
        AvailableColumns = availableColumns;
        _loadValues = loadValues;
        _coverage = coverage;
        _view = CollectionViewSource.GetDefaultView(_values);
        SelectAllVisibleCommand = new RelayCommand(_ => SetVisible(true));
        ClearAllCommand = new RelayCommand(_ => SetVisible(false));
        RemoveColumnCommand = new RelayCommand(c => RemoveColumn((string)c!));
        foreach (var column in initialColumns) SelectedColumns.Add(column);
        if (SelectedColumns.Count > 0) _ = ReloadAsync();   // one load for all initial columns
    }

    /// <summary>
    /// True when the columns together form combinations to choose from (e.g. CHEMICAL_NO + SUB_CHEMICAL_NO → "100 | 1"),
    /// false when they are aliases of one column whose single values are listed.
    /// </summary>
    public bool IsCombination { get; }

    public string Hint => IsCombination
        ? "  ▲ 컬럼을 여러 개 넣으면 그 값의 조합으로 고릅니다"
        : "  ▲ 테이블마다 이름이 다를 때만 컬럼을 더 추가";

    /// <summary>Title of the filter card: the config's filter name, or empty for a filter the user added by hand.</summary>
    public string Name { get; }
    public IReadOnlyList<string> AvailableColumns { get; }
    public ObservableCollection<string> SelectedColumns { get; } = [];
    public ICommand SelectAllVisibleCommand { get; }
    public ICommand ClearAllCommand { get; }
    public ICommand RemoveColumnCommand { get; }

    /// <summary>Bound to the "add column" drop-down: choosing an entry adds it and resets the box.</summary>
    public string? ColumnToAdd
    {
        get => _columnToAdd;
        set
        {
            if (value is null) return;
            AddColumn(value);
            _columnToAdd = null;
            Raise();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) _view.Refresh();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public string CoverageText
    {
        get
        {
            if (SelectedColumns.Count == 0) return "컬럼을 추가하세요.";
            var c = _coverage(SelectedColumns.ToList());
            return $"적용 테이블 {c.Applied} / {c.Total}" + (c.Skipped.Count > 0 ? $"  (제외: {string.Join(", ", c.Skipped)})" : "");
        }
    }

    public ICollectionView View => _view;
    public int TotalCount => _values.Count;
    public int SelectedCount => _values.Count(v => v.IsSelected);
    public IReadOnlyList<object> SelectedValues => _values.Where(v => v.IsSelected).Select(v => v.Value).ToList();

    private void AddColumn(string column)
    {
        if (SelectedColumns.Contains(column, StringComparer.OrdinalIgnoreCase)) return;
        SelectedColumns.Add(column);
        _ = ReloadAsync();
    }

    private void RemoveColumn(string column)
    {
        if (SelectedColumns.Remove(column)) _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var version = ++_loadVersion;
        var columns = SelectedColumns.ToList();
        var keep = _values.Where(v => v.IsSelected).Select(v => v.Text).ToHashSet();   // already checked values survive a column change
        Raise(nameof(CoverageText));

        if (columns.Count == 0)
        {
            Replace([], keep);
            return;
        }

        IsLoading = true;
        try
        {
            var loaded = await _loadValues(columns);
            if (version != _loadVersion) return;   // columns changed again while loading
            Replace(loaded, keep);
        }
        catch (Exception ex)
        {
            if (version == _loadVersion) Dialogs.Error($"값 목록을 읽지 못했습니다: {ex.Message}");
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    private void Replace(IReadOnlyList<object> loaded, HashSet<string> keep)
    {
        var items = loaded.Select(v => new ValueItem(v, OnItemChanged)).ToList();
        foreach (var item in items.Where(i => keep.Contains(i.Text))) item.SetSilently(true);

        _values = new ObservableCollection<ValueItem>(items);
        _view = CollectionViewSource.GetDefaultView(_values);
        _view.Filter = o => _searchText.Length == 0 || ((ValueItem)o).Text.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
        Raise(nameof(View));
        Raise(nameof(TotalCount));
        Raise(nameof(SelectedCount));
    }

    private void OnItemChanged()
    {
        if (!_suppress) Raise(nameof(SelectedCount));
    }

    private void SetVisible(bool selected)
    {
        _suppress = true;
        foreach (var item in _view.Cast<ValueItem>()) item.IsSelected = selected;
        _suppress = false;
        Raise(nameof(SelectedCount));
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using MdbHub.App.Infrastructure;
using MdbHub.Core;
using MdbHub.Data;

namespace MdbHub.App.ViewModels;

public sealed record ApplyModeOption(ApplyMode Mode, string Label);
public sealed record ApplyRow(string Table, int Inserts, int Updates, int Unchanged, string Key);

/// <summary>One checkable group (a combination of values) of the apply scope, e.g. "100 | 1".</summary>
public sealed class ScopeGroupItem(ScopeGroup group, bool isSelected, Action changed) : ObservableObject
{
    private bool _isSelected = isSelected;

    public string Key { get; } = group.Key;
    public string Text { get; } = group.Value.ToString();
    public string Detail { get; } = $"삽입 {group.Inserts:N0} · 덮어쓰기 {group.Updates:N0} · 테이블 {group.Tables}개";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) changed();
        }
    }

    internal void SetSilently(bool value)
    {
        _isSelected = value;
        Raise(nameof(IsSelected));
    }
}

public sealed class ApplyViewModel : ViewModelBase
{
    private readonly ConfigService _config;
    private string? _patchPath;
    private string? _targetPath;
    private ApplyMode _mode = ApplyMode.Upsert;
    private bool _makeBackup = true;
    private ApplyPlan? _plan;
    private string _summary = "";
    private ApplyRow? _selectedRow;
    private SideBySide? _detail;
    private string _detailMessage = "";
    private bool _isLoadingDetail;
    private int _currentChange = -1;
    private int _detailVersion;

    public ApplyViewModel(ConfigService config)
    {
        _config = config;
        config.Changed += InvalidatePreview;   // the keys may have changed, so the old preview no longer holds
        BrowsePatchCommand = new RelayCommand(_ => PatchPath = Dialogs.OpenMdb("반영할 내용이 담긴 mdb 선택") ?? PatchPath, _ => NotBusy);
        BrowseTargetCommand = new RelayCommand(_ => TargetPath = Dialogs.OpenMdb("반영 대상 mdb 선택") ?? TargetPath, _ => NotBusy);
        PreviewCommand = new RelayCommand(_ => Preview(), _ => NotBusy && HasBothPaths);
        ApplyCommand = new RelayCommand(_ => Apply(), _ => NotBusy && CanApply);
        NextChangeCommand = new RelayCommand(_ => MoveToChange(+1), _ => HasChanges);
        PreviousChangeCommand = new RelayCommand(_ => MoveToChange(-1), _ => HasChanges);
        ClearEditsCommand = new RelayCommand(_ => ClearEdits(), _ => EditCount > 0);

        ScopeGroupsView = CollectionViewSource.GetDefaultView(ScopeGroups);
        ScopeGroupsView.Filter = o => _scopeSearch.Length == 0 || ((ScopeGroupItem)o).Text.Contains(_scopeSearch, StringComparison.OrdinalIgnoreCase);
        RemoveScopeColumnCommand = new RelayCommand(c => RemoveScopeColumn((string)c!));
        SelectAllGroupsCommand = new RelayCommand(_ => SetVisibleGroups(true), _ => ScopeGroups.Count > 0);
        ClearGroupsCommand = new RelayCommand(_ => SetVisibleGroups(false), _ => ScopeGroups.Count > 0);
    }

    // ---- limiting the apply to chosen groups of rows ----
    // The user picks key columns (any, since they differ between mdb files: CHEMICAL_NO + SUB_CHEMICAL_NO, CHEM_ID, ...); the plan's rows
    // are grouped by the values of those columns and the user checks the groups to apply. _plan stays the full preview;
    // _effective is it limited to the checked groups and is what the table list, the detail view, the counts and the apply use.

    private readonly Dictionary<string, TableScopeChoice> _tableChoices = new(StringComparer.OrdinalIgnoreCase);
    private ApplyPlan? _effective;
    private bool _bulkScopeChange;
    private string _scopeSearch = "";
    private string? _scopeColumnToAdd;

    private ApplyPlan? ActivePlan => _effective ?? _plan;

    /// <summary>Key columns the tables of the preview have: what a scope can be based on.</summary>
    public ObservableCollection<string> AvailableScopeColumns { get; } = [];

    /// <summary>The chosen columns; empty means no limit (everything is applied).</summary>
    public ObservableCollection<string> ScopeColumns { get; } = [];

    public ObservableCollection<ScopeGroupItem> ScopeGroups { get; } = [];
    public ICollectionView ScopeGroupsView { get; }
    public ICommand RemoveScopeColumnCommand { get; }
    public ICommand SelectAllGroupsCommand { get; }
    public ICommand ClearGroupsCommand { get; }

    public bool HasPlan => _plan is not null;
    public bool HasScopeColumns => ScopeColumns.Count > 0;

    public string ScopeText => ScopeColumns.Count == 0 ? "" : $"조합 {ScopeGroups.Count:N0}개 중 {ScopeGroups.Count(g => g.IsSelected):N0}개 선택";

    public string ScopeSearch
    {
        get => _scopeSearch;
        set
        {
            if (Set(ref _scopeSearch, value)) ScopeGroupsView.Refresh();
        }
    }

    /// <summary>Bound to the "add column" drop-down: choosing an entry adds it and resets the box.</summary>
    public string? ScopeColumnToAdd
    {
        get => _scopeColumnToAdd;
        set
        {
            if (value is null) return;
            AddScopeColumn(value);
            _scopeColumnToAdd = null;
            Raise();
        }
    }

    private void AddScopeColumn(string column)
    {
        if (_plan is null || ScopeColumns.Contains(column, StringComparer.OrdinalIgnoreCase)) return;

        ScopeColumns.Add(column);
        if (!RebuildScopeGroups(previousSelection: null)) ScopeColumns.Remove(column);   // the user backed out: keep the earlier scope
        Raise(nameof(HasScopeColumns));
    }

    private void RemoveScopeColumn(string column)
    {
        if (!ScopeColumns.Remove(column)) return;
        RebuildScopeGroups(previousSelection: null);
        Raise(nameof(HasScopeColumns));
    }

    /// <summary>
    /// Lists the groups for the chosen columns and applies them. Tables whose key lacks one of the columns cannot be grouped:
    /// the user decides for each (apply all of it, or leave it out). Returns false when the user cancelled that question
    /// (nothing is changed then).
    /// </summary>
    private bool RebuildScopeGroups(IReadOnlyDictionary<string, bool>? previousSelection)
    {
        var plan = _plan!;
        var columns = ScopeColumns.ToList();

        if (columns.Count == 0)
        {
            ScopeGroups.Clear();
            _tableChoices.Clear();
            ApplyEffective();
            return true;
        }

        var lacking = plan.TablesLacking(columns);
        var undecided = lacking.Where(t => !_tableChoices.ContainsKey(t)).ToList();
        if (undecided.Count > 0)
        {
            var answer = Dialogs.AskScopeTables(columns, undecided);
            if (answer is null) return false;
            foreach (var table in undecided) _tableChoices[table] = answer.TryGetValue(table, out var choice) ? choice : TableScopeChoice.Exclude;
        }
        foreach (var stale in _tableChoices.Keys.Where(t => !lacking.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList()) _tableChoices.Remove(stale);

        var before = previousSelection ?? ScopeGroups.ToDictionary(g => g.Key, g => g.IsSelected);
        _bulkScopeChange = true;
        ScopeGroups.Clear();
        foreach (var group in plan.Groups(columns))
            ScopeGroups.Add(new ScopeGroupItem(group, before.TryGetValue(group.Key, out var wasOn) ? wasOn : true, OnScopeGroupChanged));
        _bulkScopeChange = false;

        ApplyEffective();
        return true;
    }

    private void OnScopeGroupChanged()
    {
        if (!_bulkScopeChange) ApplyEffective();
    }

    // "Select all" / "none" act on what the search box currently shows.
    private void SetVisibleGroups(bool selected)
    {
        _bulkScopeChange = true;
        foreach (var item in ScopeGroupsView.Cast<ScopeGroupItem>()) item.SetSilently(selected);
        _bulkScopeChange = false;
        ApplyEffective();
    }

    private ApplyScope? BuildScope() =>
        ScopeColumns.Count == 0
            ? null
            : new ApplyScope(ScopeColumns.ToList(), ScopeGroups.Where(g => g.IsSelected).Select(g => g.Key).ToHashSet(), _tableChoices);

    /// <summary>The scope as it was before a new preview, so it can be put back (columns, which groups were checked, the table decisions).</summary>
    private (List<string> Columns, Dictionary<string, bool> Groups, Dictionary<string, TableScopeChoice> Choices)? CaptureScope() =>
        ScopeColumns.Count == 0
            ? null
            : (ScopeColumns.ToList(), ScopeGroups.ToDictionary(g => g.Key, g => g.IsSelected), new Dictionary<string, TableScopeChoice>(_tableChoices));

    private bool RestoreScope((List<string> Columns, Dictionary<string, bool> Groups, Dictionary<string, TableScopeChoice> Choices)? memo)
    {
        if (memo is not { } m || _plan is null) return false;
        var available = _plan.KeyColumns;
        if (!m.Columns.All(c => available.Contains(c, StringComparer.OrdinalIgnoreCase))) return false;

        foreach (var column in m.Columns) ScopeColumns.Add(column);
        foreach (var (table, choice) in m.Choices) _tableChoices[table] = choice;
        if (RebuildScopeGroups(m.Groups))
        {
            Raise(nameof(HasScopeColumns));
            return true;
        }

        ScopeColumns.Clear();   // the user declined to decide the tables again: no limit
        _tableChoices.Clear();
        return false;
    }

    private void ApplyEffective()
    {
        if (_plan is null) return;

        var scope = BuildScope();
        _effective = scope is null ? null : _plan.WithScope(scope);
        var dropped = PruneEdits();

        var keep = _selectedRow?.Table;
        var plan = ActivePlan!;
        Rows.Clear();
        foreach (var t in plan.Tables)
            Rows.Add(new ApplyRow(t.Target.Name, t.Inserts, t.Updates, t.Unchanged, KeyText(t.Target)));

        _selectedRow = Rows.FirstOrDefault(r => string.Equals(r.Table, keep, StringComparison.OrdinalIgnoreCase))
                       ?? Rows.FirstOrDefault(r => r.Inserts + r.Updates > 0);   // otherwise the first table that will change
        Raise(nameof(SelectedRow));

        var summary = Describe(plan);
        if (ScopeSummary() is { Length: > 0 } scopeNote) summary += "\n" + scopeNote;
        if (dropped > 0) summary += $"\n✎ 범위 밖이 된 행의 직접 수정 {dropped:N0}개를 취소했습니다.";
        Summary = summary;

        Raise(nameof(ScopeText));
        Raise(nameof(HasPlan));
        CommandManager.InvalidateRequerySuggested();
        _ = ShowDetailAsync();
    }

    // An edit of a row that is no longer in the plan (it left the scope) cannot be applied, so it is dropped.
    private int PruneEdits()
    {
        if (_edits.Count == 0 || ActivePlan is not { } plan) return 0;

        var gone = _edits.Where(kv => !plan.Contains(kv.Value.Table, kv.Value.Key)).Select(kv => kv.Key).ToList();
        foreach (var key in gone) _edits.Remove(key);
        if (gone.Count > 0) RaiseEditsChanged();
        return gone.Count;
    }

    /// <summary>What the scope is, for the summary and the confirmation: the columns, how many groups are checked, the table decisions.</summary>
    private string ScopeSummary()
    {
        if (ScopeColumns.Count == 0) return "";

        var text = $"범위: {string.Join(" + ", ScopeColumns)} — {ScopeText}";
        if (_tableChoices.Count > 0)
            text += "\n기준 컬럼이 키에 없는 테이블: " + string.Join(", ",
                _tableChoices.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => $"{kv.Key}({(kv.Value == TableScopeChoice.IncludeAll ? "전부 반영" : "제외")})"));
        return text;
    }

    public ICommand NextChangeCommand { get; }
    public ICommand PreviousChangeCommand { get; }
    public ICommand ClearEditsCommand { get; }

    // Values the user changed by hand in the preview, keyed by table + row key + column. They live only in memory:
    // the patch file is never modified, and they are applied on top of the patch rows when applying.
    private readonly Dictionary<string, CellEdit> _edits = [];
    private TableApplyPlan? _currentTable;
    private Dictionary<string, RowDiff> _rowIndex = [];

    public int EditCount => _edits.Count;
    public string EditText => _edits.Count == 0 ? "" : $"직접 수정한 값 {_edits.Count:N0}개";

    private void RaiseEditsChanged()
    {
        Raise(nameof(EditCount));
        Raise(nameof(EditText));
    }

    private static string EditKey(string table, IReadOnlyList<object?> key, string column) =>
        $"{table.ToUpperInvariant()}\u001e{RowKey.Of(key)}\u001e{column.ToUpperInvariant()}";

    /// <summary>Called before a typed value is accepted: returns why it cannot be used, or null.</summary>
    public string? ValidateEdit(SideRow row, int column, string text)
    {
        if (_currentTable is null || !_rowIndex.TryGetValue(RowKey.Of(row.Key), out var diffRow))
            return "수정할 행을 찾지 못했습니다. 미리보기를 다시 만드세요.";

        var name = _currentTable.Target.Columns[column].Name;
        if (_currentTable.Target.PrimaryKey.Contains(name, StringComparer.OrdinalIgnoreCase))
            return "키 컬럼은 수정할 수 없습니다.";

        var sourceIndex = _currentTable.Source.IndexOf(name);
        if (sourceIndex < 0) return $"반영할 mdb에 '{name}' 컬럼이 없습니다.";

        var sample = diffRow.Right![sourceIndex] ?? diffRow.Left?[column];
        return CellValueParser.TryParse(_currentTable.Source.Columns[sourceIndex], sample, text, out _, out var error) ? null : error;
    }

    /// <summary>Records an accepted edit (or drops it when the value was typed back to the patch's own value).</summary>
    public void OnCellEdited(SideRow row, int column, string text)
    {
        if (_currentTable is null || !_rowIndex.TryGetValue(RowKey.Of(row.Key), out var diffRow)) return;

        var name = _currentTable.Target.Columns[column].Name;
        var sourceIndex = _currentTable.Source.IndexOf(name);
        var oldValue = diffRow.Right![sourceIndex];
        var sample = oldValue ?? diffRow.Left?[column];
        if (!CellValueParser.TryParse(_currentTable.Source.Columns[sourceIndex], sample, text, out var newValue, out _)) return;

        var key = EditKey(_currentTable.Target.Name, diffRow.Key, name);
        if (TableComparer.ValueEquals(newValue, oldValue))
        {
            _edits.Remove(key);
            row.EditedColumns.Remove(column);
        }
        else
        {
            _edits[key] = new CellEdit(_currentTable.Target.Name, diffRow.Key, name, oldValue, newValue);
            row.EditedColumns.Add(column);
        }

        row.Cells[column] = SideBySide.FormatValue(newValue, markNull: true);   // show the typed value in its normal form
        row.RefreshCells();
        RaiseEditsChanged();
    }

    private void ClearEdits()
    {
        _edits.Clear();
        RaiseEditsChanged();
        _ = ShowDetailAsync();   // rebuild the lines so every cell shows the patch's own value again
    }

    private void ShowEditsIn(TableApplyPlan table, SideBySide sides)
    {
        var mine = _edits.Values.Where(e => string.Equals(e.Table, table.Target.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0) return;

        var rightByKey = sides.Right.Where(r => r.Kind is SideRowKind.Changed or SideRowKind.RightOnly).ToDictionary(r => RowKey.Of(r.Key));
        foreach (var edit in mine)
        {
            var column = table.Target.IndexOf(edit.Column);
            if (column < 0 || !rightByKey.TryGetValue(RowKey.Of(edit.Key), out var row)) continue;
            row.Cells[column] = SideBySide.FormatValue(edit.NewValue, markNull: true);
            row.EditedColumns.Add(column);
        }
    }

    /// <summary>Asks the view to scroll both grids so that this list index is visible (and selected).</summary>
    public event Action<int>? ScrollToRowRequested;

    /// <summary>The table whose changes are shown in detail below the table list.</summary>
    public ApplyRow? SelectedRow
    {
        get => _selectedRow;
        set { if (Set(ref _selectedRow, value)) _ = ShowDetailAsync(); }
    }

    /// <summary>The selected table's changes: current target rows on the left, the rows as they will be after applying on the right.</summary>
    public SideBySide? Detail
    {
        get => _detail;
        private set
        {
            if (!Set(ref _detail, value)) return;
            _currentChange = -1;
            Raise(nameof(DetailText));
            Raise(nameof(HasChanges));
        }
    }

    public string DetailMessage
    {
        get => _detailMessage;
        private set => Set(ref _detailMessage, value);
    }

    public bool IsLoadingDetail
    {
        get => _isLoadingDetail;
        private set => Set(ref _isLoadingDetail, value);
    }

    public bool HasChanges => _detail is { DifferenceRows.Count: > 0 };

    public string DetailText
    {
        get
        {
            if (_detail is null) return "";
            var position = _currentChange < 0 ? "-" : (_currentChange + 1).ToString("N0");
            var text = $"변경 {position} / {_detail.DifferenceRows.Count:N0}   (삽입 {_detail.RightOnlyCount:N0}행, 덮어쓰기 {_detail.ChangedCount:N0}행)";
            if (_mode == ApplyMode.InsertOnly && _detail.ChangedCount > 0)
                text += "   ⚠ 삽입 전용 방식에서는 덮어쓸 행(노랑)이 있으면 반영이 중단됩니다.";
            return text;
        }
    }

    public IReadOnlyList<ApplyModeOption> Modes { get; } =
    [
        new(ApplyMode.Upsert, "덮어쓰기 + 삽입 (키가 같으면 덮어쓰고, 없으면 삽입)"),
        new(ApplyMode.InsertOnly, "삽입만 (기존 행을 바꿔야 하면 중단)"),
    ];

    public ObservableCollection<ApplyRow> Rows { get; } = [];

    public ICommand BrowsePatchCommand { get; }
    public ICommand BrowseTargetCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand ApplyCommand { get; }

    public string? PatchPath
    {
        get => _patchPath;
        set { if (Set(ref _patchPath, value)) InvalidatePreview(); }
    }

    public string? TargetPath
    {
        get => _targetPath;
        set { if (Set(ref _targetPath, value)) InvalidatePreview(); }
    }

    public ApplyMode Mode
    {
        get => _mode;
        set { if (Set(ref _mode, value)) InvalidatePreview(); }
    }

    public bool MakeBackup
    {
        get => _makeBackup;
        set => Set(ref _makeBackup, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    private bool HasBothPaths => !string.IsNullOrWhiteSpace(_patchPath) && !string.IsNullOrWhiteSpace(_targetPath);
    private bool CanApply => ActivePlan is { } plan && plan.TotalInserts + plan.TotalUpdates > 0;

    private void InvalidatePreview()
    {
        var lostEdits = _edits.Count;
        _edits.Clear();
        _plan = null;
        _effective = null;
        _tableChoices.Clear();
        ScopeColumns.Clear();
        ScopeGroups.Clear();
        AvailableScopeColumns.Clear();
        Rows.Clear();
        SelectedRow = null;
        RaiseEditsChanged();
        Raise(nameof(HasPlan));
        Raise(nameof(HasScopeColumns));
        Raise(nameof(ScopeText));
        Summary = lostEdits > 0 ? $"파일이나 설정이 바뀌어 미리보기와 직접 수정한 값 {lostEdits}개를 초기화했습니다." : "";
    }

    private async Task ShowDetailAsync()
    {
        var version = ++_detailVersion;
        var row = _selectedRow;
        Detail = null;
        DetailMessage = "";
        IsLoadingDetail = false;
        _currentTable = null;
        _rowIndex = [];

        var table = row is null ? null : ActivePlan?.Tables.FirstOrDefault(t => string.Equals(t.Target.Name, row.Table, StringComparison.OrdinalIgnoreCase));
        if (table is null) return;
        if (table.Inserts + table.Updates == 0)
        {
            DetailMessage = $"{table.Target.Name}: 변경할 내용이 없습니다 (대상과 동일).";
            return;
        }

        IsLoadingDetail = true;
        try
        {
            // Only rows the apply would write: patch rows that are new (insert) or differ from the target (overwrite).
            var sides = await Task.Run(() =>
                SideBySide.Build(TableAligner.FromDiff(table.Diff, table.Target, table.Source, includeLeftOnly: false), table.Target, onlyDifferences: false));
            if (version != _detailVersion) return;

            _currentTable = table;
            _rowIndex = table.Diff.Rows.Where(r => r.Kind != RowDiffKind.Removed).ToDictionary(r => RowKey.Of(r.Key));
            ShowEditsIn(table, sides);   // edits made earlier (before switching tables) show up again
            Detail = sides;
        }
        catch (Exception ex)
        {
            if (version == _detailVersion) DetailMessage = $"{table.Target.Name}: 변경 내용을 만들지 못했습니다.\n{ex.Message}";
        }
        finally
        {
            if (version == _detailVersion) IsLoadingDetail = false;
        }
    }

    private void MoveToChange(int step)
    {
        if (_detail is null || _detail.DifferenceRows.Count == 0) return;

        var count = _detail.DifferenceRows.Count;
        _currentChange = _currentChange < 0
            ? (step > 0 ? 0 : count - 1)
            : (_currentChange + step + count) % count;

        Raise(nameof(DetailText));
        ScrollToRowRequested?.Invoke(_detail.DifferenceRows[_currentChange]);
    }

    private void Preview()
    {
        if (_edits.Count > 0 && !Dialogs.Confirm($"미리보기를 다시 만들면 직접 수정한 값 {_edits.Count}개가 사라집니다.\n계속할까요?")) return;

        var (patch, target) = (_patchPath!, _targetPath!);
        var scopeMemo = CaptureScope();   // a new preview of the same files keeps the chosen scope
        _ = RunAsync(async () =>
        {
            InvalidatePreview();

            // Tables in the patch with no key at all: ask the user once, then remember the answer in the config file.
            var patchSchema = await Task.Run(() => KeyResolver.Resolve(MdbReader.ReadSchema(patch), _config.Config));
            KeyConfigurator.PromptForMissing(_config, patchSchema);

            var config = _config.Config;
            var plan = await Task.Run(() => MdbApplier.Preview(patch, target, config));
            _plan = plan;
            Raise(nameof(HasPlan));

            foreach (var column in plan.KeyColumns.Order(StringComparer.OrdinalIgnoreCase)) AvailableScopeColumns.Add(column);
            if (!RestoreScope(scopeMemo)) ApplyEffective();   // fills the table list, the summary and shows the first table that will change
        });
    }

    private string KeyText(TableSchema table) =>
        $"{string.Join(", ", table.PrimaryKey)} ({(_config.Config.Tables.ContainsKey(table.Name) ? "설정" : "mdb 기본 키")})";

    private static string Describe(ApplyPlan plan)
    {
        var text = plan.TotalInserts + plan.TotalUpdates == 0
            ? "변경할 내용이 없습니다 (대상과 동일)."
            : $"삽입 {plan.TotalInserts:N0}행, 덮어쓰기 {plan.TotalUpdates:N0}행";
        if (plan.SkippedTablesWithoutPrimaryKey.Count > 0)
            text += $"\n⚠ 키가 없어 제외된 테이블: {string.Join(", ", plan.SkippedTablesWithoutPrimaryKey)}";
        if (plan.KeyProblems.Count > 0)
            text += "\n⚠ " + string.Join("\n⚠ ", plan.KeyProblems);
        return text;
    }

    private void Apply()
    {
        var (patch, target, mode, backup) = (_patchPath!, _targetPath!, _mode, _makeBackup);

        // The counts shown here are those after the hand edits (an overwrite edited back to the target's value no longer counts).
        var edits = _edits.Values.ToList();
        var scope = BuildScope();
        var scopeNote = ScopeSummary();
        ApplyPlan plan;
        try { plan = ActivePlan!.WithEdits(edits); }
        catch (InvalidOperationException ex) { Dialogs.Error(ex.Message); return; }

        var skipped = plan.SkippedTablesWithoutPrimaryKey.Count > 0
            ? $"\n\n⚠ 키가 없어 반영하지 않는 테이블: {string.Join(", ", plan.SkippedTablesWithoutPrimaryKey)}"
            : "";
        var edited = edits.Count > 0
            ? $"\n\n✎ 직접 수정한 값 {edits.Count:N0}개가 반영됩니다 (테이블 {edits.Select(e => e.Table).Distinct(StringComparer.OrdinalIgnoreCase).Count()}개).\n   반영할 mdb 파일은 바뀌지 않고, 수정 내역은 대상 옆 .edits.csv 파일로 남습니다."
            : "";
        var limited = scopeNote.Length > 0 ? $"\n\n▣ {scopeNote.Replace("\n", "\n   ")}\n   선택하지 않은 조합은 반영하지 않습니다." : "";
        var message = $"대상 파일을 수정합니다.\n\n{target}\n\n삽입 {plan.TotalInserts:N0}행, 덮어쓰기 {plan.TotalUpdates:N0}행{limited}{edited}{skipped}\n\n" +
                      (backup ? "수정 전에 백업을 만듭니다." : "⚠ 백업을 만들지 않습니다.") + "\n\n계속할까요?";
        if (!Dialogs.Confirm(message)) return;

        var config = _config.Config;
        _ = RunAsync(async () =>
        {
            var result = await Task.Run(() => MdbApplier.Apply(patch, target, mode, backup, config, edits, scope));
            InvalidatePreview();
            Summary = $"반영 완료: 삽입 {result.Plan.TotalInserts:N0}행, 덮어쓰기 {result.Plan.TotalUpdates:N0}행" +
                      (result.BackupPath is null ? "" : $"  (백업: {result.BackupPath})") +
                      (scopeNote.Length > 0 ? "\n" + scopeNote : "") +
                      (result.EditLogPath is null ? "" : $"\n직접 수정한 값 {edits.Count:N0}개의 내역: {result.EditLogPath}");
            Dialogs.Info(Summary);
        });
    }
}

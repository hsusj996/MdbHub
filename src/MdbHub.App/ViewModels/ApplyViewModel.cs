using System.Collections.ObjectModel;
using System.Windows.Input;
using MdbHub.App.Infrastructure;
using MdbHub.Core;
using MdbHub.Data;

namespace MdbHub.App.ViewModels;

public sealed record ApplyModeOption(ApplyMode Mode, string Label);
public sealed record ApplyRow(string Table, int Inserts, int Updates, int Unchanged, string Key);

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
    private bool CanApply => _plan is not null && _plan.TotalInserts + _plan.TotalUpdates > 0;

    private void InvalidatePreview()
    {
        var lostEdits = _edits.Count;
        _edits.Clear();
        _plan = null;
        Rows.Clear();
        SelectedRow = null;
        RaiseEditsChanged();
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

        var table = row is null ? null : _plan?.Tables.FirstOrDefault(t => string.Equals(t.Target.Name, row.Table, StringComparison.OrdinalIgnoreCase));
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
        _ = RunAsync(async () =>
        {
            InvalidatePreview();

            // Tables in the patch with no key at all: ask the user once, then remember the answer in the config file.
            var patchSchema = await Task.Run(() => KeyResolver.Resolve(MdbReader.ReadSchema(patch), _config.Config));
            KeyConfigurator.PromptForMissing(_config, patchSchema);

            var config = _config.Config;
            var plan = await Task.Run(() => MdbApplier.Preview(patch, target, config));
            _plan = plan;

            foreach (var t in plan.Tables)
                Rows.Add(new ApplyRow(t.Target.Name, t.Inserts, t.Updates, t.Unchanged, KeyText(t.Target)));

            Summary = Describe(plan);
            SelectedRow = Rows.FirstOrDefault(r => r.Inserts + r.Updates > 0);   // show the first table that will change
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
        ApplyPlan plan;
        try { plan = _plan!.WithEdits(edits); }
        catch (InvalidOperationException ex) { Dialogs.Error(ex.Message); return; }

        var skipped = plan.SkippedTablesWithoutPrimaryKey.Count > 0
            ? $"\n\n⚠ 키가 없어 반영하지 않는 테이블: {string.Join(", ", plan.SkippedTablesWithoutPrimaryKey)}"
            : "";
        var edited = edits.Count > 0
            ? $"\n\n✎ 직접 수정한 값 {edits.Count:N0}개가 반영됩니다 (테이블 {edits.Select(e => e.Table).Distinct(StringComparer.OrdinalIgnoreCase).Count()}개).\n   반영할 mdb 파일은 바뀌지 않고, 수정 내역은 대상 옆 .edits.csv 파일로 남습니다."
            : "";
        var message = $"대상 파일을 수정합니다.\n\n{target}\n\n삽입 {plan.TotalInserts:N0}행, 덮어쓰기 {plan.TotalUpdates:N0}행{edited}{skipped}\n\n" +
                      (backup ? "수정 전에 백업을 만듭니다." : "⚠ 백업을 만들지 않습니다.") + "\n\n계속할까요?";
        if (!Dialogs.Confirm(message)) return;

        var config = _config.Config;
        _ = RunAsync(async () =>
        {
            var result = await Task.Run(() => MdbApplier.Apply(patch, target, mode, backup, config, edits));
            InvalidatePreview();
            Summary = $"반영 완료: 삽입 {result.Plan.TotalInserts:N0}행, 덮어쓰기 {result.Plan.TotalUpdates:N0}행" +
                      (result.BackupPath is null ? "" : $"  (백업: {result.BackupPath})") +
                      (result.EditLogPath is null ? "" : $"\n직접 수정한 값 {edits.Count:N0}개의 내역: {result.EditLogPath}");
            Dialogs.Info(Summary);
        });
    }
}

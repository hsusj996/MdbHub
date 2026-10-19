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

    public ApplyViewModel(ConfigService config)
    {
        _config = config;
        config.Changed += InvalidatePreview;   // the keys may have changed, so the old preview no longer holds
        BrowsePatchCommand = new RelayCommand(_ => PatchPath = Dialogs.OpenMdb("반영할 내용이 담긴 mdb 선택") ?? PatchPath, _ => NotBusy);
        BrowseTargetCommand = new RelayCommand(_ => TargetPath = Dialogs.OpenMdb("반영 대상 mdb 선택") ?? TargetPath, _ => NotBusy);
        PreviewCommand = new RelayCommand(_ => Preview(), _ => NotBusy && HasBothPaths);
        ApplyCommand = new RelayCommand(_ => Apply(), _ => NotBusy && CanApply);
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
        _plan = null;
        Rows.Clear();
        Summary = "";
    }

    private void Preview()
    {
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
        var plan = _plan!;
        var (patch, target, mode, backup) = (_patchPath!, _targetPath!, _mode, _makeBackup);

        var skipped = plan.SkippedTablesWithoutPrimaryKey.Count > 0
            ? $"\n\n⚠ 키가 없어 반영하지 않는 테이블: {string.Join(", ", plan.SkippedTablesWithoutPrimaryKey)}"
            : "";
        var message = $"대상 파일을 수정합니다.\n\n{target}\n\n삽입 {plan.TotalInserts:N0}행, 덮어쓰기 {plan.TotalUpdates:N0}행{skipped}\n\n" +
                      (backup ? "수정 전에 백업을 만듭니다." : "⚠ 백업을 만들지 않습니다.") + "\n\n계속할까요?";
        if (!Dialogs.Confirm(message)) return;

        var config = _config.Config;
        _ = RunAsync(async () =>
        {
            var result = await Task.Run(() => MdbApplier.Apply(patch, target, mode, backup, config));
            InvalidatePreview();
            Summary = $"반영 완료: 삽입 {result.Plan.TotalInserts:N0}행, 덮어쓰기 {result.Plan.TotalUpdates:N0}행" +
                      (result.BackupPath is null ? "" : $"  (백업: {result.BackupPath})");
            Dialogs.Info(Summary);
        });
    }
}

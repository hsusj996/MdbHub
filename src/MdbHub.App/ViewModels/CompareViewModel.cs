using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using MdbHub.App.Infrastructure;
using MdbHub.Core;
using MdbHub.Data;

namespace MdbHub.App.ViewModels;

/// <param name="Match">Which side(s) have the table.</param>
/// <param name="Note">Why the table cannot be viewed side by side (empty when it can).</param>
public sealed record CompareRow(string Table, int LeftOnly, int RightOnly, int Changed, int Unchanged, string Key, string Note, TableMatchKind Match)
{
    public bool IsDifferent => LeftOnly + RightOnly + Changed > 0;
}

public sealed class CompareViewModel : ViewModelBase
{
    private readonly ConfigService _config;
    private readonly ICollectionView _tablesView;
    private DatabaseSchema? _leftSchema;
    private DatabaseSchema? _rightSchema;
    private string? _leftPath;
    private string? _rightPath;
    private string? _comparedLeft;
    private string? _comparedRight;
    private bool _onlyDifferent;
    private bool _onlyDifferentRows;
    private CompareRow? _selected;
    private IReadOnlyList<AlignedRow>? _aligned;
    private TableSchema? _alignedSchema;
    private SideBySide? _sides;
    private string _sideMessage = "";
    private bool _isLoadingSides;
    private int _currentDifference = -1;
    private int _sidesVersion;
    private string _summary = "";

    public CompareViewModel(ConfigService config)
    {
        _config = config;
        config.Changed += Reset;   // the keys may have changed, so earlier results no longer hold
        _tablesView = CollectionViewSource.GetDefaultView(Tables);
        _tablesView.Filter = o => !_onlyDifferent || ((CompareRow)o).IsDifferent;
        BrowseLeftCommand = new RelayCommand(_ => LeftPath = Dialogs.OpenMdb("왼쪽 mdb 선택") ?? LeftPath, _ => NotBusy);
        BrowseRightCommand = new RelayCommand(_ => RightPath = Dialogs.OpenMdb("오른쪽 mdb 선택") ?? RightPath, _ => NotBusy);
        CompareCommand = new RelayCommand(_ => Compare(), _ => NotBusy && !string.IsNullOrWhiteSpace(_leftPath) && !string.IsNullOrWhiteSpace(_rightPath));
        NextDifferenceCommand = new RelayCommand(_ => MoveToDifference(+1), _ => HasDifferences);
        PreviousDifferenceCommand = new RelayCommand(_ => MoveToDifference(-1), _ => HasDifferences);
    }

    public ObservableCollection<CompareRow> Tables { get; } = [];
    public ICommand BrowseLeftCommand { get; }
    public ICommand BrowseRightCommand { get; }
    public ICommand CompareCommand { get; }
    public ICommand NextDifferenceCommand { get; }
    public ICommand PreviousDifferenceCommand { get; }
    public ICollectionView TablesView => _tablesView;

    /// <summary>Asks the view to scroll both grids so that this list index is visible (and selected).</summary>
    public event Action<int>? ScrollToRowRequested;

    public string? LeftPath
    {
        get => _leftPath;
        set { if (Set(ref _leftPath, value)) Reset(); }
    }

    public string? RightPath
    {
        get => _rightPath;
        set { if (Set(ref _rightPath, value)) Reset(); }
    }

    /// <summary>Hides identical tables from the table list.</summary>
    public bool OnlyDifferent
    {
        get => _onlyDifferent;
        set { if (Set(ref _onlyDifferent, value)) _tablesView.Refresh(); }
    }

    /// <summary>Hides identical lines inside the side-by-side view.</summary>
    public bool OnlyDifferentRows
    {
        get => _onlyDifferentRows;
        set { if (Set(ref _onlyDifferentRows, value)) _ = RebuildSidesAsync(); }
    }

    public CompareRow? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) _ = LoadSidesAsync(); }
    }

    public SideBySide? Sides
    {
        get => _sides;
        private set
        {
            if (!Set(ref _sides, value)) return;
            _currentDifference = -1;
            Raise(nameof(DifferenceText));
            Raise(nameof(HasDifferences));
        }
    }

    /// <summary>Shown instead of the grids when the selected table cannot be viewed side by side.</summary>
    public string SideMessage
    {
        get => _sideMessage;
        private set => Set(ref _sideMessage, value);
    }

    public bool IsLoadingSides
    {
        get => _isLoadingSides;
        private set => Set(ref _isLoadingSides, value);
    }

    public string LeftTitle => _comparedLeft is null ? "왼쪽 DB" : $"왼쪽 DB: {Path.GetFileName(_comparedLeft)}";
    public string RightTitle => _comparedRight is null ? "오른쪽 DB" : $"오른쪽 DB: {Path.GetFileName(_comparedRight)}";

    public bool HasDifferences => _sides is { DifferenceRows.Count: > 0 };

    public string DifferenceText
    {
        get
        {
            if (_sides is null) return "";
            if (_sides.DifferenceRows.Count == 0) return $"차이 없음 (전체 {_sides.TotalLines:N0}행)";
            var position = _currentDifference < 0 ? "-" : (_currentDifference + 1).ToString("N0");
            return $"차이 {position} / {_sides.DifferenceRows.Count:N0}   (전체 {_sides.TotalLines:N0}행: 왼쪽에만 {_sides.LeftOnlyCount:N0}, 오른쪽에만 {_sides.RightOnlyCount:N0}, 값 다름 {_sides.ChangedCount:N0})";
        }
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    private void Reset()
    {
        Tables.Clear();
        Selected = null;
        _comparedLeft = _comparedRight = null;
        Raise(nameof(LeftTitle));
        Raise(nameof(RightTitle));
        Summary = "";
    }

    private void Compare()
    {
        var (left, right) = (_leftPath!, _rightPath!);
        _ = RunAsync(async () =>
        {
            Reset();

            // Tables with no key at all: ask the user once, then remember the answer in the config file.
            var (leftRaw, rightRaw) = await Task.Run(() => (MdbReader.ReadSchema(left), MdbReader.ReadSchema(right)));
            KeyConfigurator.PromptForMissing(_config,
                KeyResolver.Resolve(leftRaw, _config.Config), KeyResolver.Resolve(rightRaw, _config.Config));

            var config = _config.Config;
            var (rows, ls, rs, problems) = await Task.Run(() => CompareFiles(left, right, config));
            (_leftSchema, _rightSchema) = (ls, rs);
            (_comparedLeft, _comparedRight) = (left, right);
            Raise(nameof(LeftTitle));
            Raise(nameof(RightTitle));
            foreach (var row in rows) Tables.Add(row);

            Summary = Describe(rows, problems);
            Selected = rows.FirstOrDefault(r => r.IsDifferent) ?? rows.FirstOrDefault(r => r.Note.Length == 0);
        });
    }

    private static string Describe(IReadOnlyList<CompareRow> rows, IReadOnlyList<string> problems)
    {
        var common = rows.Where(r => r.Match == TableMatchKind.Both).ToList();
        var different = common.Count(r => r.IsDifferent);
        var summary = $"공통 테이블 {common.Count}개 중 " + (different == 0
            ? "차이가 있는 테이블이 없습니다."
            : $"{different}개에 차이가 있습니다 (왼쪽에만 {rows.Sum(r => r.LeftOnly):N0}, 오른쪽에만 {rows.Sum(r => r.RightOnly):N0}, 값 다름 {rows.Sum(r => r.Changed):N0}).");

        var leftOnly = rows.Count(r => r.Match == TableMatchKind.LeftOnly);
        var rightOnly = rows.Count(r => r.Match == TableMatchKind.RightOnly);
        if (leftOnly + rightOnly > 0) summary += $"\n한쪽에만 있는 테이블: 왼쪽에만 {leftOnly}개, 오른쪽에만 {rightOnly}개 (목록의 비고 참고)";

        var notCompared = rows.Count(r => r.Match == TableMatchKind.Both && r.Note.Length > 0);
        if (notCompared > 0) summary += $"\n⚠ 공통 테이블 중 {notCompared}개는 비교하지 못했습니다 (목록의 비고 참고)";
        if (problems.Count > 0) summary += "\n⚠ " + string.Join("\n⚠ ", problems);
        return summary;
    }

    // Runs on a worker thread. Databases with different table sets are fine as long as they share at least one table;
    // a common table that cannot be compared (different structure, no key, duplicate keys) is only marked, not fatal.
    private static (List<CompareRow>, DatabaseSchema, DatabaseSchema, IReadOnlyList<string>) CompareFiles(string left, string right, AppConfig config)
    {
        var resolvedLeft = KeyResolver.Resolve(MdbReader.ReadSchema(left), config);
        var resolvedRight = KeyResolver.Resolve(MdbReader.ReadSchema(right), config);
        var matches = TableMatcher.Match(resolvedLeft.Schema, resolvedRight.Schema);

        if (!matches.Any(m => m.IsCommon))
            throw new InvalidOperationException(
                "두 DB에 이름이 같은 테이블이 하나도 없어 비교할 수 없습니다.\n" +
                $"왼쪽: {NamesPreview(resolvedLeft.Schema)}\n오른쪽: {NamesPreview(resolvedRight.Schema)}");

        var rows = new List<CompareRow>();
        foreach (var match in matches)
        {
            if (match.Kind == TableMatchKind.LeftOnly)
            {
                rows.Add(NotComparable(match, "", "왼쪽 DB에만 있는 테이블"));
                continue;
            }
            if (match.Kind == TableMatchKind.RightOnly)
            {
                rows.Add(NotComparable(match, "", "오른쪽 DB에만 있는 테이블"));
                continue;
            }

            var table = match.Left!;
            if (resolvedLeft.Sources[table.Name] == KeySource.Ignored)
            {
                rows.Add(NotComparable(match, "", "설정에서 제외됨"));
                continue;
            }
            if (match.SchemaDifferences.Count > 0)
            {
                var first = match.SchemaDifferences[0];
                var more = match.SchemaDifferences.Count > 1 ? $" 외 {match.SchemaDifferences.Count - 1}건" : "";
                rows.Add(NotComparable(match, "", $"두 DB의 구조가 달라 비교 불가: {first}{more}"));
                continue;
            }
            if (table.PrimaryKey.Count == 0)
            {
                rows.Add(NotComparable(match, "", "키가 없어 비교 불가"));
                continue;
            }

            var key = $"{string.Join(", ", table.PrimaryKey)} ({(config.Tables.ContainsKey(table.Name) ? "설정" : "mdb 기본 키")})";
            try
            {
                var diff = TableComparer.Compare(MdbReader.ReadTable(left, table), MdbReader.ReadTable(right, match.Right!), "왼쪽 mdb", "오른쪽 mdb");
                rows.Add(new CompareRow(table.Name,
                    diff.Rows.Count(r => r.Kind == RowDiffKind.Removed),
                    diff.Rows.Count(r => r.Kind == RowDiffKind.Added),
                    diff.Rows.Count(r => r.Kind == RowDiffKind.Changed),
                    diff.UnchangedCount, key, "", TableMatchKind.Both));
            }
            catch (DuplicateKeyException)
            {
                rows.Add(new CompareRow(table.Name, 0, 0, 0, 0, key, "키가 중복되어 비교 불가 (설정 탭에서 키 확인)", TableMatchKind.Both));
            }
        }

        return (rows, resolvedLeft.Schema, resolvedRight.Schema, resolvedLeft.Problems.Concat(resolvedRight.Problems).Distinct().ToList());
    }

    private static CompareRow NotComparable(TableMatch match, string key, string note) =>
        new(match.Name, 0, 0, 0, 0, key, note, match.Kind);

    private static string NamesPreview(DatabaseSchema schema)
    {
        const int Max = 8;
        var names = schema.Tables.Select(t => t.Name).ToList();
        return names.Count == 0 ? "(테이블 없음)" : string.Join(", ", names.Take(Max)) + (names.Count > Max ? $" 외 {names.Count - Max}개" : "");
    }

    // ---- side-by-side view ----

    private async Task LoadSidesAsync()
    {
        var version = ++_sidesVersion;
        var row = _selected;
        Sides = null;
        _aligned = null;
        SideMessage = "";
        if (row is null) { IsLoadingSides = false; return; }

        if (row.Note.Length > 0)
        {
            SideMessage = $"{row.Table}: {row.Note}";
            IsLoadingSides = false;
            return;
        }

        IsLoadingSides = true;
        try
        {
            var (leftPath, rightPath) = (_comparedLeft!, _comparedRight!);
            var (lt, rt) = (_leftSchema!.Find(row.Table)!, _rightSchema!.Find(row.Table)!);
            var onlyDifferences = _onlyDifferentRows;

            var (aligned, sides) = await Task.Run(() =>
            {
                var rows = TableAligner.Align(MdbReader.ReadTable(leftPath, lt), MdbReader.ReadTable(rightPath, rt), "왼쪽 mdb", "오른쪽 mdb");
                return (rows, SideBySide.Build(rows, lt, onlyDifferences));
            });
            if (version != _sidesVersion) return;   // another table was picked while this one loaded

            (_aligned, _alignedSchema) = (aligned, lt);
            Sides = sides;
        }
        catch (Exception ex)
        {
            if (version == _sidesVersion) SideMessage = $"{row.Table}: 불러오지 못했습니다.\n{ex.Message}";
        }
        finally
        {
            if (version == _sidesVersion) IsLoadingSides = false;
        }
    }

    // Toggling "only differences" re-slices the rows already in memory instead of reading the files again.
    private async Task RebuildSidesAsync()
    {
        if (_aligned is null || _alignedSchema is null) return;
        var version = ++_sidesVersion;
        var (aligned, schema, onlyDifferences) = (_aligned, _alignedSchema, _onlyDifferentRows);

        IsLoadingSides = true;
        try
        {
            var sides = await Task.Run(() => SideBySide.Build(aligned, schema, onlyDifferences));
            if (version == _sidesVersion) Sides = sides;
        }
        finally
        {
            if (version == _sidesVersion) IsLoadingSides = false;
        }
    }

    private void MoveToDifference(int step)
    {
        if (_sides is null || _sides.DifferenceRows.Count == 0) return;

        var count = _sides.DifferenceRows.Count;
        _currentDifference = _currentDifference < 0
            ? (step > 0 ? 0 : count - 1)
            : (_currentDifference + step + count) % count;

        Raise(nameof(DifferenceText));
        ScrollToRowRequested?.Invoke(_sides.DifferenceRows[_currentDifference]);
    }
}

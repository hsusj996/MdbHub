using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using MdbHub.App.Infrastructure;
using MdbHub.Core;
using MdbHub.Data;

namespace MdbHub.App.ViewModels;

/// <summary>One table in the comparison list. The counts change while the user merges.</summary>
public sealed class CompareRow : ObservableObject
{
    private int _leftOnly;
    private int _rightOnly;
    private int _changed;
    private int _pending;

    public CompareRow(string table, int leftOnly, int rightOnly, int changed, int unchanged, string key, string note, TableMatchKind match)
    {
        (Table, Unchanged, Key, Note, Match) = (table, unchanged, key, note, match);
        (_leftOnly, _rightOnly, _changed) = (leftOnly, rightOnly, changed);
    }

    public string Table { get; }
    public int Unchanged { get; }

    /// <summary>The key columns used (and where they come from).</summary>
    public string Key { get; }

    /// <summary>Why the table cannot be viewed side by side (empty when it can).</summary>
    public string Note { get; }

    /// <summary>Which side(s) have the table.</summary>
    public TableMatchKind Match { get; }

    public int LeftOnly { get => _leftOnly; private set => Set(ref _leftOnly, value); }
    public int RightOnly { get => _rightOnly; private set => Set(ref _rightOnly, value); }
    public int Changed { get => _changed; private set => Set(ref _changed, value); }

    /// <summary>Rows of this table (both files) that will be written when the merge is saved.</summary>
    public int Pending { get => _pending; private set => Set(ref _pending, value); }

    public bool IsDifferent => LeftOnly + RightOnly + Changed > 0;

    public void Update(int leftOnly, int rightOnly, int changed, int pending)
    {
        LeftOnly = leftOnly;
        RightOnly = rightOnly;
        Changed = changed;
        Pending = pending;
    }
}

public sealed class CompareViewModel : ViewModelBase
{
    private readonly ConfigService _config;
    private readonly ICollectionView _tablesView;
    private readonly Dictionary<string, MergeSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private DatabaseSchema? _leftSchema;
    private DatabaseSchema? _rightSchema;
    private string? _leftPath;
    private string? _rightPath;
    private string? _comparedLeft;
    private string? _comparedRight;
    private bool _onlyDifferent;
    private bool _onlyDifferentRows;
    private CompareRow? _selected;
    private MergeSession? _current;
    private SideBySide? _sides;
    private SideRow? _selectedLine;
    private string _sideMessage = "";
    private bool _isLoadingSides;
    private int _currentHunk = -1;
    private int _sidesVersion;
    private string _summary = "";

    public CompareViewModel(ConfigService config)
    {
        _config = config;
        config.Changed += OnConfigChanged;
        _tablesView = CollectionViewSource.GetDefaultView(Tables);
        _tablesView.Filter = o => !_onlyDifferent || ((CompareRow)o).IsDifferent || ((CompareRow)o).Pending > 0;
        BrowseLeftCommand = new RelayCommand(_ => LeftPath = Dialogs.OpenMdb("왼쪽 mdb 선택") ?? LeftPath, _ => NotBusy);
        BrowseRightCommand = new RelayCommand(_ => RightPath = Dialogs.OpenMdb("오른쪽 mdb 선택") ?? RightPath, _ => NotBusy);
        CompareCommand = new RelayCommand(_ => Compare(), _ => NotBusy && !string.IsNullOrWhiteSpace(_leftPath) && !string.IsNullOrWhiteSpace(_rightPath));
        NextDifferenceCommand = new RelayCommand(_ => MoveToHunk(+1), _ => HasDifferences);
        PreviousDifferenceCommand = new RelayCommand(_ => MoveToHunk(-1), _ => HasDifferences);

        CopyToRightCommand = new RelayCommand(p => CopyLine((SideRow)p!, toRight: true), p => _current is not null && p is SideRow { CanCopyToRight: true });
        CopyToLeftCommand = new RelayCommand(p => CopyLine((SideRow)p!, toRight: false), p => _current is not null && p is SideRow { CanCopyToLeft: true });
        CopyHunkToRightCommand = new RelayCommand(_ => CopyLines(HunkLinesOfSelection(), toRight: true), _ => HunkLinesOfSelection().Count > 0);
        CopyHunkToLeftCommand = new RelayCommand(_ => CopyLines(HunkLinesOfSelection(), toRight: false), _ => HunkLinesOfSelection().Count > 0);
        CopyTableToRightCommand = new RelayCommand(_ => CopyLines(AllLines(), toRight: true), _ => _current is not null);
        CopyTableToLeftCommand = new RelayCommand(_ => CopyLines(AllLines(), toRight: false), _ => _current is not null);
        RevertLineCommand = new RelayCommand(_ => { _current!.Revert(_selectedLine!.LineIndex); AfterMerge(); }, _ => _current is not null && _selectedLine is { IsPending: true });
        RevertTableCommand = new RelayCommand(_ => { _current!.RevertAll(); AfterMerge(); }, _ => _current is not null && PendingRows(_current) > 0);
        SaveCommand = new RelayCommand(_ => Save(), _ => NotBusy && PendingTotal > 0);
    }

    public ObservableCollection<CompareRow> Tables { get; } = [];
    public ICommand BrowseLeftCommand { get; }
    public ICommand BrowseRightCommand { get; }
    public ICommand CompareCommand { get; }
    public ICommand NextDifferenceCommand { get; }
    public ICommand PreviousDifferenceCommand { get; }
    public ICommand CopyToRightCommand { get; }
    public ICommand CopyToLeftCommand { get; }
    public ICommand CopyHunkToRightCommand { get; }
    public ICommand CopyHunkToLeftCommand { get; }
    public ICommand CopyTableToRightCommand { get; }
    public ICommand CopyTableToLeftCommand { get; }
    public ICommand RevertLineCommand { get; }
    public ICommand RevertTableCommand { get; }
    public ICommand SaveCommand { get; }
    public ICollectionView TablesView => _tablesView;

    /// <summary>Asks the view to scroll both grids so that this list index is visible (and selected).</summary>
    public event Action<int>? ScrollToRowRequested;

    /// <summary>Lines were changed in place (copy, edit, undo); the view must re-color what it shows.</summary>
    public event Action? MergeChanged;

    public string? LeftPath
    {
        get => _leftPath;
        set => Set(ref _leftPath, value);
    }

    public string? RightPath
    {
        get => _rightPath;
        set => Set(ref _rightPath, value);
    }

    /// <summary>Hides identical tables from the table list.</summary>
    public bool OnlyDifferent
    {
        get => _onlyDifferent;
        set { if (Set(ref _onlyDifferent, value)) _tablesView.Refresh(); }
    }

    /// <summary>Hides lines that were identical from the start inside the side-by-side view.</summary>
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

    /// <summary>The line selected in the grids (the hunk and single-line commands act on it).</summary>
    public SideRow? SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (!Set(ref _selectedLine, value)) return;
            Raise(nameof(SelectedHunkText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public SideBySide? Sides
    {
        get => _sides;
        private set
        {
            if (!Set(ref _sides, value)) return;
            _currentHunk = -1;
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

    public bool HasDifferences => _sides is { HunkStartRows.Count: > 0 };

    public string DifferenceText
    {
        get
        {
            if (_sides is null || _current is null) return "";
            var (leftOnly, rightOnly, changed) = _current.CurrentCounts();
            var remaining = leftOnly + rightOnly + changed;
            var hunks = _sides.HunkStartRows.Count;
            if (hunks == 0) return $"차이 없음 (전체 {_sides.TotalLines:N0}행)";

            var position = _currentHunk < 0 ? "-" : (_currentHunk + 1).ToString("N0");
            return $"구역 {position} / {hunks:N0}   (남은 차이 {remaining:N0}줄: 왼쪽에만 {leftOnly:N0}, 오른쪽에만 {rightOnly:N0}, 값 다름 {changed:N0} / 전체 {_sides.TotalLines:N0}행)";
        }
    }

    public string SelectedHunkText => _selectedLine is { Hunk: > 0 } line ? $"선택한 구역: {line.Hunk}번" : "";

    /// <summary>What will be written when saving: rows per file and the number of tables involved.</summary>
    public string PendingText
    {
        get
        {
            var (left, right) = (_sessions.Values.Sum(s => s.PendingRows(MergeSide.Left)), _sessions.Values.Sum(s => s.PendingRows(MergeSide.Right)));
            if (left + right == 0) return "";
            var tables = _sessions.Values.Count(s => PendingRows(s) > 0);
            return $"저장 대기: 왼쪽 {left:N0}행, 오른쪽 {right:N0}행 (테이블 {tables}개)";
        }
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    private int PendingTotal => _sessions.Values.Sum(PendingRows);

    private static int PendingRows(MergeSession s) => s.PendingRows(MergeSide.Left) + s.PendingRows(MergeSide.Right);

    private void OnConfigChanged()
    {
        // Changing the key settings invalidates old results, but never silently throw away merge work that is not saved yet.
        if (PendingTotal > 0)
        {
            Summary += "\n⚠ 설정이 바뀌었습니다. 저장하지 않은 병합은 이전 키 설정 기준입니다. 저장한 뒤 다시 비교하세요.";
            return;
        }
        Reset();
    }

    private void Reset()
    {
        _sessions.Clear();
        _current = null;
        Tables.Clear();
        Selected = null;
        _comparedLeft = _comparedRight = null;
        Raise(nameof(LeftTitle));
        Raise(nameof(RightTitle));
        Raise(nameof(PendingText));
        Summary = "";
    }

    // ---- comparing ----

    private void Compare()
    {
        if (PendingTotal > 0 && !Dialogs.Confirm($"저장하지 않은 병합 내용이 있습니다 ({PendingText.Replace("저장 대기: ", "")}).\n다시 비교하면 사라집니다. 계속할까요?")) return;

        var (left, right) = (_leftPath!, _rightPath!);
        _ = RunAsync(() => CompareAsync(left, right, reselect: null));
    }

    private async Task CompareAsync(string left, string right, string? reselect)
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
        Selected = rows.FirstOrDefault(r => reselect is not null && string.Equals(r.Table, reselect, StringComparison.OrdinalIgnoreCase) && r.Note.Length == 0)
                   ?? rows.FirstOrDefault(r => r.IsDifferent)
                   ?? rows.FirstOrDefault(r => r.Note.Length == 0);
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

    // ---- side-by-side view of the selected table ----

    private async Task LoadSidesAsync()
    {
        var version = ++_sidesVersion;
        var row = _selected;
        Sides = null;
        _current = null;
        SelectedLine = null;
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
            _sessions.TryGetValue(row.Table, out var existing);   // earlier merging of this table is kept while switching tables

            var (session, sides) = await Task.Run(() =>
            {
                var s = existing ?? MergeSession.Create(
                    TableAligner.Align(MdbReader.ReadTable(leftPath, lt), MdbReader.ReadTable(rightPath, rt), "왼쪽 mdb", "오른쪽 mdb"), lt);
                return (s, s.CreateView(onlyDifferences));
            });
            if (version != _sidesVersion) return;   // another table was picked while this one loaded

            _sessions[row.Table] = session;
            _current = session;
            Sides = sides;
            CommandManager.InvalidateRequerySuggested();
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

    // Toggling "only differences" re-slices the lines already in memory instead of reading the files again.
    private async Task RebuildSidesAsync()
    {
        if (_current is not { } session) return;
        var version = ++_sidesVersion;
        var onlyDifferences = _onlyDifferentRows;

        IsLoadingSides = true;
        try
        {
            var sides = await Task.Run(() => session.CreateView(onlyDifferences));
            if (version == _sidesVersion) Sides = sides;
        }
        finally
        {
            if (version == _sidesVersion) IsLoadingSides = false;
        }
    }

    private void MoveToHunk(int step)
    {
        if (_sides is null || _sides.HunkStartRows.Count == 0) return;

        var count = _sides.HunkStartRows.Count;
        _currentHunk = _currentHunk < 0
            ? (step > 0 ? 0 : count - 1)
            : (_currentHunk + step + count) % count;

        Raise(nameof(DifferenceText));
        ScrollToRowRequested?.Invoke(_sides.HunkStartRows[_currentHunk]);
    }

    // ---- merging ----

    private IReadOnlyList<int> AllLines() => _current is null ? [] : Enumerable.Range(0, _current.Lines.Count).ToList();

    private IReadOnlyList<int> HunkLinesOfSelection() =>
        _current is not null && _selectedLine is { Hunk: > 0 } line ? _current.HunkLines(line.Hunk) : [];

    private void CopyLine(SideRow row, bool toRight)
    {
        if (_current is null) return;
        if (toRight) _current.CopyToRight(row.LineIndex); else _current.CopyToLeft(row.LineIndex);
        AfterMerge();
    }

    private void CopyLines(IReadOnlyList<int> lines, bool toRight)
    {
        if (_current is null) return;
        if (toRight) _current.CopyToRight(lines); else _current.CopyToLeft(lines);
        AfterMerge();
    }

    /// <summary>Called before a typed value is accepted: returns why it cannot be used, or null.</summary>
    public string? ValidateEdit(MergeSide side, SideRow row, int column, string text)
    {
        if (_current is null) return "수정할 테이블이 선택되지 않았습니다.";
        if (_current.KeyColumns.Contains(column)) return "키 컬럼은 수정할 수 없습니다.";

        var line = _current.Lines[row.LineIndex];
        var sample = _current.OriginalValue(side, row.LineIndex, column) ?? (side == MergeSide.Left ? line.Right : line.Left)?[column];
        return CellValueParser.TryParse(_current.Schema.Columns[column], sample, text, out _, out var error) ? null : error;
    }

    /// <summary>Records an accepted hand edit on one side.</summary>
    public void OnCellEdited(MergeSide side, SideRow row, int column, string text)
    {
        if (_current is null) return;

        var line = _current.Lines[row.LineIndex];
        var sample = _current.OriginalValue(side, row.LineIndex, column) ?? (side == MergeSide.Left ? line.Right : line.Left)?[column];
        if (!CellValueParser.TryParse(_current.Schema.Columns[column], sample, text, out var value, out _)) return;

        _current.SetValue(side, row.LineIndex, column, value);
        AfterMerge();
    }

    // Lines were changed in place: refresh everything that summarizes them.
    private void AfterMerge()
    {
        if (_current is not null && _selected is not null)
        {
            var (leftOnly, rightOnly, changed) = _current.CurrentCounts();
            _selected.Update(leftOnly, rightOnly, changed, PendingRows(_current));
            _tablesView.Refresh();
        }
        Raise(nameof(DifferenceText));
        Raise(nameof(PendingText));
        Raise(nameof(SelectedHunkText));
        MergeChanged?.Invoke();
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- saving ----

    private void Save()
    {
        var (leftPath, rightPath) = (_comparedLeft!, _comparedRight!);
        var files = new List<MergeFileChanges>
        {
            new(leftPath, TablesWithChanges(MergeSide.Left)),
            new(rightPath, TablesWithChanges(MergeSide.Right)),
        };

        var message = "비교한 두 파일에 병합 내용을 저장합니다.\n\n" +
                      string.Join("\n", files.Where(f => f.ChangeCount > 0).Select(f =>
                          $"{f.Path}\n   삽입 {f.Tables.Sum(t => t.Changes.Count(c => c.Kind == RowChangeKind.Insert)):N0}행, " +
                          $"수정 {f.Tables.Sum(t => t.Changes.Count(c => c.Kind == RowChangeKind.Update)):N0}행 (테이블 {f.Tables.Count}개)")) +
                      "\n\n저장하기 전에 각 파일을 백업하고, 변경 내역은 .merge.csv로 남깁니다.\n" +
                      "저장 도중 오류가 나면 이미 저장된 파일은 백업으로 되돌립니다. 저장한 뒤 다시 비교합니다.\n\n계속할까요?";
        if (!Dialogs.Confirm(message)) return;

        var reselect = _selected?.Table;
        _ = RunAsync(async () =>
        {
            var results = await Task.Run(() => MdbMerger.Save(files, DateTime.Now));
            var done = string.Join("\n\n", results.Select(r =>
                $"{r.Path}\n   삽입 {r.Inserts:N0}행, 수정 {r.Updates:N0}행\n   백업: {r.BackupPath}\n   변경 내역: {r.LogPath}"));

            await CompareAsync(leftPath, rightPath, reselect);   // show the saved state
            Summary = "병합 저장 완료.\n" + Summary;
            Dialogs.Info("저장했습니다.\n\n" + done);
        });
    }

    private IReadOnlyList<MergeTableChanges> TablesWithChanges(MergeSide side) =>
        _sessions.Values
            .Select(s => new MergeTableChanges(s.Schema, s.PendingChanges(side)))
            .Where(t => t.Changes.Count > 0)
            .OrderBy(t => t.Schema.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Windows.Data;
using System.Windows.Input;
using MdbHub.App.Infrastructure;
using MdbHub.Core;
using MdbHub.Data;

namespace MdbHub.App.ViewModels;

public sealed record CompareRow(string Table, int LeftOnly, int RightOnly, int Changed, int Unchanged, string Key, string Note)
{
    public bool IsDifferent => LeftOnly + RightOnly + Changed > 0;
}

public sealed class CompareViewModel : ViewModelBase
{
    public const string StatusColumn = "상태";
    public const string LeftOnlyLabel = "왼쪽에만";
    public const string RightOnlyLabel = "오른쪽에만";
    public const string ChangedLabel = "값 다름";

    private readonly ConfigService _config;
    private readonly Dictionary<string, TableDiff> _diffs = new(StringComparer.OrdinalIgnoreCase);
    private DatabaseSchema? _leftSchema;
    private DatabaseSchema? _rightSchema;
    private string? _leftPath;
    private string? _rightPath;
    private bool _onlyDifferent;
    private CompareRow? _selected;
    private DataView? _diffView;
    private string _summary = "";
    private readonly ICollectionView _tablesView;

    public CompareViewModel(ConfigService config)
    {
        _config = config;
        config.Changed += Reset;   // the keys may have changed, so earlier results no longer hold
        _tablesView = CollectionViewSource.GetDefaultView(Tables);
        _tablesView.Filter = o => !_onlyDifferent || ((CompareRow)o).IsDifferent;
        BrowseLeftCommand = new RelayCommand(_ => LeftPath = Dialogs.OpenMdb("왼쪽 mdb 선택") ?? LeftPath, _ => NotBusy);
        BrowseRightCommand = new RelayCommand(_ => RightPath = Dialogs.OpenMdb("오른쪽 mdb 선택") ?? RightPath, _ => NotBusy);
        CompareCommand = new RelayCommand(_ => Compare(), _ => NotBusy && !string.IsNullOrWhiteSpace(_leftPath) && !string.IsNullOrWhiteSpace(_rightPath));
    }

    public ObservableCollection<CompareRow> Tables { get; } = [];
    public ICommand BrowseLeftCommand { get; }
    public ICommand BrowseRightCommand { get; }
    public ICommand CompareCommand { get; }
    public ICollectionView TablesView => _tablesView;

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

    public bool OnlyDifferent
    {
        get => _onlyDifferent;
        set { if (Set(ref _onlyDifferent, value)) _tablesView.Refresh(); }
    }

    public CompareRow? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) ShowDiff(); }
    }

    public DataView? DiffView
    {
        get => _diffView;
        private set => Set(ref _diffView, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    private void Reset()
    {
        _diffs.Clear();
        Tables.Clear();
        Selected = null;
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
            var (rows, diffs, ls, rs, problems) = await Task.Run(() => CompareFiles(left, right, config));
            (_leftSchema, _rightSchema) = (ls, rs);
            foreach (var (table, diff) in diffs) _diffs[table] = diff;
            foreach (var row in rows) Tables.Add(row);

            var different = rows.Count(r => r.IsDifferent);
            var summary = different == 0
                ? "두 파일의 비교 가능한 테이블이 모두 동일합니다."
                : $"차이가 있는 테이블 {different}개 / 전체 {rows.Count}개 (왼쪽에만 {rows.Sum(r => r.LeftOnly):N0}, 오른쪽에만 {rows.Sum(r => r.RightOnly):N0}, 값 다름 {rows.Sum(r => r.Changed):N0})";
            var notCompared = rows.Where(r => r.Note.Length > 0).ToList();
            if (notCompared.Count > 0) summary += $"\n⚠ 비교하지 못한 테이블 {notCompared.Count}개 (표의 비고 참고)";
            if (problems.Count > 0) summary += "\n⚠ " + string.Join("\n⚠ ", problems);
            Summary = summary;
            Selected = rows.FirstOrDefault(r => r.IsDifferent);
        });
    }

    // Runs on a worker thread. Schema differences abort the whole comparison; a table that cannot be compared is only marked.
    private static (List<CompareRow>, List<(string, TableDiff)>, DatabaseSchema, DatabaseSchema, IReadOnlyList<string>) CompareFiles(string left, string right, AppConfig config)
    {
        var resolvedLeft = KeyResolver.Resolve(MdbReader.ReadSchema(left), config);
        var resolvedRight = KeyResolver.Resolve(MdbReader.ReadSchema(right), config);
        var (ls, rs) = (resolvedLeft.Schema, resolvedRight.Schema);

        var differences = SchemaValidator.Compare(ls, rs);
        if (differences.Count > 0) throw new SchemaMismatchException(differences);

        var rows = new List<CompareRow>();
        var diffs = new List<(string, TableDiff)>();
        foreach (var table in ls.Tables)
        {
            if (resolvedLeft.Sources[table.Name] == KeySource.Ignored)
            {
                rows.Add(new CompareRow(table.Name, 0, 0, 0, 0, "", "설정에서 제외됨"));
                continue;
            }
            if (table.PrimaryKey.Count == 0)
            {
                rows.Add(new CompareRow(table.Name, 0, 0, 0, 0, "", "키가 없어 비교 불가"));
                continue;
            }

            var key = $"{string.Join(", ", table.PrimaryKey)} ({(config.Tables.ContainsKey(table.Name) ? "설정" : "mdb 기본 키")})";
            try
            {
                var diff = TableComparer.Compare(MdbReader.ReadTable(left, table), MdbReader.ReadTable(right, rs.Find(table.Name)!), "왼쪽 mdb", "오른쪽 mdb");
                diffs.Add((table.Name, diff));
                rows.Add(new CompareRow(table.Name,
                    diff.Rows.Count(r => r.Kind == RowDiffKind.Removed),
                    diff.Rows.Count(r => r.Kind == RowDiffKind.Added),
                    diff.Rows.Count(r => r.Kind == RowDiffKind.Changed),
                    diff.UnchangedCount, key, ""));
            }
            catch (DuplicateKeyException)
            {
                rows.Add(new CompareRow(table.Name, 0, 0, 0, 0, key, "키가 중복되어 비교 불가 (설정 탭에서 키 확인)"));
            }
        }
        return (rows, diffs, ls, rs, resolvedLeft.Problems.Concat(resolvedRight.Problems).Distinct().ToList());
    }

    private void ShowDiff()
    {
        if (_selected is null || !_diffs.TryGetValue(_selected.Table, out var diff))
        {
            DiffView = null;
            return;
        }
        DiffView = BuildTable(_leftSchema!.Find(_selected.Table)!, _rightSchema!.Find(_selected.Table)!, diff).DefaultView;
    }

    /// <summary>One grid row per differing PK. Changed cells read "left → right"; the first column labels the kind.</summary>
    internal static DataTable BuildTable(TableSchema left, TableSchema right, TableDiff diff)
    {
        var table = new DataTable();
        table.Columns.Add(StatusColumn, typeof(string));
        foreach (var c in left.Columns) table.Columns.Add(c.Name, typeof(string));

        foreach (var row in diff.Rows)
        {
            var cells = new object[left.Columns.Count + 1];
            switch (row.Kind)
            {
                case RowDiffKind.Removed:
                    cells[0] = LeftOnlyLabel;
                    for (var i = 0; i < left.Columns.Count; i++) cells[i + 1] = Format(row.Left![i]);
                    break;
                case RowDiffKind.Added:
                    cells[0] = RightOnlyLabel;
                    for (var i = 0; i < left.Columns.Count; i++) cells[i + 1] = Format(row.Right![right.IndexOf(left.Columns[i].Name)]);
                    break;
                default:
                    cells[0] = ChangedLabel;
                    for (var i = 0; i < left.Columns.Count; i++)
                    {
                        var name = left.Columns[i].Name;
                        cells[i + 1] = row.ChangedColumns.Contains(name, StringComparer.OrdinalIgnoreCase)
                            ? $"{Format(row.Left![i], true)} → {Format(row.Right![right.IndexOf(name)], true)}"
                            : Format(row.Left![i]);
                    }
                    break;
            }
            table.Rows.Add(cells);
        }
        return table;
    }

    private static string Format(object? value, bool markNull = false) => value switch
    {
        null => markNull ? "(null)" : "",
        byte[] bytes => $"({bytes.Length} bytes)",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.CurrentCulture),
        _ => value.ToString() ?? "",
    };
}

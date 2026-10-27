using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using MdbHub.App.Infrastructure;
using MdbHub.Core;
using MdbHub.Data;

namespace MdbHub.App.ViewModels;

public sealed record ExtractRow(string Table, string Result);

public sealed class ExtractViewModel : ViewModelBase
{
    private readonly ConfigService _config;
    private DatabaseSchema? _schema;
    private string? _sourcePath;
    private string? _outputPath;
    private string _summary = "";

    public ExtractViewModel(ConfigService config)
    {
        _config = config;
        BrowseSourceCommand = new RelayCommand(_ => BrowseSource(), _ => NotBusy);
        BrowseOutputCommand = new RelayCommand(_ => OutputPath = Dialogs.SaveMdb("추출 결과를 저장할 mdb") ?? OutputPath, _ => NotBusy);
        AddFilterCommand = new RelayCommand(_ => Filters.Add(NewFilter("", [])), _ => _schema is not null && NotBusy);
        AddCombinationFilterCommand = new RelayCommand(_ => Filters.Add(NewFilter("조합", [], combination: true)), _ => _schema is not null && NotBusy);
        RemoveFilterCommand = new RelayCommand(f => Filters.Remove((FilterViewModel)f!));
        ExtractCommand = new RelayCommand(_ => Extract(), _ => NotBusy);
    }

    public ObservableCollection<string> AvailableColumns { get; } = [];
    public ObservableCollection<FilterViewModel> Filters { get; } = [];
    public ObservableCollection<ExtractRow> Results { get; } = [];

    public ICommand BrowseSourceCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand AddFilterCommand { get; }
    public ICommand AddCombinationFilterCommand { get; }
    public ICommand RemoveFilterCommand { get; }
    public ICommand ExtractCommand { get; }

    public string? SourcePath
    {
        get => _sourcePath;
        private set => Set(ref _sourcePath, value);
    }

    public string? OutputPath
    {
        get => _outputPath;
        set => Set(ref _outputPath, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    private FilterViewModel NewFilter(string name, IEnumerable<string> columns, bool combination = false) =>
        new(name,
            AvailableColumns.ToList(),
            combination
                ? cols => Task.Run(() => (IReadOnlyList<object>)MdbReader.GetDistinctCombinations(_sourcePath!, _schema!, cols).Cast<object>().ToList())
                : cols => Task.Run(() => MdbReader.GetDistinctValues(_sourcePath!, _schema!, cols)),
            combination ? CombinationCoverage : Coverage,
            columns,
            combination);

    // Tables that have none of the filter's columns are skipped by an extract, so show them up front.
    private FilterCoverage Coverage(IReadOnlyList<string> columns)
    {
        var filter = new ColumnFilter(columns, []);
        var skipped = _schema!.Tables.Where(t => filter.ResolveIn(t) is null).Select(t => t.Name).ToList();
        return new FilterCoverage(_schema.Tables.Count - skipped.Count, _schema.Tables.Count, skipped);
    }

    // A combination needs every one of its columns: a table lacking any of them is skipped.
    private FilterCoverage CombinationCoverage(IReadOnlyList<string> columns)
    {
        var filter = new CombinationFilter(columns, null);
        var skipped = _schema!.Tables.Where(t => !filter.AppliesTo(t)).Select(t => t.Name).ToList();
        return new FilterCoverage(_schema.Tables.Count - skipped.Count, _schema.Tables.Count, skipped);
    }

    private void BrowseSource()
    {
        var path = Dialogs.OpenMdb("추출할 원본 mdb 선택");
        if (path is not null) _ = OpenSourceAsync(path);
    }

    /// <summary>Reads the mdb's tables and fills the filter cards from the config (what choosing the file in the dialog does).</summary>
    public Task OpenSourceAsync(string path) =>
        RunAsync(async () =>
        {
            var schema = await Task.Run(() => MdbReader.ReadSchema(path));
            _schema = schema;
            SourcePath = path;

            AvailableColumns.Clear();
            foreach (var c in schema.Tables.SelectMany(t => t.Columns.Select(c => c.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                AvailableColumns.Add(c);

            Filters.Clear();
            Results.Clear();
            Summary = $"테이블 {schema.Tables.Count}개를 읽었습니다. 필터에서 추출할 값을 체크하세요.";

            // Filter cards come from the config; only the columns this mdb actually has are used.
            var notInThisMdb = new List<string>();
            foreach (var definition in _config.Config.Filters)
            {
                var columns = definition.Columns
                    .Select(name => AvailableColumns.FirstOrDefault(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
                    .OfType<string>()
                    .ToList();
                if (columns.Count == 0) { notInThisMdb.Add(definition.Name); continue; }
                Filters.Add(NewFilter(definition.Name, columns));
            }
            if (Filters.Count == 0) Filters.Add(NewFilter("", []));
            if (notInThisMdb.Count > 0)
                Summary += $"  (설정의 필터 {string.Join(", ", notInThisMdb.Select(n => $"'{n}'"))}은(는) 이 mdb에 해당 컬럼이 없어 만들지 않았습니다.)";
        });

    // Checking every combination means "any value", but the columns are still required: tables without them are skipped.
    private static CombinationFilter ToCombinationFilter(FilterViewModel f) =>
        new(f.SelectedColumns.ToList(),
            f.SelectedCount == f.TotalCount ? null : f.SelectedValues.Cast<CombinationValue>().Select(c => c.Values).ToList());

    private void Extract()
    {
        if (_sourcePath is null) { Dialogs.Error("원본 mdb를 먼저 선택하세요."); return; }
        if (string.IsNullOrWhiteSpace(_outputPath)) { Dialogs.Error("저장할 mdb 경로를 지정하세요."); return; }
        if (File.Exists(_outputPath)) { Dialogs.Error("이미 존재하는 파일입니다. 다른 이름을 지정하세요."); return; }

        // A filter with nothing checked is ignored (like "select all"), so at least one filter must restrict something.
        var active = Filters.Where(f => f.SelectedColumns.Count > 0 && f.SelectedCount > 0).ToList();
        var filters = active.Where(f => !f.IsCombination).Select(f => new ColumnFilter(f.SelectedColumns.ToList(), f.SelectedValues)).ToList();
        var combinations = active.Where(f => f.IsCombination).Select(ToCombinationFilter).ToList();
        if (filters.Count + combinations.Count == 0) { Dialogs.Error("추출할 값을 하나 이상 체크하세요."); return; }

        var (source, output) = (_sourcePath, _outputPath);
        _ = RunAsync(async () =>
        {
            var result = await Task.Run(() => MdbExtractor.Extract(source, output, filters, combinations: combinations));

            Results.Clear();
            foreach (var (table, rows) in result.ExtractedRows)
            {
                var noKey = result.TablesWithoutKey.Contains(table, StringComparer.OrdinalIgnoreCase) ? "   ⚠ 키를 만들지 못해 키 없이 추출됨" : "";
                Results.Add(new ExtractRow(table, $"{rows:N0}행   (필터 컬럼: {string.Join(", ", result.FilterColumns[table])}){noKey}"));
            }
            foreach (var table in result.SkippedTables) Results.Add(new ExtractRow(table, "건너뜀 (필터 컬럼 없음)"));

            var total = result.ExtractedRows.Values.Sum();
            Summary = $"추출 완료: 테이블 {result.ExtractedRows.Count}개, 총 {total:N0}행 → {output}";
            if (result.Warnings.Count > 0)
                Summary += "\n⚠ " + string.Join("\n⚠ ", result.Warnings) + "\n   (데이터는 정상적으로 추출되었습니다. 이 메시지를 보고해 주시면 원인을 찾는 데 도움이 됩니다.)";
            if (total == 0) Dialogs.Info("조건에 맞는 행이 없어 빈 테이블만 만들어졌습니다.");
        });
    }
}

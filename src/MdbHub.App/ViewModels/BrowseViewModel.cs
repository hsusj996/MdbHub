using System.Collections.ObjectModel;
using System.Data;
using System.Windows.Input;
using MdbHub.App.Infrastructure;
using MdbHub.Core;
using MdbHub.Data;

namespace MdbHub.App.ViewModels;

public sealed record BrowseTable(string Name, int Rows, int Columns, string PrimaryKey);

public sealed class BrowseViewModel : ViewModelBase
{
    private string? _path;
    private BrowseTable? _selected;
    private DataView? _data;
    private string _summary = "";
    private DatabaseSchema? _schema;

    public BrowseViewModel()
    {
        BrowseCommand = new RelayCommand(_ => Browse(), _ => NotBusy);
    }

    public ObservableCollection<BrowseTable> Tables { get; } = [];
    public ICommand BrowseCommand { get; }

    public string? Path
    {
        get => _path;
        private set => Set(ref _path, value);
    }

    public BrowseTable? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value) && value is not null) Load(value); }
    }

    public DataView? Data
    {
        get => _data;
        private set => Set(ref _data, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    private void Browse()
    {
        var path = Dialogs.OpenMdb("조회할 mdb 선택");
        if (path is null) return;

        _ = RunAsync(async () =>
        {
            var (schema, counts) = await Task.Run(() =>
            {
                var s = MdbReader.ReadSchema(path);
                return (s, MdbReader.GetRowCounts(path, s));
            });

            _schema = schema;
            Path = path;
            Data = null;
            Tables.Clear();
            foreach (var t in schema.Tables)
                Tables.Add(new BrowseTable(t.Name, counts[t.Name], t.Columns.Count, string.Join(", ", t.PrimaryKey)));

            Summary = $"테이블 {schema.Tables.Count}개. 왼쪽에서 테이블을 선택하세요.";
            Selected = Tables.FirstOrDefault();
        });
    }

    private void Load(BrowseTable table)
    {
        var (path, schema) = (_path!, _schema!.Find(table.Name)!);
        _ = RunAsync(async () =>
        {
            var view = await Task.Run(() => ToDataTable(MdbReader.ReadTable(path, schema)).DefaultView);
            if (_selected != table) return;   // user picked another table meanwhile

            Data = view;
            Summary = $"{table.Name}: {table.Rows:N0}행, {table.Columns}열" +
                      (table.PrimaryKey.Length > 0 ? $"   PK: {table.PrimaryKey}" : "   (PK 없음)") +
                      "   - 컬럼 헤더의 필터 아이콘으로 값을 체크해 걸러 볼 수 있습니다.";
        });
    }

    // Column types come from the first non-null value so numbers and dates sort and filter as numbers and dates.
    internal static DataTable ToDataTable(TableData data)
    {
        var table = new DataTable(data.Schema.Name);
        for (var i = 0; i < data.Schema.Columns.Count; i++)
        {
            var sample = data.Rows.Select(r => r[i]).FirstOrDefault(v => v is not null);
            var type = sample is null or byte[] ? typeof(string) : sample.GetType();
            table.Columns.Add(data.Schema.Columns[i].Name, type);
        }

        table.BeginLoadData();
        foreach (var row in data.Rows)
            table.Rows.Add(row.Select(v => v switch { null => DBNull.Value, byte[] b => $"({b.Length} bytes)", _ => v }).ToArray());
        table.EndLoadData();
        return table;
    }
}

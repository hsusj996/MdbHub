using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MdbHub.App.ViewModels;
using MdbHub.Core;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.GridView;

namespace MdbHub.App.Views;

public partial class CompareView : UserControl
{
    private static readonly Brush ChangedRowBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xD0)));
    private static readonly Brush ChangedCellBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x66)));
    private static readonly Brush LeftOnlyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0xD6)));
    private static readonly Brush RightOnlyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD3, 0xF2, 0xD3)));
    private static readonly Brush FillerBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE9, 0xE9, 0xE9)));

    private CompareViewModel? _viewModel;
    private ScrollViewer? _leftScroll;
    private ScrollViewer? _rightScroll;
    private bool _syncing;

    public CompareView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => EnsureScrollSync();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ScrollToRowRequested -= ScrollToRow;
        }
        _viewModel = e.NewValue as CompareViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.ScrollToRowRequested += ScrollToRow;
        }
        RebuildColumns();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CompareViewModel.Sides)) return;
        RebuildColumns();
        // The grids may only get their scroll viewers once they have been shown with data.
        Dispatcher.BeginInvoke(EnsureScrollSync, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Both grids show the same columns with the same widths (so they can also scroll sideways together):
    // a line number, then one column per table column (read from SideRow.Cells[i]).
    private void RebuildColumns()
    {
        var sides = _viewModel?.Sides;
        foreach (var grid in new[] { LeftGrid, RightGrid })
        {
            grid.Columns.Clear();
            if (sides is null) continue;

            grid.Columns.Add(new GridViewDataColumn
            {
                Header = "#",
                DataMemberBinding = new Binding(nameof(SideRow.Number)),
                UniqueName = "number",
                Width = 52,
                TextAlignment = TextAlignment.Right,
                IsSortable = false,
                IsFilterable = false,
            });
            for (var i = 0; i < sides.Columns.Count; i++)
            {
                grid.Columns.Add(new GridViewDataColumn
                {
                    Header = sides.Columns[i],
                    DataMemberBinding = new Binding($"{nameof(SideRow.Cells)}[{i}]"),
                    DataType = typeof(string),
                    UniqueName = $"c{i}",
                    Width = ColumnWidth(sides, i),
                    IsSortable = false,
                    IsFilterable = false,
                });
            }
        }
    }

    // Fit the header and the first lines of both sides; roughly 8 px per character.
    private static double ColumnWidth(SideBySide sides, int column)
    {
        var longest = sides.Columns[column].Length;
        foreach (var list in new[] { sides.Left, sides.Right })
            foreach (var row in list.Take(300))
                longest = Math.Max(longest, row.Cells[column].Length);
        return Math.Clamp(longest * 8 + 24, 60, 240);
    }

    // ---- colors ----

    // Rows and cells are recycled while scrolling, so the color must be set (or cleared) every time.
    private void Grid_RowLoaded(object? sender, RowLoadedEventArgs e)
    {
        if (e.Row is not GridViewRow row) return;
        row.Background = (row.DataContext as SideRow)?.Kind switch
        {
            SideRowKind.Changed => ChangedRowBrush,
            SideRowKind.LeftOnly => LeftOnlyBrush,
            SideRowKind.RightOnly => RightOnlyBrush,
            SideRowKind.Filler => FillerBrush,
            _ => null,
        };
    }

    private void Grid_CellLoaded(object? sender, CellEventArgs e)
    {
        if (e.Cell is not GridViewCell cell) return;
        if (cell.DataContext is SideRow row && ColumnIndex(cell) is { } index && row.ChangedColumns.Contains(index))
            cell.Background = ChangedCellBrush;
        else
            cell.ClearValue(BackgroundProperty);
    }

    private static int? ColumnIndex(GridViewCell cell) =>
        cell.Column?.UniqueName is { Length: > 1 } name && name[0] == 'c' && int.TryParse(name.AsSpan(1), out var i) ? i : null;

    // ---- keeping the two grids together ----

    private void EnsureScrollSync()
    {
        _leftScroll = Attach(_leftScroll, FindScrollViewer(LeftGrid));
        _rightScroll = Attach(_rightScroll, FindScrollViewer(RightGrid));
    }

    private ScrollViewer? Attach(ScrollViewer? current, ScrollViewer? found)
    {
        if (ReferenceEquals(current, found)) return current;
        if (current is not null) current.ScrollChanged -= OnScrollChanged;
        if (found is not null) found.ScrollChanged += OnScrollChanged;
        return found;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncing || sender is not ScrollViewer source) return;

        var target = ReferenceEquals(source, _leftScroll) ? _rightScroll : _leftScroll;
        if (target is null) return;

        _syncing = true;
        try
        {
            // Compare the offsets themselves rather than trusting the reported change: setting an equal offset is a no-op,
            // so this cannot ping-pong between the two grids.
            if (target.VerticalOffset != source.VerticalOffset) target.ScrollToVerticalOffset(source.VerticalOffset);
            if (target.HorizontalOffset != source.HorizontalOffset) target.ScrollToHorizontalOffset(source.HorizontalOffset);
        }
        finally { _syncing = false; }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is GridViewScrollViewer scroll) return scroll;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private void LeftGrid_SelectionChanged(object? sender, SelectionChangeEventArgs e) => MirrorSelection(LeftGrid, RightGrid, sides => sides.Right);

    private void RightGrid_SelectionChanged(object? sender, SelectionChangeEventArgs e) => MirrorSelection(RightGrid, LeftGrid, sides => sides.Left);

    // Selecting a line on one side selects the same line on the other side.
    private void MirrorSelection(RadGridView from, RadGridView to, Func<SideBySide, IReadOnlyList<SideRow>> otherSide)
    {
        if (_syncing || from.SelectedItem is not SideRow row || _viewModel?.Sides is not { } sides) return;

        var list = otherSide(sides);
        if (row.ListIndex >= list.Count || ReferenceEquals(to.SelectedItem, list[row.ListIndex])) return;

        _syncing = true;
        try { to.SelectedItem = list[row.ListIndex]; }
        finally { _syncing = false; }
    }

    private void ScrollToRow(int index)
    {
        if (_viewModel?.Sides is not { } sides || index < 0 || index >= sides.Left.Count) return;

        _syncing = true;   // both grids are moved explicitly below
        try
        {
            LeftGrid.SelectedItem = sides.Left[index];
            RightGrid.SelectedItem = sides.Right[index];
            LeftGrid.ScrollIndexIntoView(index);
            RightGrid.ScrollIndexIntoView(index);
        }
        finally { _syncing = false; }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MdbHub.Core;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.GridView;

namespace MdbHub.App.Views;

/// <summary>
/// Two grids showing the same lines of a <see cref="SideBySide"/> (left and right), like a merge tool: differing rows and cells are colored,
/// and the grids scroll (both ways) and select together. Sorting and filtering are off because they would break the line-up.
/// Optionally each side can be edited and each line gets an arrow button that copies it to the other side.
/// </summary>
public partial class SideBySideView : UserControl
{
    public static readonly DependencyProperty SidesProperty = DependencyProperty.Register(
        nameof(Sides), typeof(SideBySide), typeof(SideBySideView),
        new PropertyMetadata(null, (d, _) => ((SideBySideView)d).OnSidesChanged()));

    public static readonly DependencyProperty LeftTitleProperty = DependencyProperty.Register(
        nameof(LeftTitle), typeof(string), typeof(SideBySideView), new PropertyMetadata("왼쪽"));

    public static readonly DependencyProperty RightTitleProperty = DependencyProperty.Register(
        nameof(RightTitle), typeof(string), typeof(SideBySideView), new PropertyMetadata("오른쪽"));

    public static readonly DependencyProperty IsLeftEditableProperty = DependencyProperty.Register(
        nameof(IsLeftEditable), typeof(bool), typeof(SideBySideView),
        new PropertyMetadata(false, (d, e) => ((SideBySideView)d).LeftGrid.IsReadOnly = !(bool)e.NewValue));

    public static readonly DependencyProperty IsRightEditableProperty = DependencyProperty.Register(
        nameof(IsRightEditable), typeof(bool), typeof(SideBySideView),
        new PropertyMetadata(false, (d, e) => ((SideBySideView)d).RightGrid.IsReadOnly = !(bool)e.NewValue));

    /// <summary>Shows a button column on each side to copy a line to the other side, and a marker column for lines that will be saved.</summary>
    public static readonly DependencyProperty ShowMergeColumnsProperty = DependencyProperty.Register(
        nameof(ShowMergeColumns), typeof(bool), typeof(SideBySideView),
        new PropertyMetadata(false, (d, _) => ((SideBySideView)d).RebuildColumns()));

    public static readonly DependencyProperty CopyToRightCommandProperty = DependencyProperty.Register(
        nameof(CopyToRightCommand), typeof(ICommand), typeof(SideBySideView));

    public static readonly DependencyProperty CopyToLeftCommandProperty = DependencyProperty.Register(
        nameof(CopyToLeftCommand), typeof(ICommand), typeof(SideBySideView));

    /// <summary>The line selected on either side (the same line on both).</summary>
    public static readonly DependencyProperty SelectedLineProperty = DependencyProperty.Register(
        nameof(SelectedLine), typeof(SideRow), typeof(SideBySideView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private static readonly Brush EditedCellBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9E, 0xD0, 0xFF)));
    private static readonly Brush PendingRowBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xF2, 0xFF)));
    private static readonly Brush ChangedRowBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xD0)));
    private static readonly Brush ChangedCellBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x66)));
    private static readonly Brush LeftOnlyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0xD6)));
    private static readonly Brush RightOnlyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD3, 0xF2, 0xD3)));
    private static readonly Brush FillerBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE9, 0xE9, 0xE9)));

    private ScrollViewer? _leftScroll;
    private ScrollViewer? _rightScroll;
    private bool _syncing;

    public SideBySideView()
    {
        InitializeComponent();
        LeftGrid.IsReadOnly = !IsLeftEditable;
        RightGrid.IsReadOnly = !IsRightEditable;
        Loaded += (_, _) => EnsureScrollSync();
    }

    public SideBySide? Sides
    {
        get => (SideBySide?)GetValue(SidesProperty);
        set => SetValue(SidesProperty, value);
    }

    /// <summary>Lets the user edit values on the left side (key columns and blank lines stay locked).</summary>
    public bool IsLeftEditable
    {
        get => (bool)GetValue(IsLeftEditableProperty);
        set => SetValue(IsLeftEditableProperty, value);
    }

    /// <summary>Lets the user edit values on the right side (key columns and blank lines stay locked).</summary>
    public bool IsRightEditable
    {
        get => (bool)GetValue(IsRightEditableProperty);
        set => SetValue(IsRightEditableProperty, value);
    }

    public bool ShowMergeColumns
    {
        get => (bool)GetValue(ShowMergeColumnsProperty);
        set => SetValue(ShowMergeColumnsProperty, value);
    }

    public ICommand? CopyToRightCommand
    {
        get => (ICommand?)GetValue(CopyToRightCommandProperty);
        set => SetValue(CopyToRightCommandProperty, value);
    }

    public ICommand? CopyToLeftCommand
    {
        get => (ICommand?)GetValue(CopyToLeftCommandProperty);
        set => SetValue(CopyToLeftCommandProperty, value);
    }

    public SideRow? SelectedLine
    {
        get => (SideRow?)GetValue(SelectedLineProperty);
        set => SetValue(SelectedLineProperty, value);
    }

    /// <summary>Called with (side, line, column index, typed text) before an edit is accepted; returns an error message to reject it, or null.</summary>
    public Func<MergeSide, SideRow, int, string, string?>? EditValidator { get; set; }

    /// <summary>Raised after an edit was accepted (side, line, column index, typed text).</summary>
    public event Action<MergeSide, SideRow, int, string>? CellEdited;

    public string LeftTitle
    {
        get => (string)GetValue(LeftTitleProperty);
        set => SetValue(LeftTitleProperty, value);
    }

    public string RightTitle
    {
        get => (string)GetValue(RightTitleProperty);
        set => SetValue(RightTitleProperty, value);
    }

    /// <summary>Selects the given line on both sides and scrolls it into view.</summary>
    public void ScrollToRow(int index)
    {
        if (Sides is not { } sides || index < 0 || index >= sides.Left.Count) return;

        _syncing = true;   // both grids are moved explicitly below
        try
        {
            LeftGrid.SelectedItem = sides.Left[index];
            RightGrid.SelectedItem = sides.Right[index];
            LeftGrid.ScrollIndexIntoView(index);
            RightGrid.ScrollIndexIntoView(index);
        }
        finally { _syncing = false; }
        SelectedLine = sides.Left[index];
    }

    /// <summary>Re-colors the lines currently shown, after the lines' state was changed in place (e.g. by a copy).</summary>
    public void RefreshVisuals()
    {
        foreach (var grid in new[] { LeftGrid, RightGrid })
        {
            foreach (var row in Descendants<GridViewRow>(grid))
            {
                ApplyRowBrush(row);
                foreach (var cell in Descendants<GridViewCell>(row)) ApplyCellBrush(cell);
            }
        }
    }

    private void OnSidesChanged()
    {
        LeftGrid.ItemsSource = Sides?.Left;
        RightGrid.ItemsSource = Sides?.Right;
        RebuildColumns();
        // The grids may only get their scroll viewers once they have been shown with data.
        Dispatcher.BeginInvoke(EnsureScrollSync, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Both grids show the same data columns with the same widths (so they can also scroll sideways together):
    // a line number, optional merge columns, then one column per table column (read from SideRow.Cells[i]).
    private void RebuildColumns()
    {
        var sides = Sides;
        foreach (var (grid, isLeft) in new[] { (LeftGrid, true), (RightGrid, false) })
        {
            grid.Columns.Clear();
            grid.FrozenColumnCount = 0;
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
                IsReadOnly = true,
            });

            if (ShowMergeColumns)
            {
                grid.Columns.Add(new Telerik.Windows.Controls.GridViewColumn
                {
                    Header = isLeft ? "→" : "←",
                    UniqueName = "copy",
                        Width = 44,
                    IsReadOnly = true,
                    CellTemplate = (DataTemplate)Resources[isLeft ? "CopyToRightTemplate" : "CopyToLeftTemplate"],
                });
                grid.Columns.Add(new Telerik.Windows.Controls.GridViewColumn
                {
                    Header = "저장",
                    UniqueName = "pending",
                        Width = 40,
                    IsReadOnly = true,
                    CellTemplate = (DataTemplate)Resources["PendingTemplate"],
                });
            }

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

            // The line number and the merge buttons stay put while the data columns scroll sideways.
            grid.FrozenColumnCount = ShowMergeColumns ? 3 : 1;
            grid.FrozenColumnsSplitterVisibility = Visibility.Collapsed;
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
        if (e.Row is GridViewRow row) ApplyRowBrush(row);
    }

    private static void ApplyRowBrush(GridViewRow row)
    {
        row.Background = row.DataContext is SideRow line ? line.Kind switch
        {
            SideRowKind.Changed => ChangedRowBrush,
            SideRowKind.LeftOnly => LeftOnlyBrush,
            SideRowKind.RightOnly => RightOnlyBrush,
            SideRowKind.Filler => FillerBrush,
            _ when line.IsPending => PendingRowBrush,   // equal on both sides, but this side will be written
            _ => null,
        } : null;
    }

    private void Grid_CellLoaded(object? sender, CellEventArgs e)
    {
        if (e.Cell is GridViewCell cell) ApplyCellBrush(cell);
    }

    // A cell whose value differs from what the file holds wins over "differs from the other side".
    private static void ApplyCellBrush(GridViewCell cell)
    {
        if (cell.DataContext is SideRow row && ColumnIndex(cell) is { } index)
        {
            if (row.EditedColumns.Contains(index)) { cell.Background = EditedCellBrush; return; }
            if (row.ChangedColumns.Contains(index)) { cell.Background = ChangedCellBrush; return; }
        }
        cell.ClearValue(BackgroundProperty);
    }

    private static int? ColumnIndex(GridViewCell cell) =>
        cell.Column?.UniqueName is { Length: > 1 } name && name[0] == 'c' && int.TryParse(name.AsSpan(1), out var i) ? i : null;

    // ---- editing ----

    private bool IsEditable(object? grid) => ReferenceEquals(grid, LeftGrid) ? IsLeftEditable : IsRightEditable;

    private static MergeSide SideOf(object? grid, SideBySideView view) => ReferenceEquals(grid, view.LeftGrid) ? MergeSide.Left : MergeSide.Right;

    // Only real values of rows that exist can be edited: not key columns, not blank stand-in lines, not the line number.
    private void Grid_BeginningEdit(object? sender, GridViewBeginningEditRoutedEventArgs e)
    {
        var allowed = IsEditable(sender)
            && e.Cell.DataContext is SideRow { Kind: not SideRowKind.Filler }
            && ColumnIndex(e.Cell) is { } index
            && Sides is { } sides && !sides.KeyColumns.Contains(index);
        if (!allowed) e.Cancel = true;
    }

    private void Grid_CellValidating(object? sender, GridViewCellValidatingEventArgs e)
    {
        if (e.Cell.DataContext is not SideRow row || ColumnIndex(e.Cell) is not { } index) return;

        var error = EditValidator?.Invoke(SideOf(sender, this), row, index, e.NewValue as string ?? "");
        if (error is null) return;
        e.IsValid = false;
        e.ErrorMessage = error;
    }

    private void Grid_CellEditEnded(object? sender, GridViewCellEditEndedEventArgs e)
    {
        if (e.EditAction != GridViewEditAction.Commit || e.Cell.DataContext is not SideRow row || ColumnIndex(e.Cell) is not { } index) return;

        CellEdited?.Invoke(SideOf(sender, this), row, index, e.NewData as string ?? "");
        RefreshVisuals();   // the handler may have changed this line's state (and the other side's line)
    }

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

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var inner in Descendants<T>(child)) yield return inner;
        }
    }

    private void LeftGrid_SelectionChanged(object? sender, SelectionChangeEventArgs e) => MirrorSelection(LeftGrid, RightGrid, sides => sides.Right);

    private void RightGrid_SelectionChanged(object? sender, SelectionChangeEventArgs e) => MirrorSelection(RightGrid, LeftGrid, sides => sides.Left);

    // Selecting a line on one side selects the same line on the other side.
    private void MirrorSelection(RadGridView from, RadGridView to, Func<SideBySide, IReadOnlyList<SideRow>> otherSide)
    {
        if (from.SelectedItem is SideRow selected) SelectedLine = selected;
        if (_syncing || from.SelectedItem is not SideRow row || Sides is not { } sides) return;

        var list = otherSide(sides);
        if (row.ListIndex >= list.Count || ReferenceEquals(to.SelectedItem, list[row.ListIndex])) return;

        _syncing = true;
        try { to.SelectedItem = list[row.ListIndex]; }
        finally { _syncing = false; }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}





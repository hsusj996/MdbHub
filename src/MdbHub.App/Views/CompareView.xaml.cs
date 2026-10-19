using System.Data;
using System.Windows.Controls;
using System.Windows.Media;
using MdbHub.App.ViewModels;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.GridView;

namespace MdbHub.App.Views;

public partial class CompareView : UserControl
{
    private static readonly Brush ChangedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF2, 0xA8)));
    private static readonly Brush LeftOnlyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0xD6)));
    private static readonly Brush RightOnlyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD3, 0xF2, 0xD3)));

    public CompareView() => InitializeComponent();

    // Rows get recycled while scrolling, so the background must be reset every time (null = default look).
    private void DiffGrid_RowLoaded(object? sender, RowLoadedEventArgs e)
    {
        if (e.Row is not GridViewRow row) return;

        var status = (row.DataContext as DataRowView)?[CompareViewModel.StatusColumn] as string;
        row.Background = status switch
        {
            CompareViewModel.ChangedLabel => ChangedBrush,
            CompareViewModel.LeftOnlyLabel => LeftOnlyBrush,
            CompareViewModel.RightOnlyLabel => RightOnlyBrush,
            _ => null,
        };
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

using System.Windows;
using System.Windows.Controls;
using MdbHub.App.ViewModels;
using MdbHub.Core;

namespace MdbHub.App.Views;

public partial class ApplyView : UserControl
{
    private ApplyViewModel? _viewModel;

    public ApplyView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetailView.CellEdited += OnCellEdited;
        DetailView.EditValidator = ValidateEdit;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.ScrollToRowRequested -= DetailView.ScrollToRow;
        _viewModel = e.NewValue as ApplyViewModel;
        if (_viewModel is not null) _viewModel.ScrollToRowRequested += DetailView.ScrollToRow;
    }

    // Only the right side ("after applying") is editable here.
    private string? ValidateEdit(MergeSide side, SideRow row, int column, string text) =>
        side == MergeSide.Right && _viewModel is not null ? _viewModel.ValidateEdit(row, column, text) : "이 쪽은 수정할 수 없습니다.";

    private void OnCellEdited(MergeSide side, SideRow row, int column, string text)
    {
        if (side == MergeSide.Right) _viewModel?.OnCellEdited(row, column, text);
    }
}

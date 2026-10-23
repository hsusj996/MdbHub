using System.Windows;
using System.Windows.Controls;
using MdbHub.App.ViewModels;

namespace MdbHub.App.Views;

public partial class CompareView : UserControl
{
    private CompareViewModel? _viewModel;

    public CompareView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToRowRequested -= SidesView.ScrollToRow;
            _viewModel.MergeChanged -= SidesView.RefreshVisuals;
            SidesView.CellEdited -= _viewModel.OnCellEdited;
        }
        _viewModel = e.NewValue as CompareViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollToRowRequested += SidesView.ScrollToRow;
            _viewModel.MergeChanged += SidesView.RefreshVisuals;
            SidesView.CellEdited += _viewModel.OnCellEdited;
            SidesView.EditValidator = _viewModel.ValidateEdit;
        }
        else
        {
            SidesView.EditValidator = null;
        }
    }
}

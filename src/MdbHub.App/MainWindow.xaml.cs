using System.Windows;
using MdbHub.App.ViewModels;

namespace MdbHub.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}

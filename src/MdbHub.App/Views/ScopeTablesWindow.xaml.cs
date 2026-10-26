using System.Windows;

namespace MdbHub.App.Views;

public partial class ScopeTablesWindow : Window
{
    public ScopeTablesWindow() => InitializeComponent();

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

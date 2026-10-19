using System.Windows;

namespace MdbHub.App.Views;

public partial class KeySetupWindow : Window
{
    public KeySetupWindow() => InitializeComponent();

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

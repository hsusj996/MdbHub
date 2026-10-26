using System.Windows;
using Microsoft.Win32;

namespace MdbHub.App.Infrastructure;

public static class Dialogs
{
    private const string MdbFilter = "Access mdb (*.mdb)|*.mdb|모든 파일 (*.*)|*.*";

    public static string? OpenMdb(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = MdbFilter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static string? SaveMdb(string title)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = MdbFilter, DefaultExt = ".mdb", OverwritePrompt = false };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>How errors are shown; replaced in automated checks so no modal box waits for a click.</summary>
    public static Action<string> ErrorHandler { get; set; } =
        message => MessageBox.Show(Trim(message), "오류", MessageBoxButton.OK, MessageBoxImage.Error);

    public static void Error(string message) => ErrorHandler(message);

    /// <summary>How notices are shown; replaced in automated checks.</summary>
    public static Action<string> InfoHandler { get; set; } =
        message => MessageBox.Show(message, "MdbHub", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>How yes/no questions are asked (the default answer is No); replaced in automated checks.</summary>
    public static Func<string, bool> ConfirmHandler { get; set; } =
        message => MessageBox.Show(message, "확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>
    /// How the user is asked what to do with tables that cannot be grouped by the chosen scope columns; returns the decision per table,
    /// or null when cancelled. Replaced in automated checks.
    /// </summary>
    public static Func<IReadOnlyList<string>, IReadOnlyList<string>, IReadOnlyDictionary<string, MdbHub.Data.TableScopeChoice>?> ScopeTablesHandler { get; set; } =
        ShowScopeTablesWindow;

    public static IReadOnlyDictionary<string, MdbHub.Data.TableScopeChoice>? AskScopeTables(IReadOnlyList<string> columns, IReadOnlyList<string> tables) =>
        ScopeTablesHandler(columns, tables);

    private static IReadOnlyDictionary<string, MdbHub.Data.TableScopeChoice>? ShowScopeTablesWindow(IReadOnlyList<string> columns, IReadOnlyList<string> tables)
    {
        var viewModel = new MdbHub.App.ViewModels.ScopeTablesViewModel(columns, tables);
        var window = new MdbHub.App.Views.ScopeTablesWindow { DataContext = viewModel, Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true ? viewModel.ToChoices() : null;
    }

    public static void Info(string message) => InfoHandler(message);

    public static bool Confirm(string message) => ConfirmHandler(message);

    private static string Trim(string message) => message.Length <= 1500 ? message : message[..1500] + Environment.NewLine + "...";
}

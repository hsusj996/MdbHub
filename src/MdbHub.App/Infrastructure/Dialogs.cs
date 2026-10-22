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

    public static void Info(string message) => MessageBox.Show(message, "MdbHub", MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string message) =>
        MessageBox.Show(message, "확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private static string Trim(string message) => message.Length <= 1500 ? message : message[..1500] + Environment.NewLine + "...";
}

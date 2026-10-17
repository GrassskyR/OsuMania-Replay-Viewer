using System.IO;
using System.Windows;
using System.Windows.Threading;
using ManiaReplayMaster.App.Localization;

namespace ManiaReplayMaster.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                CrashLog.Write(ex);
            }
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception);
        MessageBox.Show(
            Localizer.Text("Crash.Message", e.Exception.Message, CrashLog.LastPath),
            "Mania Replay Viewer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}

internal static class CrashLog
{
    public static string LastPath { get; private set; } = string.Empty;

    public static void Write(Exception ex)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ManiaReplayViewer");
            Directory.CreateDirectory(directory);
            LastPath = Path.Combine(directory, "crash.log");
            File.AppendAllText(LastPath,
                $"[{DateTimeOffset.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}

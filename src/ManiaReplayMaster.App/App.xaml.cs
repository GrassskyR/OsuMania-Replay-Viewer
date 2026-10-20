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

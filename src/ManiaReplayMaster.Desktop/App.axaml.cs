using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ManiaReplayMaster.Presentation;

namespace ManiaReplayMaster.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) CrashLog.Write(ex);
        };
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            CrashLog.Write(args.Exception);
            // Keep the error visible without a second modal dispatcher.
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow main })
                main.ViewModel.Log(ManiaReplayMaster.Presentation.Localization.Localizer.Text("Crash.Message", args.Exception.Message, CrashLog.LastPath));
            args.Handled = true;
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

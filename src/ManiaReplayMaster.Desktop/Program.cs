using Avalonia;
using ManiaReplayMaster.Presentation;

namespace ManiaReplayMaster.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) CrashLog.Write(ex);
        };
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseWin32()
        .UseSkia()
        .UseHarfBuzz()
        .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
        .LogToTrace();
}

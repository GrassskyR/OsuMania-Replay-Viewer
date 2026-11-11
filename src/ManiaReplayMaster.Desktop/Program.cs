using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Localization;
using AtomUI.Theme;
using AtomUI.Theme.Algorithms;
using AtomUI.Theme.Configuration;
using Avalonia;
using ManiaReplayMaster.Presentation.Localization;

namespace ManiaReplayMaster.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseAtomUIPlatformDetect()
        .WithAtomUIDefaultOptions()
        .UseAtomUI(builder =>
        {
            var language = Localizer.ResolveLanguage(Localizer.Current.Preference,
                System.Globalization.CultureInfo.CurrentUICulture);
            builder.UseLanguages(language == "zh-Hans" ? LanguageTags.ZhCN : LanguageTags.EnUS,
                [LanguageTags.EnUS, LanguageTags.ZhCN]);
            builder.WithInitialTheme(IThemeManager.DEFAULT_THEME_ID, new ThemeConfigBuilder()
                .WithAlgorithms(ThemeAlgorithm.Dark, ThemeAlgorithm.Compact)
                .WithToken("FontFamily", "Segoe UI, Microsoft YaHei")
                .WithToken("FontSize", "13")
                .Build());
            builder.UseDesktopControls();
        })
        .LogToTrace();
}

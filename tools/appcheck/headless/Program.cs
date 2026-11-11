using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Localization;
using AtomUI.Theme;
using AtomUI.Theme.Algorithms;
using AtomUI.Theme.Configuration;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ManiaReplayMaster.Presentation;
using ManiaReplayMaster.Desktop;
using Button = Avalonia.Controls.Button;
using ComboBox = Avalonia.Controls.ComboBox;
using ListBox = Avalonia.Controls.ListBox;
using ScrollViewer = Avalonia.Controls.ScrollViewer;
using TextBox = Avalonia.Controls.TextBox;
using Popup = AtomUI.Desktop.Controls.Popup;

var screenshotDirectory = Environment.GetEnvironmentVariable("MRM_LAYOUT_SCREENSHOTS");
using var preferences = new RestorePreferences();
var builder = AppBuilder.Configure<App>().UseAtomUI(atom =>
{
    atom.UseDesktopControls();
    atom.UseLanguages(LanguageTags.EnUS, [LanguageTags.EnUS, LanguageTags.ZhCN]);
    atom.WithInitialTheme(IThemeManager.DEFAULT_THEME_ID, new ThemeConfigBuilder()
        .WithAlgorithms(ThemeAlgorithm.Dark, ThemeAlgorithm.Compact)
        .WithToken("FontFamily", "Segoe UI, Microsoft YaHei")
        .WithToken("FontSize", "13")
        .Build());
});
if (screenshotDirectory is not null) builder = builder.UseSkia();
builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = screenshotDirectory is null }).SetupWithoutStarting();
var testDirectory = Path.Combine(Path.GetTempPath(), "mrm-headless-" + Guid.NewGuid().ToString("N"));
var window = new MainWindow(new MainViewModel(new UiSettings(Path.Combine(testDirectory, "settings.json"))));
var vm = (MainViewModel)window.DataContext!;
vm.AutoDetectFromGame = false;
vm.MemoryEnabled = false;
window.Show();
Dispatcher.UIThread.RunJobs();
PumpUntil(() => !vm.IsCheckingFfmpeg);
CheckResponsiveLayout();
var codec = window.FindControl<AutoComplete>("CodecBox")!;
var savedCodec = vm.Codec;
var button = window.GetVisualDescendants().OfType<Button>()
    .Single(b => AutomationProperties.GetAutomationId(b) == "CodecDropDownButton");
vm.AddAvailableCodecs(["h264_amf"]);
button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
if (!codec.IsDropDownOpen) throw new Exception("Encoder arrow did not open the menu.");
var popup = codec.GetTemplateDescendants().OfType<Popup>().Single();
var list = popup.Child!.GetVisualDescendants().OfType<ListBox>().Single();
var options = list.Items.Cast<IAutoCompleteOption>().ToArray();
if (!options.Any(option => option.Header?.ToString() == "h264_amf") || !options.Any(option => option.Header?.ToString() == "libx264"))
    throw new Exception("Detected items missing from popup.");
list.SelectedItem = options.Single(option => option.Header?.ToString() == "h264_amf");
codec.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
Dispatcher.UIThread.RunJobs();
if (vm.Codec != "h264_amf" || codec.IsDropDownOpen) throw new Exception("Choosing an encoder did not commit/close.");
var savedRendering = new ManiaReplayMaster.Core.Model.Config();
new UiSettings(Path.Combine(testDirectory, "settings.json")).ApplyRendering(savedRendering);
if (savedRendering.Codec != "h264_amf") throw new Exception("Chosen encoder was not saved.");
button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
if (codec.IsDropDownOpen) throw new Exception("Encoder arrow did not close the menu.");
vm.Codec = savedCodec;
Console.WriteLine("PASS encoder menu opens, selects, saves and closes");
var ffmpegPathBox = window.FindControl<TextBox>("FfmpegPathBox")!;
var minimal = Environment.GetEnvironmentVariable("MRM_FFMPEG");
var external = Environment.GetEnvironmentVariable("MRM_FIXTURE_FFMPEG");
if (minimal is not null && external is not null)
{
    Directory.CreateDirectory(testDirectory);
    var copied = Path.Combine(testDirectory, "中文 FFmpeg.exe");
    File.Copy(minimal, copied);
    var saved = vm.Codec;
    PumpTask(vm.ChangeFfmpegAsync(copied, CancellationToken.None));
    if (vm.FfmpegPath != copied || ffmpegPathBox.Text != copied || vm.Codec != saved)
        throw new Exception("Custom FFmpeg selection/binding did not preserve preferences.");
    if (vm.CodecChoices.Contains("h264_amf") || !vm.CodecChoices.Contains("libx264"))
        throw new Exception("Old hardware candidates remained after selecting minimal FFmpeg.");
    if (new UiSettings(Path.Combine(testDirectory, "settings.json")).FfmpegPath != copied)
        throw new Exception("Custom FFmpeg was not saved.");
    PumpTask(vm.ChangeFfmpegAsync(Path.Combine(testDirectory, "missing.exe"), CancellationToken.None));
    if (vm.FfmpegPath != copied) throw new Exception("Invalid FFmpeg replaced the current selection.");
    PumpTask(vm.ChangeFfmpegAsync(external, CancellationToken.None));
    if (vm.FfmpegPath != Path.GetFullPath(external) || vm.ResolveFfmpeg() != Path.GetFullPath(external))
        throw new Exception("External FFmpeg was not selected consistently.");
    PumpTask(vm.ChangeFfmpegAsync(string.Empty, CancellationToken.None));
    if (vm.FfmpegPath != string.Empty || new UiSettings(Path.Combine(testDirectory, "settings.json")).FfmpegPath != string.Empty)
        throw new Exception("Restoring bundled FFmpeg was not persisted.");
    Console.WriteLine("PASS actual window FFmpeg path binding, Unicode path, persistence, invalid selection, candidate refresh and restore");
}
window.Close();
for (var i = 0; i < 500 && window.IsVisible; i++)
{
    Thread.Sleep(20);
    Dispatcher.UIThread.RunJobs();
}
if (window.IsVisible) throw new Exception("Headless window did not finish cancellation/close.");
Console.WriteLine("PASS window closes cleanly");
if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, true);

void CheckResponsiveLayout()
{
    var scroll = window.FindControl<ScrollViewer>("ContentScroll")!;
    var parameterFields = new[] { "SpeedSlider", "WidthBox", "HeightBox", "FpsBox", "CodecBox", "PresetBox" };
    var savedLanguage = vm.L.Preference;
    foreach (var language in new[] { "en", "zh-Hans" })
    foreach (var (width, height, scaling) in new[] { (900, 620, 1d), (640, 420, 1.5), (480, 320, 2d) })
    {
        window.FindControl<ComboBox>("LanguageBox")!.SelectedIndex = language == "en" ? 2 : 1;
        scroll.Offset = default;
        window.SetRenderScaling(scaling);
        // ApplyScreenBounds also lowers the minimum when a monitor has fewer logical pixels.
        window.MinWidth = Math.Min(480, width);
        window.Width = width;
        window.Height = height;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        foreach (var name in parameterFields.Concat(new[] { "ReplayBox", "BeatmapBox", "OutputBox", "FfmpegPathBox", "ScoreCandidateBox" }))
        {
            var control = window.FindControl<Control>(name)!;
            var point = control.TranslatePoint(default, window)!.Value;
            if (point.X < 0 || point.X + control.Bounds.Width > window.ClientSize.Width + 1 || control.Bounds.Width < 40)
                throw new Exception($"{name} is clipped horizontally at {width}x{height}, {scaling:P0}.");
        }
        foreach (var name in new[] { "RenderButton", "JudgeOnlyButton", "CancelButton" })
        {
            var control = window.FindControl<Button>(name)!;
            var point = control.TranslatePoint(default, window)!.Value;
            if (point.Y < 0 || point.Y + control.Bounds.Height > window.ClientSize.Height + 1)
                throw new Exception($"{name} is not visible at {width}x{height}, {scaling:P0}.");
        }
        if (window.FindControl<Button>("RenderButton")!.Content?.ToString() != (language == "en" ? "Render" : "渲染视频"))
            throw new Exception("Changing language did not update visible actions.");
        if (height <= 360 && scroll.Extent.Height <= scroll.Viewport.Height)
            throw new Exception("Small window did not make settings scrollable.");
        if (width == 900 && height == 620 && scroll.Extent.Height > scroll.Viewport.Height + 1)
            throw new Exception($"Default window unnecessarily scrolls in {language}.");
        if (screenshotDirectory is not null)
        {
            Directory.CreateDirectory(screenshotDirectory);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.GetLastRenderedFrame()!;
            frame.Save(Path.Combine(screenshotDirectory, $"layout-{language}-{width}x{height}-{scaling}.png"),
                Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        Dispatcher.UIThread.RunJobs();
        var log = window.FindControl<ListBox>("LogBox")!;
        var logPoint = log.TranslatePoint(default, window)!.Value;
        if (logPoint.Y < 0 || logPoint.Y + log.Bounds.Height > window.ClientSize.Height)
            throw new Exception("Log cannot be reached by scrolling the compact window.");
    }
    window.FindControl<ComboBox>("LanguageBox")!.SelectedIndex = savedLanguage switch { "zh-Hans" => 1, "en" => 2, _ => 0 };
    window.MinWidth = 480;
    window.SetRenderScaling(1);
    window.Width = 900;
    window.Height = 620;
    scroll.Offset = default;
    Dispatcher.UIThread.RunJobs();
    Console.WriteLine("PASS bilingual controls remain usable at normal, medium and small window sizes");
}

void PumpTask(Task task)
{
    PumpUntil(() => task.IsCompleted);
    task.GetAwaiter().GetResult();
}

void PumpUntil(Func<bool> complete)
{
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (!complete())
    {
        if (DateTime.UtcNow > deadline) throw new TimeoutException("Headless operation did not finish.");
        Thread.Sleep(10);
        Dispatcher.UIThread.RunJobs();
    }
    Dispatcher.UIThread.RunJobs();
}

// The window shares the process-wide localizer. Restore its persisted preferences even on assertion failure.
public sealed class RestorePreferences : IDisposable
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ManiaReplayViewer", "ui-settings.json");
    private readonly byte[]? _bytes;

    public RestorePreferences() => _bytes = File.Exists(_path) ? File.ReadAllBytes(_path) : null;

    public void Dispose()
    {
        if (_bytes is null) File.Delete(_path);
        else if (!File.Exists(_path) || !File.ReadAllBytes(_path).SequenceEqual(_bytes))
            File.WriteAllBytes(_path, _bytes);
    }
}

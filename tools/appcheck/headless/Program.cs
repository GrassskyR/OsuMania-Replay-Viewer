using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ManiaReplayMaster.Presentation;
using ManiaReplayMaster.Desktop;

var screenshotDirectory = Environment.GetEnvironmentVariable("MRM_LAYOUT_SCREENSHOTS");
var builder = AppBuilder.Configure<App>();
if (screenshotDirectory is not null) builder = builder.UseSkia();
builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = screenshotDirectory is null }).SetupWithoutStarting();
var testDirectory = Path.Combine(Path.GetTempPath(), "mrm-headless-" + Guid.NewGuid().ToString("N"));
var localizer = new ManiaReplayMaster.Presentation.Localization.Localizer(
    System.Globalization.CultureInfo.GetCultureInfo("en"), Path.Combine(testDirectory, "language.json"));
if (localizer["Browse"] != "Browse") throw new Exception("English presentation resources are missing.");
localizer.Preference = "zh-Hans";
if (localizer["Browse"] != "浏览") throw new Exception("Chinese presentation resources are missing.");
Console.WriteLine("PASS presentation resources resolve in English and Chinese without the legacy UI project");
var window = new MainWindow(new MainViewModel(new UiSettings(Path.Combine(testDirectory, "settings.json"))));
var vm = (MainViewModel)window.DataContext!;
vm.AutoDetectFromGame = false;
vm.MemoryEnabled = false;
window.Show();
Dispatcher.UIThread.RunJobs();
PumpUntil(() => !vm.IsCheckingFfmpeg);
CheckResponsiveLayout();
var codec = window.FindControl<AutoCompleteBox>("CodecBox")!;
var savedCodec = vm.Codec;
var selection = new EncoderChoice { Codec = savedCodec };
using var isolatedBinding = codec.Bind(AutoCompleteBox.TextProperty,
    new Binding(nameof(EncoderChoice.Codec)) { Source = selection, Mode = BindingMode.TwoWay });
var button = window.GetVisualDescendants().OfType<Button>()
    .Single(b => AutomationProperties.GetAutomationId(b) == "CodecDropDownButton");
vm.AddAvailableCodecs(["h264_amf"]);
button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
if (!codec.IsDropDownOpen) throw new Exception("Encoder arrow did not open the menu.");
Console.WriteLine("PASS actual window encoder arrow opens dropdown without desktop UI");
var popup = codec.GetTemplateDescendants().OfType<Popup>().Single();
var list = popup.Child!.GetVisualDescendants().OfType<ListBox>().Single();
if (!list.Items.Contains("h264_amf") || !list.Items.Contains("libx264")) throw new Exception("Detected items missing from popup.");
Console.WriteLine("PASS popup contains detected encoders: " + string.Join(", ", list.Items));
list.SelectedItem = "h264_amf";
codec.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
Dispatcher.UIThread.RunJobs();
if (selection.Codec != "h264_amf" || codec.IsDropDownOpen) throw new Exception("Choosing an encoder did not commit/close.");
if (vm.Codec != savedCodec) throw new Exception("Isolated test changed the real preferences.");
Console.WriteLine("PASS choosing detected encoder updates binding and closes popup, with isolated test state");
button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
Dispatcher.UIThread.RunJobs();
if (codec.IsDropDownOpen) throw new Exception("Encoder arrow did not close the menu.");
Console.WriteLine("PASS encoder arrow toggles closed");
var ffmpegPathBox = window.FindControl<TextBox>("FfmpegPathBox")!;
if (!ffmpegPathBox.IsReadOnly || string.IsNullOrWhiteSpace(ffmpegPathBox.PlaceholderText))
    throw new Exception("FFmpeg path control is not configured.");
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
Console.WriteLine("PASS close cancels background encoder discovery");
if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, true);

void CheckResponsiveLayout()
{
    foreach (var (width, height) in new[] { (1920, 1040), (1366, 728), (2560, 1400), (3840, 2080) })
    foreach (var scaling in new[] { 1, 1.25, 1.5, 1.75, 2, 2.5, 3 })
    {
        var size = WindowSizing.ForWorkArea(width, height, scaling);
        if (size.Width * scaling > width || size.Height * scaling > height
            || size.MinWidth > size.Width || size.MinHeight > size.Height
            || size.MinWidth > size.AvailableWidth || size.MinHeight > size.AvailableHeight)
            throw new Exception($"Window does not fit {width}x{height} at {scaling:P0}.");
    }
    var defaultSize = WindowSizing.ForWorkArea(1920, 1040, 1);
    if (defaultSize.Width != 960 || defaultSize.Height != 680)
        throw new Exception("1080p default window is no longer compact.");

    var scroll = window.FindControl<ScrollViewer>("ContentScroll")!;
    var parameterFields = new[] { "SpeedSlider", "WidthBox", "HeightBox", "FpsBox", "CodecBox", "PresetBox" };
    foreach (var (width, height, scaling) in new[] { (960, 680, 1d), (960, 680, 1.25), (816, 450, 1.5), (640, 420, 1.75), (560, 360, 2d) })
    {
        window.SetRenderScaling(scaling);
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
        var speed = window.FindControl<Slider>("SpeedSlider")!.TranslatePoint(default, window)!.Value;
        var heightBox = window.FindControl<TextBox>("HeightBox")!.TranslatePoint(default, window)!.Value;
        if (width == 960 && Math.Abs(speed.Y - heightBox.Y) > 1)
            throw new Exception("Wide window did not arrange three fields per row.");
        if (width == 560 && heightBox.Y <= speed.Y)
            throw new Exception("Narrow window did not wrap parameter fields.");
        if (height == 360 && scroll.Extent.Height <= scroll.Viewport.Height)
            throw new Exception("Small window did not make settings scrollable.");
        if (screenshotDirectory is not null)
        {
            Directory.CreateDirectory(screenshotDirectory);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.GetLastRenderedFrame()!;
            frame.Save(Path.Combine(screenshotDirectory, $"layout-{width}x{height}-{scaling}.png"),
                Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
    scroll.Offset = new Vector(0, scroll.Extent.Height);
    Dispatcher.UIThread.RunJobs();
    var log = window.FindControl<ListBox>("LogBox")!;
    var logPoint = log.TranslatePoint(default, window)!.Value;
    if (logPoint.Y < 0 || logPoint.Y + log.Bounds.Height > window.ClientSize.Height)
        throw new Exception("Log cannot be reached by scrolling the compact window.");
    window.SetRenderScaling(1);
    window.Width = 960;
    window.Height = 680;
    scroll.Offset = default;
    Dispatcher.UIThread.RunJobs();
    Console.WriteLine("PASS work-area sizing (28 resolution/DPI combinations), responsive fields, pinned actions and scrollable log (5 layouts)");
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

public sealed class EncoderChoice
{
    public string Codec { get; set; } = "libx264";
}

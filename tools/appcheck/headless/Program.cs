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
using ManiaReplayMaster.App;
using ManiaReplayMaster.Desktop;

AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
var testDirectory = Path.Combine(Path.GetTempPath(), "mrm-headless-" + Guid.NewGuid().ToString("N"));
var window = new MainWindow(new MainViewModel(new UiSettings(Path.Combine(testDirectory, "settings.json"))));
var vm = (MainViewModel)window.DataContext!;
vm.AutoDetectFromGame = false;
vm.MemoryEnabled = false;
window.Show();
Dispatcher.UIThread.RunJobs();
PumpUntil(() => !vm.IsCheckingFfmpeg);
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

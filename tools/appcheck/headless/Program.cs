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
var window = new MainWindow();
var vm = (MainViewModel)window.DataContext!;
vm.AutoDetectFromGame = false;
vm.MemoryEnabled = false;
window.Show();
Dispatcher.UIThread.RunJobs();
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
window.Close();
for (var i = 0; i < 500 && window.IsVisible; i++)
{
    Thread.Sleep(20);
    Dispatcher.UIThread.RunJobs();
}
if (window.IsVisible) throw new Exception("Headless window did not finish cancellation/close.");
Console.WriteLine("PASS close cancels background encoder discovery");

public sealed class EncoderChoice
{
    public string Codec { get; set; } = "libx264";
}

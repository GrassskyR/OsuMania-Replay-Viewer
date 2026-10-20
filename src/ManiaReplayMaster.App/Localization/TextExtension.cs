using System.Windows.Data;
using System.Windows.Markup;
namespace ManiaReplayMaster.App.Localization;

/// <summary>A live indexer binding, so existing controls translate without recreating the window.</summary>
public sealed class TextExtension : MarkupExtension
{
    public TextExtension(string key) => Key = key;
    public string Key { get; }
    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]") { Source = Localizer.Current, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

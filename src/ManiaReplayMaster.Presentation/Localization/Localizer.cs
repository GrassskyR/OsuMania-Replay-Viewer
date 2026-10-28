using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace ManiaReplayMaster.Presentation.Localization;

public sealed class Localizer : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new("ManiaReplayMaster.Presentation.Localization.Strings", typeof(Localizer).Assembly);
    public static Localizer Current { get; } = new(CultureInfo.CurrentUICulture, UiSettings.Current);

    private readonly CultureInfo _systemCulture;
    private readonly UiSettings _settings;
    private string _preference;
    private CultureInfo _culture;

    public Localizer(CultureInfo systemCulture, string settingsPath) : this(systemCulture, new UiSettings(settingsPath)) { }

    private Localizer(CultureInfo systemCulture, UiSettings settings)
    {
        _systemCulture = systemCulture;
        _settings = settings;
        _preference = Normalize(settings.Language);
        _culture = CultureInfo.GetCultureInfo(ResolveLanguage(_preference, _systemCulture));
    }

    public string Preference
    {
        get => _preference;
        set
        {
            var preference = Normalize(value);
            if (_preference == preference) return;
            _preference = preference;
            _culture = CultureInfo.GetCultureInfo(ResolveLanguage(preference, _systemCulture));
            _settings.SetLanguage(preference);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preference)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            // Notify indexer subscribers and Avalonia compiled bindings.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        }
    }

    public static string ResolveLanguage(string preference, CultureInfo systemCulture)
        => Normalize(preference) switch
        {
            "zh-Hans" => "zh-Hans",
            "en" => "en",
            _ => systemCulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : "en"
        };

    private static string Normalize(string? value) => value is "en" or "zh-Hans" ? value : "auto";
    public string this[string key] => Resources.GetString(key, _culture) ?? key;
    public static string Text(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, Current[key], args);
    public event PropertyChangedEventHandler? PropertyChanged;
}

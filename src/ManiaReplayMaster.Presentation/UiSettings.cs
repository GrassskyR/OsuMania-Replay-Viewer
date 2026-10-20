using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.App;

/// <summary>Per-user GUI preferences, shared by localization and render controls.</summary>
public sealed class UiSettings
{
    public static UiSettings Current { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ManiaReplayViewer", "ui-settings.json"));

    private readonly string _path;
    private readonly JsonObject _values;

    public UiSettings(string path)
    {
        _path = path;
        try
        {
            _values = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _values = new JsonObject();
        }
    }

    public string Language => ReadString("language") ?? "auto";
    public string FfmpegPath => ReadString("ffmpegPath") ?? string.Empty;

    public void SetFfmpegPath(string path)
    {
        if (FfmpegPath == path) return;
        _values["ffmpegPath"] = path;
        Save();
    }

    public void SetLanguage(string language)
    {
        if (ReadString("language") == language) return;
        _values["language"] = language;
        Save();
    }

    public void ApplyRendering(Config config)
    {
        if (_values["speed"] is JsonValue value && value.TryGetValue<int>(out var speed) && speed is >= 1 and <= 100)
            config.Speed = speed;
        config.Codec = ReadString("codec") ?? "libx264";
        config.CodecPreset = ReadString("codecPreset") ?? "auto";
    }

    public void RememberRendering(Config config)
    {
        var changed = false;
        if (config.Speed is >= 1 and <= 100)
        {
            if (_values["speed"] is not JsonValue value || !value.TryGetValue<int>(out var speed) || speed != config.Speed)
            {
                _values["speed"] = config.Speed;
                changed = true;
            }
        }
        foreach (var (key, text) in new[] { ("codec", config.Codec), ("codecPreset", config.CodecPreset) })
        {
            if (string.IsNullOrWhiteSpace(text) || ReadString(key) == text) continue;
            _values[key] = text;
            changed = true;
        }
        if (changed) Save();
    }

    private string? ReadString(string key)
        => _values[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : null;

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                _values.WriteTo(writer);
            File.WriteAllBytes(_path, stream.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only preferences file must not block editing or rendering.
        }
    }
}

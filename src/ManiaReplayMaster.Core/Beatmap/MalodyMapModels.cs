// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using System.Text.Json;
using System.Text.Json.Serialization;
using ManiaReplayMaster.Core.Beatmap;
using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Core.Beatmap;

/// <summary>Port of the <c>MalodyMapModels.kt</c> data classes (Gson → System.Text.Json).</summary>
public sealed class McModel
{
    [JsonPropertyName("meta")] public McMeta Meta { get; set; } = new();

    [JsonPropertyName("time")] public List<McTime> Time { get; set; } = new();

    [JsonPropertyName("note")] public List<McNote> Note { get; set; } = new();

    public static McModel Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, MalodyJsonContext.Default.McModel)
               ?? throw new InvalidDataException("Empty .mc document.");
    }
}

public sealed class McMeta
{
    [JsonPropertyName("mode_ext")] public McModeExt ModeExt { get; set; } = new();
}

public sealed class McModeExt
{
    [JsonPropertyName("column")] public int Column { get; set; }
}

/// <summary>Port of <c>MCBeatObject</c>: <c>beat[0] + beat[1]/beat[2] + 1</c>.</summary>
public abstract class McBeatObject
{
    protected abstract IReadOnlyList<int> Beat { get; }

    public double BeatValue => Beat[0] + (double)Beat[1] / Beat[2] + 1;
}

public sealed class McTime : McBeatObject
{
    [JsonPropertyName("beat")] public List<int> BeatList { get; set; } = new();

    [JsonPropertyName("bpm")] public double Bpm { get; set; }

    protected override IReadOnlyList<int> Beat => BeatList;
}

public sealed class McNote : McBeatObject
{
    [JsonPropertyName("beat")] public List<int> BeatList { get; set; } = new();

    [JsonPropertyName("endbeat")] public List<int>? EndBeat { get; set; }

    [JsonPropertyName("column")] public int? Column { get; set; }

    [JsonPropertyName("sound")] public string? Sound { get; set; }

    [JsonPropertyName("offset")] public double? Offset { get; set; }

    protected override IReadOnlyList<int> Beat => BeatList;

    public double? EndBeatValue => EndBeat is null
        ? null
        : EndBeat[0] + (double)EndBeat[1] / EndBeat[2] + 1;
}

using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Core.Model;

/// <summary>
/// Port of <c>me.replaymaster.model.BeatMap</c>.
/// </summary>
public sealed class BeatMap
{
    public const string MuteMusic = "mute_sound.mp3";

    public const int TypeMania = 0;
    public const int TypeTaiko = 1;

    /// <summary>Currently unsupported (only used so mode-mismatch errors read right).</summary>
    public const int TypeOsuStd = 2;

    private static readonly string[] MusicExtensions = { "mp3", "ogg", "wav" };

    public int Key = -1;
    public List<Note> Notes = new();
    public string BgmPath = MuteMusic;
    public int BgmOffset;
    public double Od;
    public int Type = TypeMania;
    public double? SliderMultiplier;

    /// <summary>"osu!mania" / "osu!taiko" / "malody:key" / "osu!std"</summary>
    public string GameMode = string.Empty;

    public long Duration;

    public bool IsMania => Type == TypeMania;

    public bool IsTaiko => Type == TypeTaiko;

    /// <summary>
    /// Port of <c>BeatMap.checkValidation()</c>: if the declared audio file is missing, fall back
    /// to any sibling with a music extension, else to the mute placeholder.
    /// </summary>
    public string CheckValidation(Action<string, object?[]>? warning = null)
    {
        if (!File.Exists(BgmPath))
        {
            var parent = Path.GetDirectoryName(BgmPath);
            var original = BgmPath;
            var path = MuteMusic;
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                var wantedExt = Path.GetExtension(BgmPath).TrimStart('.');
                string? found = null;
                foreach (var file in Directory.EnumerateFiles(parent))
                {
                    var ext = Path.GetExtension(file).TrimStart('.');
                    if (string.Equals(ext, wantedExt, StringComparison.OrdinalIgnoreCase)
                        || MusicExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        found = file;
                        break;
                    }
                }

                path = found ?? MuteMusic;
            }

            warning?.Invoke("warning.bgm_not_found", new object?[] { original, path });
            BgmPath = path;
        }

        return BgmPath;
    }
}

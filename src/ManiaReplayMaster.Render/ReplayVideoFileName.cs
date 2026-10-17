using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ManiaReplayMaster.Core.IO;
using ManiaReplayMaster.Core.Replay;

namespace ManiaReplayMaster.Render;

/// <summary>Builds an osu! export-style video name, even for hash-named Data/r replays.</summary>
public static class ReplayVideoFileName
{
    public static string Create(string beatmapPath, string replayPath)
    {
        var fallback = Path.GetFileNameWithoutExtension(replayPath);
        if (!string.Equals(Path.GetExtension(replayPath), ".osr", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(beatmapPath), ".osu", StringComparison.OrdinalIgnoreCase))
            return SafeFileName(fallback);

        try
        {
            using var reader = OsuBinaryReader.OpenFile(replayPath);
            var replay = OsuReplayParser.ParseHeader(reader);
            if (string.IsNullOrWhiteSpace(replay.Username)
                || !Enum.IsDefined(replay.Mode)
                || replay.TimestampTicks <= 0 || replay.TimestampTicks > DateTime.MaxValue.Ticks)
                return SafeFileName(fallback);

            var metadata = ReadMetadata(beatmapPath);
            if (!metadata.TryGetValue("Artist", out var artist) || string.IsNullOrWhiteSpace(artist)
                || !metadata.TryGetValue("Title", out var title) || string.IsNullOrWhiteSpace(title)
                || !metadata.TryGetValue("Version", out var difficulty) || string.IsNullOrWhiteSpace(difficulty))
                return SafeFileName(fallback);

            var date = new DateTime(replay.TimestampTicks, DateTimeKind.Utc).ToLocalTime()
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return SafeFileName($"{replay.Username} - {artist} - {title} [{difficulty}] ({date}) {replay.Mode}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Optional naming metadata must not prevent rendering an otherwise supported replay.
            return SafeFileName(fallback);
        }
    }

    private static Dictionary<string, string> ReadMetadata(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var inMetadata = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                if (inMetadata) break;
                inMetadata = line == "[Metadata]";
            }
            else if (inMetadata && !line.StartsWith("//", StringComparison.Ordinal))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                    result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
        return result;
    }

    private static string SafeFileName(string stem)
    {
        // Use Windows rules explicitly so names remain portable when tests run on other platforms.
        var safe = new string(stem.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "replay";
        var device = safe.Split('.')[0];
        if (device.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || device.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || device.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || device.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (device.Length == 4
                && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && device[3] is >= '1' and <= '9'))
            safe = "_" + safe;

        // Leave room for the extension and distinguish long names sharing the same prefix.
        if (safe.Length > 236)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stem)))[..10];
            var length = char.IsHighSurrogate(safe[224]) ? 224 : 225;
            safe = safe[..length].TrimEnd(' ', '.') + "-" + hash;
        }
        return safe + ".mp4";
    }
}

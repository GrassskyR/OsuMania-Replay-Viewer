// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Core;

/// <summary>
/// Port of the helper functions in <c>Utils.kt</c>. The resource bundle of the original is
/// replaced by an injectable log sink so the Core stays UI-agnostic.
/// </summary>
public static class Utils
{
    private static readonly string[] MusicExtensions = { "mp3", "ogg", "wav" };

    /// <summary>
    /// Port of <c>adjust()</c>.
    ///
    /// Order matters: rate scaling is applied to both the replay and the beatmap before
    /// mirroring, and mirroring only touches beatmap notes (never replay data) because the
    /// replay's column mask is derived from the pre-mirror layout.
    /// </summary>
    public static void Adjust(BeatMap beatMap, ReplayModel replayModel, Action<string, object?[]>? log = null)
    {
        if (replayModel.Rate != 1.0)
        {
            log?.Invoke("rate.scale", new object?[] { replayModel.Rate });
            foreach (var note in replayModel.ReplayData)
            {
                note.Scale(replayModel.Rate);
            }

            foreach (var note in beatMap.Notes)
            {
                note.Scale(replayModel.Rate);
            }
        }

        if (replayModel.Mirror)
        {
            log?.Invoke("adjust.mirror", Array.Empty<object?>());
            foreach (var note in beatMap.Notes)
            {
                note.Column = beatMap.Key - note.Column - 1;
            }
        }
    }

    /// <summary>Port of <c>getMD5()</c>: lowercase hex MD5 of the whole file.</summary>
    public static string? GetMd5(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var md5 = System.Security.Cryptography.MD5.Create();
            var digest = md5.ComputeHash(stream);
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Port of <c>unzip()</c>; directory entries are created implicitly by the extractor.</summary>
    public static void Unzip(string zipFilePath, string destinationDirectory)
    {
        System.IO.Compression.ZipFile.ExtractToDirectory(zipFilePath, destinationDirectory, overwriteFiles: true);
    }

    public static bool IsMusicExtension(string extension)
        => MusicExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
}

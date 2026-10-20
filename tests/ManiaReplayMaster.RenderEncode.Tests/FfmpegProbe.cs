using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ManiaReplayMaster.RenderEncode.Tests;

/// <summary>
/// Locates <c>ffmpeg</c>/<c>ffprobe</c> and inspects a produced media file, so the end-to-end tests
/// can verify real streams instead of only asserting that a file appeared.
///
/// Resolution order: <c>MRM_FFMPEG</c> environment variable, then <c>C:\ffmpeg\ffmpeg.exe</c>,
/// then plain <c>ffmpeg</c> from <c>PATH</c>. <c>ffprobe</c> is expected next to it.
/// </summary>
internal static class FfmpegProbe
{
    public static string FixtureFfmpeg => Environment.GetEnvironmentVariable("MRM_FIXTURE_FFMPEG") ?? Resolve().Ffmpeg;
    public static (string Ffmpeg, string Ffprobe) Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("MRM_FFMPEG");
        var ffmpeg = !string.IsNullOrWhiteSpace(configured)
            ? configured!
            : File.Exists(@"C:\ffmpeg\ffmpeg.exe")
                ? @"C:\ffmpeg\ffmpeg.exe"
                : "ffmpeg";

        var ffprobe = Environment.GetEnvironmentVariable("MRM_FFPROBE")
            ?? Path.Combine(Path.GetDirectoryName(ffmpeg) ?? ".", "ffprobe.exe");
        if (!File.Exists(ffprobe))
        {
            ffprobe = "ffprobe";
        }

        return (ffmpeg, ffprobe);
    }

    public static MediaInfo Inspect(string ffprobePath, string mediaPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add(mediaPath);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("failed to start ffprobe");
        var json = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe failed ({process.ExitCode}): {errors}");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var formatDuration = root.TryGetProperty("format", out var format)
                             && format.TryGetProperty("duration", out var durationElement)
                             && double.TryParse(durationElement.GetString(), NumberStyles.Float,
                                 CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0d;

        var streams = new List<MediaStream>();
        if (root.TryGetProperty("streams", out var streamArray))
        {
            foreach (var stream in streamArray.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var typeElement)
                    ? typeElement.GetString() ?? string.Empty
                    : string.Empty;
                var codec = stream.TryGetProperty("codec_name", out var codecElement)
                    ? codecElement.GetString() ?? string.Empty
                    : string.Empty;
                var streamDuration = stream.TryGetProperty("duration", out var streamDurationElement)
                                     && double.TryParse(streamDurationElement.GetString(), NumberStyles.Float,
                                         CultureInfo.InvariantCulture, out var streamParsed)
                    ? streamParsed
                    : formatDuration;
                var width = stream.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : 0;
                var height = stream.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : 0;

                streams.Add(new MediaStream(type, codec, streamDuration, width, height));
            }
        }

        return new MediaInfo(formatDuration, streams);
    }

    public static void Run(string ffmpegPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("failed to start ffmpeg");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg failed ({process.ExitCode}) for {string.Join(' ', arguments)}:{Environment.NewLine}" +
                stderr + stdout);
        }
    }
}

internal sealed record MediaStream(string CodecType, string CodecName, double DurationSeconds, int Width, int Height);

internal sealed class MediaInfo
{
    public MediaInfo(double durationSeconds, List<MediaStream> streams)
    {
        DurationSeconds = durationSeconds;
        Streams = streams;
    }

    public double DurationSeconds { get; }

    public List<MediaStream> Streams { get; }

    public IEnumerable<MediaStream> VideoStreams => Streams.Where(s => s.CodecType == "video");

    public IEnumerable<MediaStream> AudioStreams => Streams.Where(s => s.CodecType == "audio");
}

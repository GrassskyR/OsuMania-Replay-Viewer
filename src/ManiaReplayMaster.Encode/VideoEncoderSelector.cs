using System.ComponentModel;
using System.Diagnostics;
using ManiaReplayMaster.Encode.Diagnostics;

namespace ManiaReplayMaster.Encode;

public sealed record EncoderSelection(string Codec, string Preset, bool IsFallback = false)
{
    public bool IsHardwareAccelerated => Codec is "h264_nvenc" or "h264_qsv" or "h264_amf";
}

/// <summary>Tests actual encoder initialization; an FFmpeg encoder listing alone cannot verify a GPU or driver.</summary>
public static class VideoEncoderSelector
{
    public static bool IsAutomatic(string codec) => string.Equals(codec.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

    public static string DefaultPreset(string codec) => codec switch
    {
        "h264_nvenc" => "p1",
        "h264_qsv" => "veryfast",
        "h264_amf" => "speed",
        _ => "ultrafast"
    };

    public static EncoderSelection Select(string codec, string preset, Func<EncoderSelection, bool> probe)
    {
        if (!IsAutomatic(codec))
        {
            codec = codec.Trim();
            return new EncoderSelection(codec,
                string.IsNullOrWhiteSpace(preset) || IsAutomatic(preset) ? DefaultPreset(codec) : preset.Trim());
        }

        // Auto mode owns the preset as well: CPU presets such as ultrafast are invalid for NVENC/AMF.
        foreach (var candidate in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            var selection = new EncoderSelection(candidate, DefaultPreset(candidate));
            if (probe(selection)) return selection;
        }

        return new EncoderSelection("libx264", "ultrafast", IsFallback: true);
    }

    public static EncoderSelection Detect(string ffmpegPath, int width, int height, int frameRate,
        string codec, string preset, PortLogger log, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Select(codec, preset, selection => Probe(ffmpegPath, width, height, frameRate,
            selection, log, cancellationToken));
    }

    private static bool Probe(string ffmpegPath, int width, int height, int frameRate,
        EncoderSelection selection, PortLogger log, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i",
            FormattableString.Invariant($"color=black:s={width}x{height}:r={frameRate}"),
            "-frames:v", "3", "-an"
        };
        FfmpegCommandBuilder.AppendVideoEncoderOptions(arguments, selection.Codec, selection.Preset, "unknown");
        arguments.AddRange(new[] { "-f", "null", "-" });
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                process.WaitForExit();
                Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
                cancellationToken.ThrowIfCancellationRequested();
                log.Debug($"Encoder probe timed out: {selection.Codec}");
                return false;
            }

            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            log.Debug($"Encoder probe {selection.Codec}: exit {process.ExitCode}. {stderr.Result.Trim()}");
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            log.Debug($"Encoder probe {selection.Codec}: {ex.Message}");
            return false;
        }
    }
}

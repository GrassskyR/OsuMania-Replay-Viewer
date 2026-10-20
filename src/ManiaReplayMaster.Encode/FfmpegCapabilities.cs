using System.Diagnostics;

namespace ManiaReplayMaster.Encode;

/// <summary>Reads capabilities from the selected executable, with bounded and cancellable processes.</summary>
public static class FfmpegCapabilities
{
    public static IReadOnlySet<string> ParseEncoders(string listing)
    {
        var encoders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in listing.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && fields[0].Length == 6 && fields[0][0] is 'V' or 'A'
                && fields[1] != "=")
                encoders.Add(fields[1]);
        }
        return encoders;
    }

    public static IReadOnlySet<string> ReadEncoders(string executable, CancellationToken cancellationToken = default)
        => ParseEncoders(Run(executable, ["-hide_banner", "-encoders"], cancellationToken));

    public static string Validate(string executable, CancellationToken cancellationToken = default)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("FFmpeg executable not found.", executable);
        var version = Run(executable, ["-version"], cancellationToken).Split('\n')[0].Trim();
        if (!version.StartsWith("ffmpeg version ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected executable is not FFmpeg.");
        var encoders = ReadEncoders(executable, cancellationToken);
        foreach (var required in new[] { "libx264", "aac", "libmp3lame" })
            if (!encoders.Contains(required)) throw new InvalidOperationException($"FFmpeg is missing encoder: {required}");
        var filters = Run(executable, ["-hide_banner", "-filters"], cancellationToken)
            .Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 2).Select(fields => fields[1]).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[] { "format", "loop", "setpts", "crop", "trim", "transpose", "concat", "scale",
                     "atempo", "adelay", "anullsrc", "aresample", "aformat", "color" })
            if (!filters.Contains(required)) throw new InvalidOperationException($"FFmpeg is missing filter: {required}");

        var output = Path.Combine(Path.GetTempPath(), "mrm-ffmpeg-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            // Keep the product's default AAC selection and audio parameters.
            Run(executable, ["-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i",
                "color=black:s=64x64:r=30", "-f", "lavfi", "-i", "anullsrc", "-t", "0.1",
                "-c:v", "libx264", "-preset:v", "ultrafast", "-pix_fmt", "yuv420p", "-y", output], cancellationToken);
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new InvalidOperationException("FFmpeg did not produce a video.");
        }
        finally { if (File.Exists(output)) File.Delete(output); }
        return version;
    }

    private static string Run(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start FFmpeg.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit();
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("FFmpeg did not respond within 15 seconds.");
        }
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException($"FFmpeg failed ({process.ExitCode}): {stderr.Result.Trim()}");
        return stdout.Result;
    }
}

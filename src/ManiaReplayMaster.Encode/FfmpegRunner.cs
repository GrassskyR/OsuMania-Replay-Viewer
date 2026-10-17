// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Encode.Diagnostics;

namespace ManiaReplayMaster.Encode;

/// <summary>
/// Runs one ffmpeg invocation and turns its output into progress, mirroring
/// <c>Encoder.runFFMPEG</c>.
///
/// Reference behaviour kept as-is:
/// <list type="bullet">
/// <item>stderr is merged into stdout (<c>redirectErrorStream(true)</c>) -- here both pipes are
/// drained into the same handler; ffmpeg writes its progress to stderr, which is why the original
/// had to merge them;</item>
/// <item>progress is parsed with <c>time=(\d+:\d+:\d+\.\d+)\s</c> and scaled as
/// <c>time / expectedDuration * 100</c>; an <c>expectedDuration</c> of exactly 0 disables parsing
/// (that is the mute-sound pass);</item>
/// <item>100% is reported for the stage when the process exits, if parsing was enabled;</item>
/// <item>a missing output file throws <see cref="VideoGenerationException"/> (error code
/// <c>render_failed</c>).</item>
/// </list>
///
/// Added (the reference could not be cancelled): a <see cref="CancellationToken"/> kills the
/// ffmpeg process tree, which unblocks the output pumps, and the call then throws
/// <see cref="OperationCanceledException"/>.
/// </summary>
public static class FfmpegRunner
{
    private static readonly Regex TimePattern = new(@"time=(\d+:\d+:\d+\.\d+)\s", RegexOptions.Compiled);

    public static void Run(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        string expectedOutputPath,
        double expectedDuration,
        string stage,
        int startProgress,
        int endProgress,
        ProgressReporter reporter,
        PortLogger log,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // The reference never writes to the child's stdin but leaves the pipe open. We close it
            // instead of inheriting the console's stdin so a stray "File exists. Overwrite?" prompt
            // can never block a GUI process; the output file is deleted before every pass anyway.
            RedirectStandardInput = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child may have exited before we got here; nothing to do.
        }

        void HandleLine(string line)
        {
            log.Debug("[FFMPEG] " + line);
            if (expectedDuration == 0.0)
            {
                return;
            }

            var match = TimePattern.Match(line);
            if (!match.Success)
            {
                return;
            }

            var timeRead = ParseTime(match.Groups[1].Value);
            reporter.Report(stage, startProgress, endProgress, timeRead / expectedDuration * 100);
        }

        var stdoutPump = Task.Run(() => Pump(process.StandardOutput, HandleLine));
        var stderrPump = Task.Run(() => Pump(process.StandardError, HandleLine));

        using (cancellationToken.Register(() => TryKill(process)))
        {
            process.WaitForExit();
        }

        Task.WaitAll(stdoutPump, stderrPump);

        if (expectedDuration != 0.0)
        {
            reporter.Report(stage, startProgress, endProgress, 100.0);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                $"ffmpeg was cancelled during stage '{stage}'.", cancellationToken);
        }

        if (!File.Exists(expectedOutputPath))
        {
            throw new VideoGenerationException(new Exception(expectedOutputPath));
        }
    }

    /// <summary>
    /// Port of the reference's <c>parseTime</c>: <c>"h:m:s.frac"</c> -&gt; seconds, returning 0 when
    /// the value cannot be parsed (the original wrapped the whole thing in a try/catch).
    /// </summary>
    private static double ParseTime(string value)
    {
        var pieces = value.Split(':');
        if (pieces.Length < 3)
        {
            return 0d;
        }

        if (!double.TryParse(pieces[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
            || !double.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(pieces[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return 0d;
        }

        return hours * 60 * 60 + minutes * 60 + seconds;
    }

    private static void Pump(StreamReader reader, Action<string> onLine)
    {
        while (reader.ReadLine() is { } line)
        {
            onLine(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The process may have exited between the check and the kill.
        }
    }
}

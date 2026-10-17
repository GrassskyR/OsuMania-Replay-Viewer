namespace ManiaReplayMaster.Encode;

/// <summary>
/// Port of the ffmpeg discovery in <c>ReplayMaster.generateVideo</c>.
///
/// The reference does:
/// <code>
/// var ffmpegPath = File("ffmpeg.exe").absolutePath   // i.e. next to the working directory
/// if (!File(ffmpegPath).exists()) ffmpegPath = "ffmpeg"   // i.e. resolved from PATH
/// </code>
///
/// The port keeps that behaviour and additionally prefers an <c>ffmpeg.exe</c> that ships next to
/// the application executable, which is the layout the packaged app uses. An explicit path always
/// wins (the GUI exposes it as a setting).
/// </summary>
public static class FfmpegLocator
{
    public const string ExecutableName = "ffmpeg.exe";

    /// <summary>
    /// Resolution order: explicit path, then <c>ffmpeg.exe</c> next to the application executable,
    /// then <c>ffmpeg.exe</c> in the current working directory (reference behaviour), then
    /// <c>ffmpeg</c> on <c>PATH</c>, then the conventional Windows install locations.
    /// </summary>
    public static string Locate(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDirectory))
        {
            var besideExecutable = Path.Combine(baseDirectory, ExecutableName);
            if (File.Exists(besideExecutable))
            {
                return besideExecutable;
            }
        }

        var workingDirectory = Path.GetFullPath(ExecutableName);
        if (File.Exists(workingDirectory))
        {
            return workingDirectory;
        }

        // A bare "ffmpeg" only works when it really is on PATH, so verify before promising it.
        var onPath = FindOnPath();
        if (onPath is not null)
        {
            return onPath;
        }

        foreach (var candidate in WellKnownLocations())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Fall back to the reference behaviour so the caller's own error stays meaningful.
        return "ffmpeg";
    }

    /// <summary>Looks up <c>ffmpeg.exe</c> on PATH without relying on shell resolution.</summary>
    private static string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(entry.Trim().Trim('"'), ExecutableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entries are skipped rather than fatal.
            }
        }

        return null;
    }

    /// <summary>Conventional locations used by the common Windows ffmpeg distributions.</summary>
    private static IEnumerable<string> WellKnownLocations()
    {
        yield return @"C:\ffmpeg\ffmpeg.exe";

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            yield return Path.Combine(programFiles, "ffmpeg", "bin", ExecutableName);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", ExecutableName);
        }
    }
}

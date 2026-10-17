using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ManiaReplayMaster.Game;

/// <summary>Where the osu! root directory came from, so the UI can show and explain the choice.</summary>
public enum OsuRootSource
{
    Manual,
    RunningProcess,
    LocalAppData,
    Registry,
    ConfigFile,
    NotFound
}

/// <summary>
/// Locates the osu!stable installation and the directories that matter for the game-link feature
/// (plan §5.1). Resolution order: explicit override → running <c>osu!.exe</c> process image path →
/// <c>%LOCALAPPDATA%\osu!</c> → registry → <c>osu!*.cfg</c> next to a known executable.
///
/// The <c>BeatmapDirectory</c> key in <c>osu!&lt;user&gt;.cfg</c> is respected (osu! lets the user move
/// <c>Songs</c> elsewhere). There is deliberately no <c>ReplayDirectory</c> key: osu!stable has none,
/// and <c>Data\r</c> always lives directly under the root.
/// </summary>
public sealed class GameLocator
{
    public const string ProcessName = "osu!";

    private GameLocator()
    {
    }

    /// <summary>Absolute path of the osu! root, or <c>null</c> when nothing was found.</summary>
    public string? Root { get; private init; }

    public OsuRootSource Source { get; private init; } = OsuRootSource.NotFound;

    /// <summary>Human-readable trace of the resolution attempt (one line per candidate).</summary>
    public IReadOnlyList<string> Trace { get; private init; } = Array.Empty<string>();

    /// <summary><c>Songs</c> directory, honouring <c>BeatmapDirectory</c> from the cfg when present.</summary>
    public string? SongsDirectory { get; private init; }

    /// <summary>True when <c>BeatmapDirectory</c> pointed outside the osu! root.</summary>
    public bool SongsDirectoryIsCustom { get; private init; }

    /// <summary>F2-exported replays (<c>Replays\</c>).</summary>
    public string? ReplaysDirectory { get; private init; }

    /// <summary>Auto-saved, obfuscated-name replays (<c>Data\r\</c>) — the primary replay store.</summary>
    public string? DataRDirectory { get; private init; }

    public string? OsuDbPath { get; private init; }

    public string? ScoresDbPath { get; private init; }

    public string? CollectionDbPath { get; private init; }

    public string? OsuExePath { get; private init; }

    /// <summary>The player name from <c>osu!&lt;user&gt;.cfg</c> / <c>osu!.db</c>, when known.</summary>
    public string? ConfigPlayerName { get; private init; }

    public bool Found => Root is not null;

    public static GameLocator Resolve(string? manualRoot = null)
    {
        var trace = new List<string>();

        foreach (var (candidate, source) in EnumerateCandidates(manualRoot, trace))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string? normalized;
            try
            {
                normalized = Path.GetFullPath(candidate.Trim().Trim('"'));
            }
            catch (Exception ex)
            {
                trace.Add($"{source}: '{candidate}' is not a valid path ({ex.GetType().Name}).");
                continue;
            }

            if (!Directory.Exists(normalized))
            {
                trace.Add($"{source}: '{normalized}' does not exist.");
                continue;
            }

            if (!LooksLikeOsuRoot(normalized, out var reason))
            {
                trace.Add($"{source}: '{normalized}' rejected — {reason}.");
                continue;
            }

            trace.Add($"{source}: accepted '{normalized}'.");
            return Build(normalized, source, trace);
        }

        trace.Add("No osu! installation found. Use the manual override to point at the osu! folder.");
        return new GameLocator { Source = OsuRootSource.NotFound, Trace = trace };
    }

    /// <summary>
    /// Builds a locator for a known root without probing (used by tests and by the harness, which
    /// must be able to point at a fixture tree).
    /// </summary>
    public static GameLocator FromRoot(string root, OsuRootSource source = OsuRootSource.Manual)
        => Build(Path.GetFullPath(root), source, new List<string> { $"{source}: explicit root '{Path.GetFullPath(root)}'." });

    public string? ResolveReplayFile(string fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return null;
        }

        if (Path.IsPathRooted(fileNameOrPath))
        {
            return File.Exists(fileNameOrPath) ? fileNameOrPath : null;
        }

        foreach (var dir in new[] { DataRDirectory, ReplaysDirectory })
        {
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, fileNameOrPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<(string? Path, OsuRootSource Source)> EnumerateCandidates(
        string? manualRoot, List<string> trace)
    {
        if (!string.IsNullOrWhiteSpace(manualRoot))
        {
            yield return (manualRoot, OsuRootSource.Manual);
        }

        var running = FindRunningOsuRoot(trace);
        if (running is not null)
        {
            yield return (running, OsuRootSource.RunningProcess);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return (Path.Combine(localAppData, "osu!"), OsuRootSource.LocalAppData);
        }

        foreach (var reg in EnumerateRegistryCandidates(trace))
        {
            yield return (reg, OsuRootSource.Registry);
        }

        foreach (var cfg in EnumerateCfgCandidates(trace))
        {
            yield return (cfg, OsuRootSource.ConfigFile);
        }
    }

    /// <summary>
    /// Reads the image path of a running <c>osu!.exe</c>. Reading a process path requires only
    /// query access; nothing is opened for writing and no memory is touched here.
    /// </summary>
    private static string? FindRunningOsuRoot(List<string> trace)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(ProcessName))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path))
                        {
                            var root = Path.GetDirectoryName(path);
                            trace.Add($"RunningProcess: osu!.exe (pid {process.Id}) -> '{root}'.");
                            return root;
                        }
                    }
                    catch (Exception ex)
                    {
                        trace.Add($"RunningProcess: pid {process.Id} image path unreadable ({ex.GetType().Name}).");
                    }
                }
            }

            trace.Add("RunningProcess: no osu! process found.");
        }
        catch (Exception ex)
        {
            trace.Add($"RunningProcess: enumeration failed ({ex.GetType().Name}).");
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> EnumerateRegistryCandidates(List<string> trace)
    {
        // osu!stable does not write an "install path" key by design; the uninstall entry is the only
        // place a path may appear (e.g. after an installer-based setup), so it is checked last and
        // treated as a hint only.
        var keys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\osu!",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\osu!"
        };

        foreach (var keyName in keys)
        {
            string? value = null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyName);
                value = key?.GetValue("InstallLocation") as string
                        ?? key?.GetValue("DisplayIcon") as string;
            }
            catch (Exception ex)
            {
                trace.Add($"Registry: {keyName} unreadable ({ex.GetType().Name}).");
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var cleaned = value.Trim('"');
            if (cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = Path.GetDirectoryName(cleaned) ?? cleaned;
            }

            trace.Add($"Registry: {keyName} -> '{cleaned}'.");
            yield return cleaned;
        }
    }

    /// <summary>
    /// Fallback: look for an <c>osu!*.cfg</c> beside any plausible osu! directory found on the usual
    /// drives. Cheap (top-level enumeration only) and only used when everything else failed.
    /// </summary>
    private static IEnumerable<string> EnumerateCfgCandidates(List<string> trace)
    {
        var probes = new List<string>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            probes.Add(Path.Combine(drive.RootDirectory.FullName, "osu!"));
            probes.Add(Path.Combine(drive.RootDirectory.FullName, "Games", "osu!"));
            probes.Add(Path.Combine(drive.RootDirectory.FullName, "Game", "osu"));
        }

        foreach (var probe in probes)
        {
            bool exists;
            try
            {
                exists = Directory.Exists(probe) && File.Exists(Path.Combine(probe, "osu!.exe"));
            }
            catch
            {
                continue;
            }

            if (exists)
            {
                trace.Add($"ConfigFile: probed '{probe}'.");
                yield return probe;
            }
        }
    }

    /// <summary>
    /// A directory is an osu! root when it carries at least one of the decisive artifacts.
    /// <c>osu!.exe</c> alone is accepted so that a partially copied install is still usable.
    /// </summary>
    private static bool LooksLikeOsuRoot(string root, out string reason)
    {
        if (File.Exists(Path.Combine(root, "osu!.exe")))
        {
            reason = "osu!.exe present";
            return true;
        }

        if (File.Exists(Path.Combine(root, "osu!.db")) || File.Exists(Path.Combine(root, "scores.db")))
        {
            reason = "osu!.db/scores.db present";
            return true;
        }

        if (Directory.Exists(Path.Combine(root, "Songs")) && Directory.Exists(Path.Combine(root, "Data", "r")))
        {
            reason = "Songs\\ and Data\\r\\ present";
            return true;
        }

        reason = "no osu!.exe, osu!.db, scores.db, or Songs\\+Data\\r\\";
        return false;
    }

    private static GameLocator Build(string root, OsuRootSource source, List<string> trace)
    {
        var songs = Path.Combine(root, "Songs");
        var custom = false;
        string? player = null;

        var (cfgSongs, cfgPlayer, cfgTrace) = ReadUserConfig(root);
        foreach (var line in cfgTrace)
        {
            trace.Add(line);
        }

        if (!string.IsNullOrWhiteSpace(cfgSongs))
        {
            var expanded = Environment.ExpandEnvironmentVariables(cfgSongs.Trim().Trim('"'));
            try
            {
                var full = Path.GetFullPath(expanded);
                if (Directory.Exists(full))
                {
                    songs = full;
                    custom = !string.Equals(
                        full.TrimEnd(Path.DirectorySeparatorChar),
                        Path.Combine(root, "Songs").TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);
                    trace.Add($"cfg: BeatmapDirectory -> '{full}'.");
                }
                else
                {
                    trace.Add($"cfg: BeatmapDirectory '{full}' does not exist; using '{songs}'.");
                }
            }
            catch (Exception ex)
            {
                trace.Add($"cfg: BeatmapDirectory '{cfgSongs}' unusable ({ex.GetType().Name}).");
            }
        }

        player = cfgPlayer;

        var exe = Path.Combine(root, "osu!.exe");
        return new GameLocator
        {
            Root = root,
            Source = source,
            Trace = trace,
            SongsDirectory = songs,
            SongsDirectoryIsCustom = custom,
            ReplaysDirectory = Path.Combine(root, "Replays"),
            DataRDirectory = Path.Combine(root, "Data", "r"),
            OsuDbPath = Path.Combine(root, "osu!.db"),
            ScoresDbPath = Path.Combine(root, "scores.db"),
            CollectionDbPath = Path.Combine(root, "collection.db"),
            OsuExePath = File.Exists(exe) ? exe : null,
            ConfigPlayerName = player
        };
    }

    /// <summary>
    /// Parses <c>BeatmapDirectory</c> and <c>Username</c> out of <c>osu!&lt;user&gt;.cfg</c>, which is a
    /// plain <c>Key = Value</c> text file. The generic <c>osu!.cfg</c> holds DLL hashes and is skipped.
    /// </summary>
    private static (string? Songs, string? Player, List<string> Trace) ReadUserConfig(string root)
    {
        var trace = new List<string>();
        string? songs = null;
        string? player = null;

        string[] files;
        try
        {
            files = Directory.GetFiles(root, "osu!*.cfg", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            trace.Add($"cfg: enumeration failed ({ex.GetType().Name}).");
            return (null, null, trace);
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.Equals("osu!.cfg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var sep = line.IndexOf('=');
                    if (sep <= 0)
                    {
                        continue;
                    }

                    var key = line[..sep].Trim();
                    var value = line[(sep + 1)..].Trim();
                    if (key.Equals("BeatmapDirectory", StringComparison.OrdinalIgnoreCase))
                    {
                        songs ??= value;
                    }
                    else if (key.Equals("Username", StringComparison.OrdinalIgnoreCase))
                    {
                        player ??= value;
                    }
                }

                trace.Add($"cfg: read '{name}'.");
            }
            catch (Exception ex)
            {
                trace.Add($"cfg: '{name}' unreadable ({ex.GetType().Name}).");
            }
        }

        return (songs, player, trace);
    }
}

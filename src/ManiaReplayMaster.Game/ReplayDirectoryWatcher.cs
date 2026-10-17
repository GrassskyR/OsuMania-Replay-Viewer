namespace ManiaReplayMaster.Game;

/// <summary>
/// Level-2 of the replay strategy (plan §3.3): watch <c>Data\r</c> and <c>Replays\</c> for newly
/// written <c>.osr</c> files so the "just played a map" case works even when memory reading is
/// completely unavailable.
///
/// <c>Data\r</c> receives a <c>.osr</c> plus a same-named <c>.osg</c> per passed solo play, and osu!
/// writes them under obfuscated <c>&lt;beatmapMd5&gt;-&lt;FILETIME&gt;</c> names — the same rule
/// <see cref="ScoreRecord.ReplayFileName"/> uses, so a watch hit can be turned into a score match
/// without any memory access at all.
///
/// Read-only with respect to the game: <see cref="FileSystemWatcher"/> never modifies the watched
/// directory, and the notification filters are limited to <c>*.osr</c>.
/// </summary>
public sealed class ReplayDirectoryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _gate = new();
    private readonly HashSet<string> _recent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised for every newly observed <c>.osr</c>. The handler must not throw.</summary>
    public event Action<OsuReplayHeaderResult, string>? ReplayAdded;

    public bool IsRunning { get; private set; }

    /// <summary>Directories actually being watched (missing ones are skipped, not an error).</summary>
    public IReadOnlyList<string> WatchedDirectories { get; private set; } = Array.Empty<string>();

    /// <summary>Number of files observed since <see cref="Start"/>.</summary>
    public int ObservedCount { get; private set; }

    /// <summary>Raised when a watched directory disappears or the watcher faults; the watcher keeps going.</summary>
    public event Action<string>? WatcherError;

    /// <summary>Starts watching the given directories. Safe to call twice.</summary>
    public void Start(IEnumerable<string?> directories)
    {
        Stop();

        var watched = new List<string>();
        foreach (var dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(dir, "*.osr")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Created += (_, e) => OnFile(e.FullPath);
                watcher.Changed += (_, e) => OnFile(e.FullPath);
                watcher.Renamed += (_, e) => OnFile(e.FullPath);
                watcher.Error += (_, e) => WatcherError?.Invoke($"{dir}: {e.GetException().Message}");
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                watched.Add(dir);
            }
            catch (Exception ex)
            {
                WatcherError?.Invoke($"{dir}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        WatchedDirectories = watched;
        IsRunning = _watchers.Count > 0;
    }

    public void Stop()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // Disposing a watcher must never throw into the UI path.
            }
        }

        _watchers.Clear();
        WatchedDirectories = Array.Empty<string>();
        IsRunning = false;
    }

    /// <summary>Resolves one observed path into a header. Called on a watcher thread; must not block.</summary>
    private void OnFile(string path)
    {
        lock (_gate)
        {
            if (!_recent.Add(path))
            {
                return;
            }

            if (_recent.Count > 4096)
            {
                _recent.Clear();
                _recent.Add(path);
            }

            ObservedCount++;
        }

        // osu! may still be flushing the file; retry briefly before giving up.
        var result = ReadWithRetry(path, attempts: 5, delayMs: 120);
        ReplayAdded?.Invoke(result, path);
    }

    private static OsuReplayHeaderResult ReadWithRetry(string path, int attempts, int delayMs)
    {
        OsuReplayHeaderResult result = OsuReplayHeaderResult.Failed("not attempted");
        for (var i = 0; i < attempts; i++)
        {
            result = OsuReplayHeaderReader.TryParse(path);
            if (result.Success)
            {
                return result;
            }

            Thread.Sleep(delayMs);
        }

        return result;
    }

    /// <summary>
    /// One-shot variant of the same logic for callers that are handed a path instead of an event
    /// (e.g. "the user just pressed F2"): returns the header plus, when the name follows the
    /// <c>Data\r</c> convention, the beatmap MD5 and play timestamp embedded in it.
    /// </summary>
    public static OsuReplayHeaderResult ResolveExisting(string path, out string? beatmapMd5, out long? filetime)
    {
        beatmapMd5 = null;
        filetime = null;

        var result = ReadWithRetry(path, attempts: 3, delayMs: 100);
        if (OsuReplayHeaderReader.IsObfuscatedReplayName(Path.GetFileName(path), out var md5, out var ft))
        {
            beatmapMd5 = md5;
            filetime = ft;
        }

        return result;
    }

    public void Dispose() => Stop();
}

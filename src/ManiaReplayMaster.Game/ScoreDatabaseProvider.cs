using System.Diagnostics;

namespace ManiaReplayMaster.Game;

/// <summary>
/// Lazily loads and caches <c>scores.db</c>, which is the join table between "the score the player is
/// looking at" and "the replay file on disk" (plan §3.3 level 1).
///
/// Behaviour that the rest of the subsystem depends on:
/// <list type="bullet">
/// <item>the file is re-read whenever its size or last-write time changes, because osu! rewrites it
/// after every play;</item>
/// <item>a parse failure (torn write, unknown build) is reported through <see cref="LastResult"/>
/// instead of throwing, so the caller degrades to the level-3 header scan instead of showing an
/// error the user cannot act on;</item>
/// <item>lookups by beatmap MD5 are O(1).</item>
/// </list>
/// </summary>
public sealed class ScoreDatabaseProvider
{
    private readonly object _gate = new();
    private ScoreDatabaseResult? _result;
    private long _loadedSize = -1;
    private long _loadedTicks = -1;

    public ScoreDatabaseProvider(GameLocator locator, ScoreDatabaseOptions? options = null)
    {
        Locator = locator;
        Options = options ?? ScoreDatabaseOptions.Default;
    }

    public GameLocator Locator { get; }

    public ScoreDatabaseOptions Options { get; }

    /// <summary>Result of the most recent read attempt; <c>null</c> before the first read.</summary>
    public ScoreDatabaseResult? LastResult
    {
        get
        {
            lock (_gate)
            {
                return _result;
            }
        }
    }

    public bool IsAvailable => LastResult?.Success == true;

    public int GroupCount => LastResult?.ParsedGroupCount ?? 0;

    public int RecordCount => LastResult?.ScoreRecordCount ?? 0;

    /// <summary>True when the last attempt failed; the message is in <see cref="LastResult"/>.</summary>
    public bool HasError => LastResult is not null && !LastResult.Success;

    public string? Error => LastResult?.Error;

    /// <summary>Total time spent parsing <c>scores.db</c> so far (a diagnostic for the status bar).</summary>
    public TimeSpan TotalParseTime { get; private set; }

    /// <summary>Loads (or reloads) the database. Never throws; inspect <see cref="LastResult"/>.</summary>
    public ScoreDatabaseResult? Load(bool force = false)
    {
        var path = Locator.ScoresDbPath;
        if (string.IsNullOrEmpty(path))
        {
            lock (_gate)
            {
                _result = new ScoreDatabaseResult
                {
                    Path = string.Empty,
                    FileLength = 0,
                    Success = false,
                    Error = "no osu! root resolved, so scores.db is unknown"
                };
                return _result;
            }
        }

        long size;
        long ticks;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                lock (_gate)
                {
                    _result = new ScoreDatabaseResult
                    {
                        Path = path,
                        FileLength = 0,
                        Success = false,
                        Error = $"scores.db not found at '{path}'"
                    };
                    return _result;
                }
            }

            size = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _result = new ScoreDatabaseResult
                {
                    Path = path,
                    FileLength = 0,
                    Success = false,
                    Error = $"cannot stat scores.db: {ex.GetType().Name}: {ex.Message}"
                };
                return _result;
            }
        }

        lock (_gate)
        {
            var unchanged = !force && _result is not null && size == _loadedSize && ticks == _loadedTicks;
            if (unchanged)
            {
                return _result;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var fresh = ScoreDatabaseReader.Read(path, Options);
        stopwatch.Stop();

        lock (_gate)
        {
            _result = fresh;
            _loadedSize = size;
            _loadedTicks = ticks;
            TotalParseTime += stopwatch.Elapsed;
            return _result;
        }
    }

    /// <summary>Local scores for a beatmap MD5. Empty when the database is unavailable.</summary>
    public IReadOnlyList<ScoreRecord> ForBeatmap(string? beatmapMd5)
    {
        if (string.IsNullOrEmpty(beatmapMd5))
        {
            return Array.Empty<ScoreRecord>();
        }

        var result = Load();
        return result?.ForBeatmap(beatmapMd5) ?? Array.Empty<ScoreRecord>();
    }

    public override string ToString()
    {
        var result = LastResult;
        return result is null
            ? "ScoreDatabaseProvider(not loaded)"
            : $"ScoreDatabaseProvider({result.Summary})";
    }
}

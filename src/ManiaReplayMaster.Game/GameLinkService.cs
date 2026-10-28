namespace ManiaReplayMaster.Game;

/// <summary>Everything the UI needs after one auto-fill attempt, including the failure explanation.</summary>
public sealed class AutoFillResult
{
    public required string BeatmapMd5 { get; init; }

    public BeatmapLocation? Beatmap { get; init; }

    public ReplayResolution Replay { get; init; } = new() { Candidates = Array.Empty<ReplayCandidate>() };

    /// <summary>Derived parameters the render panel can pre-fill (plan §5.3 step 6).</summary>
    public int? Mods { get; init; }

    public GameMode? Mode { get; init; }

    public int? KeyCount { get; init; }

    public double? Rate { get; init; }

    public bool? Mirror { get; init; }

    public bool? ScoreV2 { get; init; }

    /// <summary>One-line status-bar message, in the style the plan asks for.</summary>
    public string StatusText { get; init; } = string.Empty;

    public bool FullyResolved => Beatmap is not null && Replay.IsConfident;

    /// <summary>The candidate list for the "pick another" dropdown.</summary>
    public IReadOnlyList<ReplayCandidate> Alternatives => Replay.Candidates;

    public override string ToString() => StatusText;
}

/// <summary>
/// Facade over the whole game-link subsystem (plan §5.1), so the UI layer never has to wire the
/// pieces together itself:
/// <c>GameLocator</c> → <see cref="ScoreDatabaseProvider"/> → <see cref="ReplayResolver"/> plus
/// <see cref="BeatmapLocator"/>.
///
/// The design rule throughout: <b>every failure degrades, nothing throws at the UI</b>. With memory
/// unavailable, no <c>scores.db</c>, or no game running at all, the object still answers questions
/// and explains what it could not do.
/// </summary>
public sealed class GameLinkService : IDisposable
{
    public GameLinkService(GameLocator? locator = null, bool startWatcher = true)
    {
        Locator = locator ?? GameLocator.Resolve();
        Scores = new ScoreDatabaseProvider(Locator);
        Replays = new ReplayResolver(Locator, Scores);
        Beatmaps = new BeatmapLocator(Locator);
        Watcher = new ReplayDirectoryWatcher();

        if (startWatcher && Locator.Found)
        {
            Watcher.Start(new[] { Locator.DataRDirectory, Locator.ReplaysDirectory });
        }
    }

    public GameLocator Locator { get; }

    public ScoreDatabaseProvider Scores { get; }

    public ReplayResolver Replays { get; }

    public BeatmapLocator Beatmaps { get; }

    public ReplayDirectoryWatcher Watcher { get; }

    /// <summary>Raised when a new <c>.osr</c> appears; the argument already carries a parsed header.</summary>
    public event Action<OsuReplayHeaderResult, string>? ReplayFileObserved
    {
        add => Watcher.ReplayAdded += value;
        remove => Watcher.ReplayAdded -= value;
    }

    /// <summary>
    /// Runs the plan §5.3 flow for a given beatmap identity: locate the beatmap file, rank the replay
    /// candidates, and derive the render parameters. Safe to call with a snapshot that is almost
    /// empty (e.g. only the MD5 from a manually chosen <c>.osr</c>).
    /// </summary>
    public AutoFillResult AutoFill(GameSnapshot snapshot, bool allowSongsScan = false)
    {
        var md5 = snapshot.BeatmapMd5?.ToLowerInvariant();

        if (md5 is null && (snapshot.BeatmapFolder is null || snapshot.BeatmapOsuFile is null))
        {
            return new AutoFillResult
            {
                BeatmapMd5 = string.Empty,
                StatusText = "没有任何谱面信息可用（内存读取不可用，也无法从回放头得到 MD5）。"
            };
        }

        // Beatmap first: the memory identity path is free, the osu!.db index is one cached read.
        BeatmapLocation? beatmap = null;
        if (!string.IsNullOrEmpty(snapshot.BeatmapFolder) && !string.IsNullOrEmpty(snapshot.BeatmapOsuFile))
        {
            beatmap = Beatmaps.ResolveFromIdentity(snapshot.BeatmapFolder, snapshot.BeatmapOsuFile, md5);
        }

        beatmap ??= md5 is null ? null : Beatmaps.ResolveByMd5(md5, allowSongsScan);

        var resolution = md5 is null
            ? new ReplayResolution
            {
                RequestedMd5 = null,
                Candidates = Array.Empty<ReplayCandidate>(),
                NotFoundReason = "no beatmap MD5, so the replay cannot be matched"
            }
            : Replays.Resolve(snapshot, md5, allowScan: true);

        var best = resolution.Best;
        var header = best?.Header;
        var score = best?.Score;

        var mods = snapshot.Mods != 0 ? snapshot.Mods : header?.Mods ?? score?.Mods;
        var mode = snapshot.Mode != GameMode.Unknown ? snapshot.Mode : header?.Mode ?? score?.Mode;

        var parts = new List<string>();
        if (beatmap is not null)
        {
            parts.Add($"谱面 {beatmap.FileName}");
        }
        else
        {
            parts.Add("谱面未找到");
        }

        if (best is not null)
        {
            parts.Add($"回放 {Path.GetFileName(best.ReplayPath)} ({best.Confidence:P0})");
        }
        else
        {
            parts.Add(resolution.NotFoundReason ?? "回放未找到");
        }

        if (score is not null)
        {
            parts.Add(score.Describe());
        }

        return new AutoFillResult
        {
            BeatmapMd5 = md5 ?? string.Empty,
            Beatmap = beatmap,
            Replay = resolution,
            Mods = mods,
            Mode = mode,
            KeyCount = DeriveKeyCount(mods, score, header),
            Rate = DeriveRate(mods),
            Mirror = mods is null ? null : (mods.Value & (1 << 30)) != 0,
            ScoreV2 = mods is null ? null : (mods.Value & (1 << 29)) != 0,
            StatusText = string.Join("  ·  ", parts)
        };
    }

    /// <summary>
    /// Pre-fills from a manually chosen replay file (plan §2.1, scene A): parse the header, locate the
    /// beatmap by its MD5, and offer the local scores of that beatmap.
    /// </summary>
    public AutoFillResult AutoFillFromReplayFile(string replayPath, bool allowSongsScan = false)
    {
        var parsed = OsuReplayHeaderReader.TryParse(replayPath);
        if (parsed.Header is null)
        {
            return new AutoFillResult
            {
                BeatmapMd5 = string.Empty,
                StatusText = $"无法解析回放文件：{parsed.Error}"
            };
        }

        var header = parsed.Header;
        var snapshot = new GameSnapshot
        {
            BeatmapMd5 = header.BeatmapHash,
            Mods = header.Mods,
            Mode = header.Mode,
            PlayerName = header.Username,
            Score = header.Score,
            MaxCombo = header.MaxCombo,
            HitCounts = header.HitCounts,
            ScoreDate = header.TimestampUtc,
            Status = MemoryReadStatus.NotRunning,
            State = OsuGameState.NotRunning
        };

        var result = AutoFill(snapshot, allowSongsScan);

        // The chosen file is by definition the answer; put it first with full confidence.
        var self = new ReplayCandidate
        {
            ReplayPath = replayPath,
            Source = ReplayMatchSource.Manual,
            Confidence = 1.0,
            RawScore = 1.0,
            Header = header,
            Score = result.Replay.Candidates.FirstOrDefault(c =>
                string.Equals(c.ReplayPath, replayPath, StringComparison.OrdinalIgnoreCase))?.Score,
            Explanation = "user-selected file",
            FileSizeBytes = header.FileSizeBytes,
            FileModified = header.FileModified
        };

        var merged = new[] { self }
            .Concat(result.Replay.Candidates.Where(c =>
                !string.Equals(c.ReplayPath, replayPath, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new AutoFillResult
        {
            BeatmapMd5 = result.BeatmapMd5,
            Beatmap = result.Beatmap,
            Replay = new ReplayResolution
            {
                RequestedMd5 = header.BeatmapHash,
                Candidates = merged,
                ResolvedBy = ReplayMatchSource.Manual,
                Attempts = result.Replay.Attempts,
                Duration = result.Replay.Duration
            },
            Mods = header.Mods,
            Mode = header.Mode,
            KeyCount = DeriveKeyCount(header.Mods, null, header),
            Rate = DeriveRate(header.Mods),
            Mirror = header.HasMirror,
            ScoreV2 = header.HasScoreV2,
            StatusText = result.StatusText
        };
    }

    /// <summary>
    /// Column count for mania. The legacy mod bits 16..19 name the key count when the score was set
    /// with a key mod; otherwise the beatmap's own CircleSize is the key count, so it is read from the
    /// file when the <c>osu!.db</c> index is available.
    /// </summary>
    private int? DeriveKeyCount(int? mods, ScoreRecord? score, OsuReplayHeader? header)
    {
        if (mods is not null)
        {
            var m = mods.Value;
            if ((m & (1 << 16)) != 0) return 4;
            if ((m & (1 << 17)) != 0) return 5;
            if ((m & (1 << 18)) != 0) return 6;
            if ((m & (1 << 19)) != 0) return 7;
            if ((m & (1 << 20)) != 0) return 8;
            if ((m & (1 << 28)) != 0) return 9;
        }

        var md5 = score?.BeatmapMd5 ?? header?.BeatmapHash;
        if (md5 is null)
        {
            return null;
        }

        var db = Beatmaps.LoadOsuDatabase();
        var entry = db?.FindByMd5(md5);
        return entry is null ? null : (int)Math.Round(entry.CircleSize);
    }

    /// <summary>Playback rate implied by DT/NC/HT. The GUI still lets the user override it.</summary>
    private static double? DeriveRate(int? mods)
    {
        if (mods is null)
        {
            return null;
        }

        var m = mods.Value;
        if ((m & (1 << 6)) != 0 || (m & (1 << 9)) != 0)
        {
            return 1.5;
        }

        if ((m & (1 << 8)) != 0)
        {
            return 0.75;
        }

        return 1.0;
    }

    public void Dispose() => Watcher.Dispose();
}

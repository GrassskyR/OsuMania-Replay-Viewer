namespace ManiaReplayMaster.Game;

/// <summary>osu! rulesets, matching the on-disk <c>mode</c> byte of both <c>.osr</c> and <c>osu!.db</c>.</summary>
public enum GameMode : byte
{
    Osu = 0,
    Taiko = 1,
    CatchTheBeat = 2,
    OsuMania = 3,
    Unknown = 255
}

/// <summary>
/// State machine of the game-link subsystem (plan §5.2):
/// <c>NotRunning → Idle → SongSelect → Playing → Results</c>, plus <see cref="ScorePanelOpen"/>
/// (the primary trigger of the auto-fill feature) and <see cref="WatchingReplay"/>.
/// </summary>
public enum OsuGameState
{
    /// <summary>No <c>osu!.exe</c> process found.</summary>
    NotRunning = 0,

    /// <summary>Process running, but the state could not be determined (memory unusable, unknown version).</summary>
    Unknown = 1,

    /// <summary>Main menu / editor / anywhere that is not song select or gameplay.</summary>
    Idle = 2,

    /// <summary>Song select.</summary>
    SongSelect = 3,

    /// <summary>Actively playing.</summary>
    Playing = 4,

    /// <summary>Results screen after a play.</summary>
    Results = 5,

    /// <summary>A score panel / local leaderboard entry is open — the feature's main trigger point.</summary>
    ScorePanelOpen = 6,

    /// <summary>A replay is actually being played back.</summary>
    WatchingReplay = 7
}

/// <summary>Why the memory layer could not produce a full snapshot. Never an exception in the UI path.</summary>
public enum MemoryReadStatus
{
    /// <summary>Everything needed was read successfully.</summary>
    Ok = 0,

    /// <summary>osu!.exe is not running.</summary>
    NotRunning = 1,

    /// <summary>OpenProcess failed (permission / elevated game / protected process).</summary>
    AccessDenied = 2,

    /// <summary>No signature matched in the module image — almost always an unknown game build.</summary>
    SignaturesNotFound = 3,

    /// <summary>Signatures resolved but the pointer chain produced an unreadable/implausible value.</summary>
    PointerChainFailed = 4,

    /// <summary>Signatures resolved, but the beatmap object could not be produced (main menu, etc.).</summary>
    BeatmapUnavailable = 5,

    /// <summary>Any other unexpected failure; see <see cref="GameSnapshot.Detail"/>.</summary>
    Error = 6
}

/// <summary>
/// One immutable observation of the game (plan §5.2). Every field is nullable/optional so that a
/// partial read is still useful: the GUI must stay fully functional when only some fields resolve.
/// </summary>
public sealed class GameSnapshot
{
    public OsuGameState State { get; init; } = OsuGameState.NotRunning;

    public MemoryReadStatus Status { get; init; } = MemoryReadStatus.NotRunning;

    /// <summary>Human-readable explanation of <see cref="Status"/>, for the status bar.</summary>
    public string? Detail { get; init; }

    /// <summary>Current beatmap MD5 (lowercase hex) — <c>[Beatmap] + 0x6C</c>.</summary>
    public string? BeatmapMd5 { get; init; }

    /// <summary>Song folder name — <c>[Beatmap] + 0x78</c>. Together with <see cref="BeatmapOsuFile"/>
    /// this composes the <c>.osu</c> path directly, skipping any MD5 index.</summary>
    public string? BeatmapFolder { get; init; }

    /// <summary><c>.osu</c> file name — <c>[Beatmap] + 0x90</c>.</summary>
    public string? BeatmapOsuFile { get; init; }

    public int? BeatmapId { get; init; }

    public int? BeatmapSetId { get; init; }

    public int? ObjectCount { get; init; }

    public GameMode Mode { get; init; } = GameMode.Unknown;

    public int Mods { get; init; }

    /// <summary>Player of the score currently being looked at, when available.</summary>
    public string? PlayerName { get; init; }

    public int? Score { get; init; }

    public int? MaxCombo { get; init; }

    /// <summary><c>[300, 100, 50, geki, katu, miss]</c>, i.e. for mania <c>[320, 300, 200, 320, 200, miss]</c>.</summary>
    public int[]? HitCounts { get; init; }

    public DateTimeOffset? ScoreDate { get; init; }

    /// <summary>True while a replay is actually playing back (<c>canRunSlowly + 0x46</c>).</summary>
    public bool IsWatchingReplay { get; init; }

    public int Retries { get; init; }

    /// <summary>Number of signature patterns that resolved. 0 == memory layer unusable.</summary>
    public int ResolvedSignatures { get; init; }

    /// <summary>Number of patterns attempted, so the UI can say "3/11 signatures".</summary>
    public int AttemptedSignatures { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>True when there is enough information to attempt score/replay resolution.</summary>
    public bool HasBeatmapIdentity =>
        !string.IsNullOrEmpty(BeatmapMd5) || (!string.IsNullOrEmpty(BeatmapFolder) && !string.IsNullOrEmpty(BeatmapOsuFile));

    /// <summary>True when memory reading produced usable data (as opposed to a degraded snapshot).</summary>
    public bool MemoryAvailable => Status == MemoryReadStatus.Ok || Status == MemoryReadStatus.BeatmapUnavailable;

    public static GameSnapshot NotRunning(string? detail = null) => new()
    {
        State = OsuGameState.NotRunning,
        Status = MemoryReadStatus.NotRunning,
        Detail = detail ?? "osu!.exe is not running."
    };

    public override string ToString()
        => $"GameSnapshot({State}, {Status}, md5={BeatmapMd5 ?? "-"}, folder={BeatmapFolder ?? "-"}, mods={Mods})";
}

/// <summary>How a replay candidate was located, ordered by reliability (plan §3.3).</summary>
public enum ReplayMatchSource
{
    /// <summary>Exact pointer from <c>scores.db</c> (<c>&lt;beatmapMd5&gt;-&lt;FILETIME&gt;.osr</c>).</summary>
    ScoresDbPointer = 0,

    /// <summary>Newly created file observed by <see cref="ReplayDirectoryWatcher"/>.</summary>
    DirectoryWatch = 1,

    /// <summary>Found by scoring every <c>.osr</c> header on disk.</summary>
    HeaderScan = 2,

    /// <summary>Reconstructed from an in-memory replay dump (stub in this version).</summary>
    MemoryDump = 3,

    /// <summary>Explicitly chosen by the user / caller.</summary>
    Manual = 4
}

/// <summary>A ranked replay candidate returned by <see cref="ReplayResolver"/>; never just one.</summary>
public sealed class ReplayCandidate
{
    public required string ReplayPath { get; init; }

    public required ReplayMatchSource Source { get; init; }

    /// <summary>0..1 confidence. 1.0 means "the pointer from scores.db and the header agree exactly".</summary>
    public required double Confidence { get; init; }

    /// <summary>Raw weighted score before normalisation; useful for debugging ties.</summary>
    public double RawScore { get; init; }

    /// <summary>The parsed <c>.osr</c> header, when it was read.</summary>
    public OsuReplayHeader? Header { get; init; }

    /// <summary>The <c>scores.db</c> record this candidate came from, when applicable.</summary>
    public ScoreRecord? Score { get; init; }

    /// <summary>Per-field match explanation, e.g. <c>beatmapMd5=exact, mods=match, score=match</c>.</summary>
    public string? Explanation { get; init; }

    public long FileSizeBytes { get; init; }

    public DateTimeOffset? FileModified { get; init; }

    /// <summary>
    /// Cached existence of <see cref="ReplayPath"/>. A <c>scores.db</c> record whose file was deleted
    /// still exists as a record, and the UI should say "this score has no replay file" rather than
    /// hiding the score.
    /// </summary>
    public bool FileExists { get; init; }

    public override string ToString()
        => $"{Confidence:P0} {Source} {Path.GetFileName(ReplayPath)}";
}

namespace ManiaReplayMaster.Game;

/// <summary>
/// One <c>osu!.db</c> record reduced to what beatmap resolution needs. The full 26 MB database is
/// walked field by field (see <c>docs/FORMATS.md</c> §3) because the record length is
/// variable; only the index-relevant fields are kept so the whole index fits in a few MB.
/// </summary>
public sealed record BeatmapIndexEntry
{
    /// <summary>32 lowercase hex MD5 of the <c>.osu</c> file bytes at import time.</summary>
    public required string Md5 { get; init; }

    /// <summary>Songs subfolder. May itself contain <c>\</c> (28 such records on the reference
    /// machine — locally created collections), so it must be combined as a relative path.</summary>
    public required string FolderName { get; init; }

    /// <summary>File name inside <see cref="FolderName"/>.</summary>
    public required string OsuFileName { get; init; }

    public int BeatmapId { get; init; } = -1;

    public int BeatmapSetId { get; init; } = -1;

    public GameMode Mode { get; init; } = GameMode.Unknown;

    public string? Artist { get; init; }

    public string? Title { get; init; }

    public string? DifficultyName { get; init; }

    public string? Creator { get; init; }

    public int TotalTimeMs { get; init; }

    public int PreviewTimeMs { get; init; }

    public int DrainTimeSeconds { get; init; }

    public float ApproachRate { get; init; }

    public float CircleSize { get; init; }

    public float HpDrainRate { get; init; }

    public float OverallDifficulty { get; init; }

    public double SliderVelocity { get; init; }

    public int TimingPointCount { get; init; }

    public int StarRatingPairCount { get; init; }

    /// <summary>Absolute path of the beatmap file, composed from the root (<c>Songs\&lt;folder&gt;\&lt;file&gt;</c>).</summary>
    public string? AbsolutePath { get; set; }

    public string DisplayName => string.Join(" - ", new[] { Artist, Title, DifficultyName }.Where(s => !string.IsNullOrEmpty(s)));

    public override string ToString() => $"{Md5} {FolderName}\\{OsuFileName}";
}

/// <summary>Options for <see cref="BeatmapDatabaseReader"/>.</summary>
public sealed class BeatmapDatabaseOptions
{
    public static BeatmapDatabaseOptions Default { get; } = new();

    /// <summary>Keep only these rulesets (empty = all). Mania rendering only needs 3, but the index is cheap.</summary>
    public IReadOnlySet<GameMode> Modes { get; init; } = new HashSet<GameMode>();

    /// <summary>
    /// When true (default) a walk that does not consume the file exactly is an error. The reference
    /// file satisfies this for all 22,123 records, so any deviation means the layout changed.
    /// </summary>
    public bool RequireExactByteConsumption { get; init; } = true;

    /// <summary>
    /// When true, keep only records whose composed <c>Songs\&lt;folder&gt;\&lt;file&gt;</c> path exists on disk.
    /// Off by default: the stale records are still useful for diagnostics and for explaining to the
    /// user why a beatmap could not be found.
    /// </summary>
    public bool ExistingFilesOnly { get; init; }

    /// <summary>Called with the running record count so a 26 MB walk can report progress.</summary>
    public Action<int>? Progress { get; init; }
}

/// <summary>Result counters of an <c>osu!.db</c> walk.</summary>
public sealed class BeatmapDatabaseResult
{
    public required string Path { get; init; }

    public required long FileLength { get; init; }

    public int Version { get; init; }

    public int DeclaredFolderCount { get; init; }

    public bool AccountUnlocked { get; init; }

    public long UnlockTimeTicks { get; init; }

    public string? PlayerName { get; init; }

    public int DeclaredBeatmapCount { get; init; }

    public int ParsedBeatmapCount { get; init; }

    public long BytesConsumed { get; init; }

    public int TrailerValue { get; init; }

    public bool HasTrailer { get; init; }

    public int TrailingByteCount { get; init; }

    public long StarRatingPairTotal { get; init; }

    public long TimingPointTotal { get; init; }

    public bool ExactByteConsumption => TrailingByteCount == 0 && BytesConsumed == FileLength;

    public IReadOnlyDictionary<string, BeatmapIndexEntry> ByMd5 { get; init; } =
        new Dictionary<string, BeatmapIndexEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<int, BeatmapIndexEntry> ByBeatmapId { get; init; } =
        new Dictionary<int, BeatmapIndexEntry>();

    public IReadOnlyDictionary<int, IReadOnlyList<BeatmapIndexEntry>> ByBeatmapSetId { get; init; } =
        new Dictionary<int, IReadOnlyList<BeatmapIndexEntry>>();

    public TimeSpan ParseDuration { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool Success { get; init; }

    public string? Error { get; init; }

    /// <summary>Mode histogram, e.g. mania 12,323 / osu 9,322 / taiko 391 / catch 87.</summary>
    public IReadOnlyDictionary<GameMode, int> ModeHistogram { get; init; } =
        new Dictionary<GameMode, int>();

    public int Count => ByMd5.Count;

    public string Summary =>
        $"{ParsedBeatmapCount}/{DeclaredBeatmapCount} records, {BytesConsumed}/{FileLength} bytes " +
        $"(trailing {TrailingByteCount}), version {Version}, {ByMd5.Count} unique MD5" +
        (Success ? string.Empty : $" — FAILED: {Error}");

    public BeatmapIndexEntry? FindByMd5(string? md5)
        => md5 is null ? null : ByMd5.TryGetValue(md5, out var entry) ? entry : null;

    public BeatmapIndexEntry? FindByBeatmapId(int id)
        => ByBeatmapId.TryGetValue(id, out var entry) ? entry : null;
}

/// <summary>Thrown when <c>osu!.db</c> does not match the documented layout.</summary>
public sealed class BeatmapDatabaseFormatException : Exception
{
    public BeatmapDatabaseFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

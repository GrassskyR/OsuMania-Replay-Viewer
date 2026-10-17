namespace ManiaReplayMaster.Game;

/// <summary>
/// One local score record from <c>scores.db</c>. Field order and types are exactly the table in
/// <c>docs/FORMATS.md</c> §3 (verified there against all 5,420 records and the matching
/// <c>.osr</c> headers).
/// </summary>
public sealed record ScoreRecord
{
    public GameMode Mode { get; init; } = GameMode.Unknown;

    /// <summary>Client build version (YYYYMMDD) that wrote the score, e.g. <c>20260924</c>.</summary>
    public int Version { get; init; }

    /// <summary>Beatmap MD5 — always equals the enclosing group key for a well-formed file.</summary>
    public string? BeatmapMd5 { get; init; }

    /// <summary>May legitimately be empty (258 such records on the reference machine).</summary>
    public string? PlayerName { get; init; }

    /// <summary>Hash of the replay payload. <b>Not</b> the <c>Data\r</c> file name — see <see cref="ReplayFileName"/>.</summary>
    public string? ReplayMd5 { get; init; }

    public ushort Count300 { get; init; }

    public ushort Count100 { get; init; }

    public ushort Count50 { get; init; }

    public ushort CountGeki { get; init; }

    public ushort CountKatu { get; init; }

    public ushort CountMiss { get; init; }

    public int TotalScore { get; init; }

    public ushort MaxCombo { get; init; }

    public bool Perfect { get; init; }

    /// <summary>Legacy mod bitmask (bit 29 = ScoreV2, bit 30 = Mirror).</summary>
    public int Mods { get; init; }

    /// <summary>Usually null (written as a single <c>0x00</c> byte).</summary>
    public string? HpGraph { get; init; }

    /// <summary>.NET <see cref="DateTime"/> ticks, UTC.</summary>
    public long DateTicks { get; init; }

    /// <summary>Byte-array length field; <c>-1</c> in every record of the reference file.</summary>
    public int ReplayLength { get; init; }

    /// <summary>Online score id after upload; <c>0</c> when never uploaded.</summary>
    public long OnlineId { get; init; }

    /// <summary>Byte offset of this record inside <c>scores.db</c>, for diagnostics.</summary>
    public long FileOffset { get; init; }

    /// <summary>
    /// <c>Data\r</c> file name for this score:
    /// <c>&lt;beatmapMd5&gt;-&lt;dateTicks - 504911232000000000&gt;.osr</c>.
    ///
    /// The critical measured correction (SCORESDB_FORMAT.md §4): the leading hex run is the
    /// <b>beatmap</b> MD5, not the record's <c>ReplayMd5</c>, and the decimal suffix is the score's
    /// own timestamp converted from .NET ticks to Windows FILETIME. That mapping resolved
    /// 5,420/5,420 records on the reference machine.
    /// </summary>
    public string ReplayFileName =>
        BeatmapMd5 is null
            ? throw new InvalidOperationException("Score record has no beatmap MD5.")
            : $"{BeatmapMd5}-{DateTicks - OsuReplayHeaderReader.DotNetTicksMinusFiletimeTicks}.osr";

    /// <summary>Windows FILETIME of the auto-saved replay file name.</summary>
    public long ReplayFiletime => DateTicks - OsuReplayHeaderReader.DotNetTicksMinusFiletimeTicks;

    public DateTimeOffset DateUtc
    {
        get
        {
            try
            {
                return new DateTimeOffset(Math.Max(0, DateTicks), TimeSpan.Zero);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTimeOffset.MinValue;
            }
        }
    }

    /// <summary>Accuracy in percent, using the mania/standard weighting. 0 when there are no judgements.</summary>
    public double AccuracyPercent
    {
        get
        {
            var total = (long)Count300 + Count100 + Count50 + CountMiss;
            if (total == 0)
            {
                return 0;
            }

            return 100.0 * (300.0 * Count300 + 100.0 * Count100 + 50.0 * Count50) / (300.0 * total);
        }
    }

    /// <summary>Hit counts ordered as <c>[300, 100, 50, geki, katu, miss]</c> (the plan §5.2 order).</summary>
    public int[] HitCounts => new[] { (int)Count300, (int)Count100, (int)Count50, (int)CountGeki, (int)CountKatu, (int)CountMiss };

    /// <summary>Human-readable summary used in the UI status bar.</summary>
    public string Describe()
        => $"{PlayerName ?? "(no name)"} · {TotalScore:N0} · {MaxCombo}x · {ModsToString(Mods)} · {DateUtc:yyyy-MM-dd HH:mm}";

    /// <summary>Decodes the legacy mod bitmask into the abbreviations osu! displays.</summary>
    public static string ModsToString(int mods)
    {
        if (mods == 0)
        {
            return "None";
        }

        var parts = new List<string>();
        void Add(int bit, string name)
        {
            if ((mods & bit) != 0)
            {
                parts.Add(name);
            }
        }

        Add(1 << 0, "NF");
        Add(1 << 1, "EZ");
        Add(1 << 3, "HD");
        Add(1 << 4, "HR");
        Add(1 << 5, "SD");
        Add(1 << 6, "DT");
        Add(1 << 7, "RX");
        Add(1 << 8, "HT");
        Add(1 << 9, "NC");
        Add(1 << 10, "FL");
        Add(1 << 12, "SO");
        Add(1 << 13, "AP");
        Add(1 << 14, "PF");
        Add(1 << 16, "4K");
        Add(1 << 17, "5K");
        Add(1 << 18, "6K");
        Add(1 << 19, "7K");
        Add(1 << 20, "8K");
        Add(1 << 21, "FI");
        Add(1 << 22, "RD");
        Add(1 << 26, "CN");
        Add(1 << 27, "TG");
        Add(1 << 28, "9K");
        Add(1 << 29, "V2");
        Add(1 << 30, "MR");
        return parts.Count == 0 ? $"0x{mods:X}" : string.Join(string.Empty, parts);
    }
}

/// <summary>Options for <see cref="ScoreDatabaseReader"/>.</summary>
public sealed class ScoreDatabaseOptions
{
    public static ScoreDatabaseOptions Default { get; } = new();

    /// <summary>
    /// When true (default) a parse whose documented walk does not consume the file to its last byte
    /// is reported as a failure instead of silently returning a possibly wrong list. Format drift in
    /// this undocumented, versioned file is the single biggest risk of the feature (R3).
    /// </summary>
    public bool RequireExactByteConsumption { get; init; } = true;

    /// <summary>Maximum number of trailing bytes still considered "padding, not drift".</summary>
    public int AllowedTrailingBytes { get; init; }

    /// <summary>Records whose embedded MD5 differs from the group are counted as anomalies.</summary>
    public bool DetectRecordAnomalies { get; init; } = true;
}

/// <summary>Outcome counters of a <c>scores.db</c> parse — everything the validation harness reports.</summary>
public sealed class ScoreDatabaseResult
{
    public required string Path { get; init; }

    public required long FileLength { get; init; }

    public int Version { get; init; }

    public int DeclaredGroupCount { get; init; }

    public int ParsedGroupCount { get; init; }

    public int ScoreRecordCount { get; init; }

    public int EmptyGroupCount { get; init; }

    public long BytesConsumed { get; init; }

    public int TrailingByteCount { get; init; }

    public bool ExactByteConsumption => TrailingByteCount == 0 && BytesConsumed == FileLength;

    public int DuplicateGroupMd5Count { get; init; }

    public int MalformedGroupMd5Count { get; init; }

    public int RecordMd5MismatchCount { get; init; }

    public int RecordVersionBelowOnlineIdGateCount { get; init; }

    public int MinimumRecordVersion { get; init; }

    public int MaximumRecordVersion { get; init; }

    public TimeSpan ParseDuration { get; init; }

    public IReadOnlyDictionary<string, List<ScoreRecord>> Groups { get; init; } =
        new Dictionary<string, List<ScoreRecord>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public bool Success { get; init; }

    public string? Error { get; init; }

    /// <summary>Total records across all groups (same as <see cref="ScoreRecordCount"/>).</summary>
    public int TotalScores => ScoreRecordCount;

    public string Summary =>
        $"{ParsedGroupCount}/{DeclaredGroupCount} groups, {ScoreRecordCount} records, " +
        $"{BytesConsumed}/{FileLength} bytes (trailing {TrailingByteCount}), version {Version}" +
        (Success ? string.Empty : $" — FAILED: {Error}");

    public IReadOnlyList<ScoreRecord> ForBeatmap(string beatmapMd5)
        => Groups.TryGetValue(beatmapMd5, out var list) ? list : Array.Empty<ScoreRecord>();
}

/// <summary>Thrown when <c>scores.db</c> cannot be parsed to the documented layout.</summary>
public sealed class ScoreDatabaseFormatException : Exception
{
    public ScoreDatabaseFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

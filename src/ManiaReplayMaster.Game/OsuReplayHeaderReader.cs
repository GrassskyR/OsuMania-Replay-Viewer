using ManiaReplayMaster.Core.Replay;

namespace ManiaReplayMaster.Game;

/// <summary>
/// A <c>.osr</c> header read without touching the LZMA payload.
///
/// This is a deliberate, documented subset of <c>ManiaReplayMaster.Core.Replay.OsuReplay</c>: the
/// field ordering and byte semantics are identical, but the compressed action stream is never read
/// or decompressed. That matters because replay resolution has to scan up to ~10,000 files
/// (<c>Data\r</c> on the reference machine) and the LZMA payload is 90 %+ of each file.
///
/// Equality with the full parser is asserted by the test suite over a sample of real files
/// (<c>OsuReplayHeaderReaderTests</c>), so the two can never silently disagree.
/// </summary>
public sealed record OsuReplayHeader
{
    /// <summary>Absolute path of the <c>.osr</c> file.</summary>
    public required string FilePath { get; init; }

    public long FileSizeBytes { get; init; }

    public DateTimeOffset? FileModified { get; init; }

    public GameMode Mode { get; init; } = GameMode.Unknown;

    /// <summary>Client build version (YYYYMMDD) that wrote the replay.</summary>
    public int FileVersion { get; init; }

    /// <summary>Beatmap MD5 from the header — the join key to <c>osu!.db</c> and <c>scores.db</c>.</summary>
    public string? BeatmapHash { get; init; }

    public string? Username { get; init; }

    /// <summary>Replay payload hash (equals <c>scores.db</c> field #5, but is <b>not</b> a file name).</summary>
    public string? ReplayHash { get; init; }

    public int Count300 { get; init; }

    public int Count100 { get; init; }

    public int Count50 { get; init; }

    public int CountGeki { get; init; }

    public int CountKatu { get; init; }

    public int Misses { get; init; }

    public int Score { get; init; }

    public int MaxCombo { get; init; }

    public bool Perfect { get; init; }

    public int Mods { get; init; }

    /// <summary>Usually null.</summary>
    public string? Diagram { get; init; }

    /// <summary>.NET ticks of the play, UTC.</summary>
    public long TimestampTicks { get; init; }

    /// <summary>Number of leading bytes that belong to the header (everything before the payload).</summary>
    public long HeaderLength { get; init; }

    /// <summary>Declared payload length, or <c>null</c> when the unreadable/negative sentinel was written.</summary>
    public int? DeclaredPayloadLength { get; init; }

    /// <summary>Payload length computed as <c>fileSize - headerLength - 8</c> (the trailing online id).</summary>
    public long? ComputedPayloadLength { get; init; }

    /// <summary>Online score id when present (may be 0).</summary>
    public long OnlineScoreId { get; init; }

    public bool HasScoreV2 => (Mods & (1 << 29)) != 0;

    public bool HasMirror => (Mods & (1 << 30)) != 0;

    public DateTimeOffset TimestampUtc
    {
        get
        {
            try
            {
                return new DateTimeOffset(Math.Max(0, TimestampTicks), TimeSpan.Zero);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTimeOffset.MinValue;
            }
        }
    }

    public int[] HitCounts => new[] { Count300, Count100, Count50, CountGeki, CountKatu, Misses };

    /// <summary>True when the parsed header is structurally consistent with the file it came from.</summary>
    public bool IsStructurallyPlausible
    {
        get
        {
            if (Mode is < GameMode.Osu or > GameMode.OsuMania)
            {
                return false;
            }

            if (FileVersion is < 20000000 or > 21000000)
            {
                return false;
            }

            if (!OsuStrings.LooksLikeMd5(BeatmapHash))
            {
                return false;
            }

            return ComputedPayloadLength is null or >= 0;
        }
    }

    /// <summary>Compares the identity fields that score matching relies on. Used by the validation harness.</summary>
    public bool HeaderFieldsEqual(OsuReplay other)
    {
        return Mode == (GameMode)other.Mode
               && FileVersion == other.FileVersion
               && BeatmapHash == other.BeatmapHash
               && Username == other.Username
               && ReplayHash == other.ReplayHash
               && Count300 == other.Hit300
               && Count100 == other.Hit100
               && Count50 == other.Hit50
               && CountGeki == other.Beat300
               && CountKatu == other.Beat100
               && Misses == other.Misses
               && Score == other.Points
               && MaxCombo == other.MaxCombo
               && Perfect == other.Perfect
               && Mods == other.Mods
               && Diagram == other.Diagram
               && TimestampTicks == other.TimestampTicks;
    }

    public override string ToString()
        => $"{Path.GetFileName(FilePath)} md5={BeatmapHash} {Username} {Score} {MaxCombo}x {ScoreRecord.ModsToString(Mods)}";
}

/// <summary>Outcome of a header read; a malformed file never throws out of a directory scan.</summary>
public sealed record OsuReplayHeaderResult
{
    public OsuReplayHeader? Header { get; init; }

    public string? Error { get; init; }

    public bool Success => Header is not null;

    public static OsuReplayHeaderResult Failed(string? error = null) => new() { Error = error };
}

/// <summary>
/// Header-only <c>.osr</c> reader. See <see cref="OsuReplayHeader"/> for why the payload is skipped.
/// </summary>
public static class OsuReplayHeaderReader
{
    /// <summary>
    /// .NET ticks of 1601-01-01T00:00:00Z. Subtracting it from a score's .NET <c>DateTicks</c> yields
    /// the Windows FILETIME used in the <c>Data\r</c> file name
    /// (<c>docs/SCORESDB_FORMAT.md</c> §4, 5,420/5,420 verified).
    /// </summary>
    public const long DotNetTicksMinusFiletimeTicks = 504911232000000000L;

    /// <summary>Minimum header size: fixed fields plus three 32-char hashes and an empty player name.</summary>
    private const long MinimumHeaderLength = 64;

    /// <summary>Online id gating copied from <c>LegacyScoreDecoder</c> (see SCORESDB_FORMAT.md §2.1).</summary>
    private const int OnlineIdInt64Version = 20140721;

    private const int OnlineIdInt32Version = 20121008;

    /// <summary>Reads only the header. Returns a failure result for a malformed or locked file.</summary>
    public static OsuReplayHeaderResult TryParse(string path)
    {
        try
        {
            return new OsuReplayHeaderResult { Header = Parse(path) };
        }
        catch (Exception ex)
        {
            return OsuReplayHeaderResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Reads only the header; throws on malformed input.</summary>
    public static OsuReplayHeader Parse(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Cannot stat '{path}'.", ex);
        }

        DateTimeOffset? modified = null;
        try
        {
            modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch
        {
            // A missing/invalid timestamp must not fail the read.
        }

        using var reader = ManiaReplayMaster.Core.IO.OsuBinaryReader.OpenFile(path);
        var mode = (GameMode)reader.ReadByte();
        var version = reader.ReadInt32();
        var beatmapHash = reader.ReadHeaderString();
        var username = reader.ReadHeaderString();
        var replayHash = reader.ReadHeaderString();
        var count300 = reader.ReadUInt16();
        var count100 = reader.ReadUInt16();
        var count50 = reader.ReadUInt16();
        var countGeki = reader.ReadUInt16();
        var countKatu = reader.ReadUInt16();
        var misses = reader.ReadUInt16();
        var score = reader.ReadInt32();
        var maxCombo = reader.ReadUInt16();
        var perfect = reader.ReadBoolean();
        var mods = reader.ReadInt32();
        var diagram = reader.ReadHeaderString();
        var timestamp = reader.ReadInt64();
        var payloadLength = reader.ReadInt32();
        var headerLength = reader.Position;

        if (headerLength < MinimumHeaderLength)
        {
            throw new InvalidDataException(
                $"'{path}' produced a {headerLength}-byte header, which is below the {MinimumHeaderLength}-byte minimum; not an .osr file?");
        }

        long? computedPayload = null;
        if (info.Exists && info.Length >= headerLength)
        {
            computedPayload = info.Length - headerLength - 8;
        }

        long onlineId = 0;
        if (version >= OnlineIdInt64Version && info.Exists && info.Length >= headerLength + 8)
        {
            // Safe: the header length is exact, so the trailing online id sits at a known offset.
            // The payload itself is never read into memory.
            try
            {
                onlineId = ReadTrailingOnlineId(path, headerLength);
            }
            catch
            {
                onlineId = 0;
            }
        }
        else if (version is >= OnlineIdInt32Version and < OnlineIdInt64Version && info.Exists && info.Length >= headerLength + 4)
        {
            try
            {
                onlineId = ReadTrailingOnlineId32(path, headerLength);
            }
            catch
            {
                onlineId = 0;
            }
        }

        return new OsuReplayHeader
        {
            FilePath = path,
            FileSizeBytes = info.Exists ? info.Length : 0,
            FileModified = modified,
            Mode = mode,
            FileVersion = version,
            BeatmapHash = beatmapHash,
            Username = username,
            ReplayHash = replayHash,
            Count300 = count300,
            Count100 = count100,
            Count50 = count50,
            CountGeki = countGeki,
            CountKatu = countKatu,
            Misses = misses,
            Score = score,
            MaxCombo = maxCombo,
            Perfect = perfect,
            Mods = mods,
            Diagram = diagram,
            TimestampTicks = timestamp,
            HeaderLength = headerLength,
            DeclaredPayloadLength = payloadLength,
            ComputedPayloadLength = computedPayload,
            OnlineScoreId = onlineId
        };
    }

    /// <summary>
    /// Reads the trailing <c>int64</c> online id without reading the LZMA payload in between.
    /// Read-only: the file is opened with <c>FileShare.ReadWrite</c> and never written.
    /// </summary>
    private static long ReadTrailingOnlineId(string path, long headerLength)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(headerLength, SeekOrigin.Begin);
        var buffer = new byte[8];
        ReadExactly(stream, buffer);
        return BitConverter.ToInt64(buffer, 0);
    }

    private static long ReadTrailingOnlineId32(string path, long headerLength)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // The int32 online id sits after the payload, so it is located from the end of the file.
        stream.Seek(-4, SeekOrigin.End);
        var buffer = new byte[4];
        ReadExactly(stream, buffer);
        return BitConverter.ToInt32(buffer, 0);
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
            {
                throw new EndOfStreamException("Unexpected end of file.");
            }

            read += n;
        }
    }

    /// <summary>True when the file name matches the <c>&lt;md5&gt;-&lt;18 digits&gt;.osr</c> pattern of <c>Data\r</c>.</summary>
    public static bool IsObfuscatedReplayName(string fileName, out string? beatmapMd5, out long? filetime)
    {
        beatmapMd5 = null;
        filetime = null;

        if (!fileName.EndsWith(".osr", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = fileName[..^4];
        var dash = stem.IndexOf('-');
        if (dash != 32 || stem.Length <= 34)
        {
            return false;
        }

        var md5 = stem[..32];
        if (!OsuStrings.LooksLikeMd5(md5))
        {
            return false;
        }

        if (!long.TryParse(stem[33..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var ft))
        {
            return false;
        }

        beatmapMd5 = md5.ToLowerInvariant();
        filetime = ft;
        return true;
    }

    /// <summary>
    /// The exact <c>Data\r</c> file name a score's replay would have. Kept here (rather than only on
    /// <see cref="ScoreRecord"/>) so the naming rule lives in one place next to the FILETIME constant.
    /// </summary>
    public static string ComposeDataRFileName(string beatmapMd5, long dateTicks)
        => $"{beatmapMd5.ToLowerInvariant()}-{dateTicks - DotNetTicksMinusFiletimeTicks}.osr";
}

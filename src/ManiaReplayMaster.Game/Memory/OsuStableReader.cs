using System.Globalization;

namespace ManiaReplayMaster.Game.Memory;

/// <summary>
/// Reads the current game state out of a running osu!stable (plan §3.4.2 / §5.2).
///
/// Design constraints taken from the plan and honoured here:
/// <list type="bullet">
/// <item><b>Data, not logic.</b> Every offset lives in <see cref="OsuStableStructure"/> and every byte
/// pattern in <see cref="SignatureTables"/>; this class only walks them, so a new osu! build is handled
/// by replacing the table.</item>
/// <item><b>Graceful degradation.</b> Unknown build, denied access, or a null pointer chain produces a
/// <see cref="GameSnapshot"/> with a <see cref="MemoryReadStatus"/> and a human-readable
/// <see cref="GameSnapshot.Detail"/> — never an exception, and never a silently wrong value.</item>
/// <item><b>Read-only.</b> All access is through <see cref="ProcessMemory"/>, which only requests
/// <c>PROCESS_VM_READ | PROCESS_QUERY_INFORMATION</c>. Nothing is written to the game.</item>
/// </list>
///
/// <b>Calibration status:</b> the offsets are transcribed from the public tosu/gosumemory tables quoted
/// in plan §3.4.2 and have <b>not</b> been confirmed against a running game on this machine (the plan
/// forbids launching it, and §3.4.3 lists the selected-score offset as an open question). The
/// self-test target in <c>tools/gamecheck/x86target</c> verifies the machinery — region walking,
/// chunked reads, 4-byte pointer chains, wildcard signature scanning — but not these offsets.
/// </summary>
public sealed class OsuStableReader
{
    private readonly Dictionary<string, ulong> _resolved = new(StringComparer.OrdinalIgnoreCase);

    public OsuStableReader(
        ProcessMemory memory,
        ProcessModuleInfo module,
        SignatureResolution signatures,
        SignatureTable? table = null,
        SignatureScanner? scanner = null)
    {
        Memory = memory;
        Module = module;
        Signatures = signatures;
        Table = table ?? SignatureTables.Default;
        Scanner = scanner ?? new SignatureScanner(memory);
        Walker = new MemoryChainWalker(memory);

        foreach (var (name, address) in signatures.Addresses)
        {
            _resolved[name] = address;
        }

        if (signatures.TryGet(SignatureTables.BaseAddr, out var baseAddr))
        {
            // The beatmap object is at [baseAddr - 0xC]; that displacement is part of the table's
            // semantics, not a hard-coded constant in the read logic.
            BaseAddress = baseAddr;
        }
    }

    public ProcessMemory Memory { get; }

    public ProcessModuleInfo Module { get; }

    public SignatureResolution Signatures { get; }

    public SignatureTable Table { get; }

    public SignatureScanner Scanner { get; }

    public MemoryChainWalker Walker { get; }

    /// <summary>The <c>baseAddr</c> signature value; 0 when it did not resolve.</summary>
    public ulong BaseAddress { get; }

    /// <summary>Extra addresses the caller wants available as chain roots (used by the self-test).</summary>
    public Dictionary<string, ulong> ExtraResolvedAddresses { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the table against the module image. Returns a report instead of throwing so an unknown
    /// build is a first-class outcome.
    /// </summary>
    public static SignatureResolution ResolveSignatures(ProcessMemory memory, ProcessModuleInfo module, SignatureTable table)
        => new SignatureScanner(memory).Resolve(module, table);

    /// <summary>Reads one field of the structure table by name.</summary>
    public bool TryReadField(string fieldName, out MemoryValue value, out string? failure)
    {
        value = default;
        var field = OsuStableStructure.Get(fieldName);
        if (field is null)
        {
            failure = $"no such field '{fieldName}'";
            return false;
        }

        return TryReadField(field, out value, out failure);
    }

    public bool TryReadField(StructureField field, out MemoryValue value, out string? failure)
    {
        value = default;

        if (field.XorWithAddress != 0)
        {
            // mods = key1 ^ key2; reading only one key yields a number that looks valid and is wrong.
            if (!Walker.TryReadAt(field.XorWithAddress, MemoryValueKind.Int32, out var first, out failure))
            {
                return false;
            }

            if (!TryResolveFieldAddress(field, out var secondAddress, out failure))
            {
                return false;
            }

            if (!Memory.TryReadInt32(secondAddress, out var second))
            {
                failure = $"cannot read the second XOR key at 0x{secondAddress:X}";
                return false;
            }

            value = MemoryValue.FromInt(first.Integer ^ second);
            return PassesSanity(field, value.Integer, ref failure);
        }

        if (!TryResolveFieldAddress(field, out var address, out failure))
        {
            return false;
        }

        // A DirectAddress field whose kind is Int32 and whose mode is ChainAddress is an address, not a
        // value; the score/gameplay roots are read by their own helpers.
        if (field.Kind == MemoryValueKind.Byte)
        {
            if (!Memory.TryReadByte(address, out var b))
            {
                failure = $"no byte at 0x{address:X}";
                return false;
            }

            value = MemoryValue.FromInt(b);
            return PassesSanity(field, value.Integer, ref failure);
        }

        if (!Walker.TryReadAt(address, field.Kind, out value, out failure))
        {
            return false;
        }

        return PassesSanity(field, value.Integer, ref failure);
    }

    /// <summary>Resolves the address a field reads from, without reading its value.</summary>
    public bool TryResolveFieldAddress(StructureField field, out ulong address, out string? failure)
    {
        address = 0;
        failure = null;

        if (field.Mode == MemoryAccessMode.DirectAddress)
        {
            if (!TryResolveAddressByName(field.AddressSignature, field.SignatureOffset, out address, out failure))
            {
                return false;
            }

            address = (ulong)((long)address + field.Offset);
            return true;
        }

        if (!TryResolveAddressByName(field.BaseSignature, 0, out var root, out failure))
        {
            return false;
        }

        if (field.Mode == MemoryAccessMode.ChainAddress)
        {
            address = root;
            return true;
        }

        if (field.BaseSignature is not null
            && field.BaseSignature.Equals(SignatureTables.BaseAddr, StringComparison.OrdinalIgnoreCase))
        {
            // [Base - 0xC] is the beatmap object; the displacement is table data.
            root -= 0xC;
        }

        if (field.Chain.Count == 0)
        {
            // No dereference at all: the base address is the object and the offset selects the field.
            address = (ulong)((long)root + field.Offset);
            return true;
        }

        var chain = field.ToChain(root);
        if (!Walker.TryResolve(chain, out var finalAddress, out failure))
        {
            return false;
        }

        // The offset is an offset *inside* the final object, so it is applied after the chain resolves
        // ([[Beatmap] + 0x6C]: dereference Beatmap, add 0x6C, read). Applying it to the last dereference
        // instead reads one level short.
        address = (ulong)((long)finalAddress + field.Offset);
        return true;
    }

    /// <summary>
    /// Resolves a signature name to an address. Supports the "<c>Name+0xNN+0xMM</c>" form used by the
    /// derived score/gameplay roots: each segment after the first is read as a 32-bit pointer stored at
    /// that offset inside the previous object, which is exactly how the plan's chains compose
    /// (<c>readInt(ruleset + 0x64)</c> → <c>readInt(gameplayBase + 0x38)</c> → …).
    /// </summary>
    public bool TryResolveAddressByName(string? name, long offset, out ulong address, out string? failure)
    {
        address = 0;
        failure = null;

        if (string.IsNullOrEmpty(name))
        {
            failure = "no address source";
            return false;
        }

        var segments = name.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            failure = $"'{name}' is not an address source";
            return false;
        }

        if (!TryResolveBaseName(segments[0].Trim(), out var value, out failure))
        {
            return false;
        }

        // Each following segment points inside the object the previous step produced:
        // "RulesetsAddr+0x64+0x38" == readPointer(readPointer(RulesetsAddr) + 0x64) + 0x38.
        for (var i = 1; i < segments.Length; i++)
        {
            var deltaText = segments[i].Trim();
            ulong delta;
            if (deltaText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!ulong.TryParse(deltaText[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out delta))
                {
                    failure = $"'{deltaText}' in '{name}' is not a hex offset";
                    return false;
                }
            }
            else if (!ulong.TryParse(deltaText, NumberStyles.Integer, CultureInfo.InvariantCulture, out delta))
            {
                failure = $"'{deltaText}' in '{name}' is not an offset";
                return false;
            }

            var pointerAddress = value + delta;
            if (!Memory.TryReadPointer(pointerAddress, out var pointed) || pointed == 0)
            {
                failure = $"no pointer at 0x{pointerAddress:X} (step {i} of '{name}')";
                return false;
            }

            value = pointed;
        }

        address = (ulong)((long)value + offset);
        return true;
    }

    private bool TryResolveBaseName(string name, out ulong address, out string? failure)
    {
        address = 0;
        failure = null;

        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(name[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var literal))
        {
            address = literal;
            return true;
        }

        if (_resolved.TryGetValue(name, out var resolved))
        {
            address = resolved;
            return true;
        }

        if (ExtraResolvedAddresses.TryGetValue(name, out var extra))
        {
            address = extra;
            return true;
        }

        failure = $"signature '{name}' did not resolve";
        return false;
    }

    /// <summary>Wraps the captured variables so a sanity failure can carry a message.</summary>
    private bool PassesSanity(StructureField field, long value, ref string? failure)
    {
        if (field.SanityMaximum > 0 && (value < 0 || value > field.SanityMaximum))
        {
            failure = $"{field.Name} = {value} is outside the plausible range 0..{field.SanityMaximum}";
            return false;
        }

        return true;
    }

    // ---------------------------------------------------------------- snapshot

    /// <summary>
    /// Builds a full <see cref="GameSnapshot"/>. Never throws: every failure lands in
    /// <see cref="GameSnapshot.Status"/> and <see cref="GameSnapshot.Detail"/>.
    /// </summary>
    public GameSnapshot ReadSnapshot(DateTimeOffset? timestamp = null)
    {
        var notes = new List<string>();

        var status = ReadStatus(notes);
        var state = MapState(status);

        string? md5 = null;
        string? folder = null;
        string? osuFile = null;
        int? beatmapId = null;
        int? setId = null;
        int? objectCount = null;
        var mode = GameMode.Unknown;
        double? circleSize = null;

        if (TryReadField(OsuStableStructure.BeatmapMd5, out var md5Value, out var md5Failure))
        {
            var text = md5Value.Text?.Trim();
            if (text is not null && OsuStrings.LooksLikeMd5(text))
            {
                md5 = text.ToLowerInvariant();
            }
            else
            {
                notes.Add($"beatmap MD5 implausible ('{Truncate(text)}')");
            }
        }
        else if (md5Failure is not null)
        {
            notes.Add($"beatmap MD5: {md5Failure}");
        }

        if (TryReadField(OsuStableStructure.BeatmapFolder, out var folderValue, out _))
        {
            folder = folderValue.Text;
        }

        if (TryReadField(OsuStableStructure.BeatmapOsuFile, out var fileValue, out _))
        {
            osuFile = fileValue.Text;
        }

        if (TryReadField(OsuStableStructure.BeatmapId, out var idValue, out _))
        {
            beatmapId = (int)idValue.Integer;
        }

        if (TryReadField(OsuStableStructure.BeatmapSetId, out var setIdValue, out _))
        {
            setId = (int)setIdValue.Integer;
        }

        if (TryReadField(OsuStableStructure.BeatmapObjectCount, out var countValue, out _))
        {
            objectCount = (int)countValue.Integer;
        }

        if (TryReadField(OsuStableStructure.CircleSize, out var csValue, out _))
        {
            circleSize = csValue.Real;
            if (circleSize is > 0 and < 10)
            {
                mode = GameMode.OsuMania;
            }
        }

        var hasBeatmapObject = TryReadField(OsuStableStructure.BeatmapMd5, out _, out _);
        if (!hasBeatmapObject
            && folder is null
            && osuFile is null)
        {
            // Nothing usable: report the most specific reason we have.
            var readStatus = Signatures.ResolvedCount == 0
                ? MemoryReadStatus.SignaturesNotFound
                : MemoryReadStatus.BeatmapUnavailable;

            return new GameSnapshot
            {
                State = state,
                Status = readStatus,
                Detail = Signatures.Summary + (notes.Count == 0 ? string.Empty : " | " + string.Join("; ", notes)),
                AttemptedSignatures = Signatures.AttemptedCount,
                ResolvedSignatures = Signatures.ResolvedCount,
                Timestamp = timestamp ?? DateTimeOffset.Now
            };
        }

        var mods = ReadMods(notes);
        var playerName = ReadStringField(OsuStableStructure.PlayerName, notes, "player name");
        var score = ReadIntField(OsuStableStructure.ScoreBase, 0x78, notes, "score");
        var maxCombo = ReadIntField(OsuStableStructure.ScoreBase, 0x68, notes, "max combo");
        var retries = ReadIntField(OsuStableStructure.Retries, 0, notes, "retries") ?? 0;
        var hitCounts = ReadHitCounts(notes);
        var isWatchingReplay = ReadReplayFlag(notes);
        var scoreDate = ReadScoreDate(notes);

        var detail = Signatures.Summary;
        if (notes.Count > 0)
        {
            detail += " | " + string.Join("; ", notes.Take(8));
        }

        return new GameSnapshot
        {
            State = state,
            Status = MemoryReadStatus.Ok,
            Detail = detail,
            BeatmapMd5 = md5,
            BeatmapFolder = folder,
            BeatmapOsuFile = osuFile,
            BeatmapId = beatmapId,
            BeatmapSetId = setId,
            ObjectCount = objectCount,
            Mode = mode,
            Mods = mods,
            PlayerName = playerName,
            Score = score,
            MaxCombo = maxCombo,
            HitCounts = hitCounts,
            ScoreDate = scoreDate,
            IsWatchingReplay = isWatchingReplay,
            Retries = retries,
            ResolvedSignatures = Signatures.ResolvedCount,
            AttemptedSignatures = Signatures.AttemptedCount,
            Timestamp = timestamp ?? DateTimeOffset.Now
        };
    }

    /// <summary>Reads the raw game-state value (<c>[Status - 0x4]</c>).</summary>
    public int? ReadStatus(List<string>? notes = null)
    {
        if (TryReadField(OsuStableStructure.Status, out var value, out var failure))
        {
            return (int)value.Integer;
        }

        if (failure is not null)
        {
            notes?.Add($"state: {failure}");
        }

        return null;
    }

    /// <summary>Maps the raw status to the plan §5.2 state machine, complemented by the replay flag.</summary>
    public static OsuGameState MapState(int? rawStatus)
    {
        return rawStatus switch
        {
            0 => OsuGameState.Idle,
            1 => OsuGameState.SongSelect,
            2 => OsuGameState.Playing,
            7 => OsuGameState.Results,
            null => OsuGameState.Unknown,
            _ => OsuGameState.Unknown
        };
    }

    /// <summary>Current mods, computed as <c>key1 ^ key2</c>. 0 when unavailable.</summary>
    public int ReadMods(List<string>? notes = null)
    {
        var key1Field = OsuStableStructure.Get(OsuStableStructure.ModsXor1)!;
        var key2Field = OsuStableStructure.Get(OsuStableStructure.ModsXor2)!;

        if (!TryResolveFieldAddress(key1Field, out var key1Address, out var failure1))
        {
            notes?.Add($"mods: {failure1}");
            return 0;
        }

        if (!TryResolveFieldAddress(key2Field, out var key2Address, out var failure2))
        {
            notes?.Add($"mods: {failure2}");
            return 0;
        }

        if (!Memory.TryReadInt32(key1Address, out var key1) || !Memory.TryReadInt32(key2Address, out var key2))
        {
            notes?.Add("mods: XOR keys unreadable");
            return 0;
        }

        var mods = key1 ^ key2;
        if (!PlausibleMods(mods))
        {
            notes?.Add($"mods: 0x{mods:X} is not a plausible legacy mod mask (XOR keys may have moved)");
        }

        return mods;
    }

    /// <summary>
    /// Legacy mod masks only use bits 0..30 with a small set of known bits; a value with unrelated high
    /// bits means the two XOR keys were read from the wrong place.
    /// </summary>
    public static bool PlausibleMods(int mods)
    {
        if (mods == 0)
        {
            return true;
        }

        if (mods < 0)
        {
            return false;
        }

        const int known = (1 << 0) | (1 << 1) | (1 << 3) | (1 << 4) | (1 << 5) | (1 << 6) | (1 << 7)
                          | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 12) | (1 << 13) | (1 << 14)
                          | (1 << 16) | (1 << 17) | (1 << 18) | (1 << 19) | (1 << 20) | (1 << 21)
                          | (1 << 22) | (1 << 26) | (1 << 27) | (1 << 28) | (1 << 29) | (1 << 30);
        return (mods & ~known) == 0;
    }

    public string? ReadPlayerName(List<string>? notes = null)
        => ReadStringField(OsuStableStructure.PlayerName, notes, "player name");

    /// <summary>Hit counts in the order the rest of the app uses: <c>[300, 100, 50, geki, katu, miss]</c>.</summary>
    public int[]? ReadHitCounts(List<string>? notes = null)
    {
        if (!TryResolveAddressByName(OsuStableStructure.ScoreBase, 0, out var scoreBase, out var failure))
        {
            notes?.Add($"hit counts: {failure}");
            return null;
        }

        // scoreBase + 0x88 / 0x8A / 0x8C / 0x8E / 0x90 / 0x92 (plan §3.4.2).
        long[] offsets = { 0x88, 0x8A, 0x8C, 0x8E, 0x90, 0x92 };
        var counts = new int[6];
        for (var i = 0; i < offsets.Length; i++)
        {
            if (!Memory.TryReadUInt16(scoreBase + (ulong)offsets[i], out var value))
            {
                notes?.Add($"hit counts: no u16 at 0x{scoreBase + (ulong)offsets[i]:X}");
                return null;
            }

            counts[i] = value;
        }

        if (counts.All(c => c == 0))
        {
            notes?.Add("hit counts are all zero (no score loaded yet?)");
        }

        return counts;
    }

    /// <summary>True when a replay is actually playing back (<c>canRunSlowly + 0x46</c>).</summary>
    public bool ReadReplayFlag(List<string>? notes = null)
    {
        if (!TryResolveAddressByName(SignatureTables.CanRunSlowlyAddr, 0, out var address, out var failure))
        {
            notes?.Add($"replay flag: {failure}");
            return false;
        }

        // The signature points at code that loads a pointer; the flag lives inside the object it points
        // to (plan §3.4.2: readByte(readInt(canRunSlowlyAddr + 0x46))).
        if (!Memory.TryReadPointer(address, out var pointer) || pointer == 0)
        {
            notes?.Add($"replay flag: cannot dereference 0x{address:X}");
            return false;
        }

        if (!Memory.TryReadByte(pointer + 0x46, out var flag))
        {
            notes?.Add($"replay flag: cannot read byte at 0x{pointer + 0x46:X}");
            return false;
        }

        return flag == 1;
    }

    private string? ReadStringField(string fieldName, List<string>? notes, string label)
    {
        if (TryReadField(fieldName, out var value, out var failure))
        {
            return value.Text;
        }

        if (failure is not null)
        {
            notes?.Add($"{label}: {failure}");
        }

        return null;
    }

    private int? ReadIntField(string baseField, long offset, List<string>? notes, string label)
    {
        if (baseField.Equals(OsuStableStructure.ScoreBase, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveAddressByName(OsuStableStructure.ScoreBase, 0, out var scoreBase, out var failure))
            {
                notes?.Add($"{label}: {failure}");
                return null;
            }

            if (!Memory.TryReadInt32(scoreBase + (ulong)offset, out var value))
            {
                notes?.Add($"{label}: no i32 at 0x{scoreBase + (ulong)offset:X}");
                return null;
            }

            return value;
        }

        var field = OsuStableStructure.Get(baseField);
        var fieldFailure = default(string);
        if (field is not null && TryReadField(field, out var fieldValue, out fieldFailure))
        {
            return (int)fieldValue.Integer;
        }

        if (field is not null && fieldFailure is not null)
        {
            notes?.Add($"{label}: {fieldFailure}");
        }

        return null;
    }

    private DateTimeOffset? ReadScoreDate(List<string>? notes)
    {
        // The results screen's score object is the only place a per-score date appears. The offset is
        // listed as +0xA0/+0xA4 in the plan, but its representation is not confirmed on this build, so
        // a failure here is reported and simply leaves the field empty.
        if (!TryResolveAddressByName(OsuStableStructure.SelectedScore, 0, out var selectedScore, out _))
        {
            return null;
        }

        if (!Memory.TryReadInt64(selectedScore + 0xA0, out var ticks) || ticks <= 0)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            notes?.Add($"score date: {ticks} is not a valid .NET tick count");
            return null;
        }
    }

    private static string Truncate(string? value)
        => value is null ? "(null)" : value.Length <= 24 ? value : value[..24] + "…";
}

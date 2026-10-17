namespace ManiaReplayMaster.Game.Memory;

/// <summary>How a structure field's value is obtained from its address.</summary>
public enum MemoryValueKind
{
    /// <summary>The address holds a <c>u32</c>.</summary>
    Int32 = 0,

    /// <summary>The address holds a <c>u16</c>.</summary>
    UInt16 = 1,

    /// <summary>The address holds a single byte.</summary>
    Byte = 2,

    /// <summary>The address holds a <c>u64</c> (used for date fields when present).</summary>
    Int64 = 3,

    /// <summary>The address holds a 4-byte pointer to a NUL-terminated string.</summary>
    PointerToAnsiString = 4,

    /// <summary>The address holds a 4-byte pointer to a NUL-terminated UTF-8/ANSI string.</summary>
    PointerToUtf8String = 5,

    /// <summary>The address holds a 4-byte float.</summary>
    Float = 6,

    /// <summary>The address holds the .NET tick count of a <c>DateTime</c> (8 bytes).</summary>
    DotNetTicks = 7,

    /// <summary>A pointer to a CLR string object: method table, Int32 length, UTF-16 characters.</summary>
    PointerToManagedString = 8
}

/// <summary>How the chain and the offset compose before the value is read.</summary>
public enum MemoryAccessMode
{
    /// <summary>Resolve the pointer chain, add the offset, read the value there. The normal case.</summary>
    ChainThenOffset = 0,

    /// <summary>Take the chain's final address and read the value directly at it (no extra offset).</summary>
    ChainAddress = 1,

    /// <summary>No chain: read directly at <c>BaseAddress + Offset</c>.</summary>
    DirectAddress = 2,

    /// <summary>Treat the address as the start of an inline wide-char array (not a pointer to it).</summary>
    InlineString = 3
}

/// <summary>
/// One field of the §3.4.2 offset table, expressed as data. The plan is explicit that these offsets
/// must be updatable rather than compiled into logic: osu!stable is obfuscated and every update can
/// move them (risk R1). Verified fields were checked against live stable b20260924 on 2026-10-17.
///
/// <see cref="Verified"/> records exactly that: <c>false</c> means "transcribed from the public
/// tosu/gosumemory tables in the plan, not confirmed against a running game".
/// </summary>
public sealed record StructureField
{
    public required string Name { get; init; }

    /// <summary>Base address the chain starts from, either a signature name or a raw absolute address.</summary>
    public string? BaseSignature { get; init; }

    public ulong BaseAddress { get; init; }

    public MemoryAccessMode Mode { get; init; } = MemoryAccessMode.ChainThenOffset;

    /// <summary>Dereference steps applied to the base, e.g. <c>[0x8, 0xB8, 0x4]</c> for the Songs directory.</summary>
    public IReadOnlyList<ulong> Chain { get; init; } = Array.Empty<ulong>();

    /// <summary>Final offset added after the chain (the <c>+0x6C</c> in <c>[[Beatmap] + 0x6C]</c>).</summary>
    public long Offset { get; init; }

    public MemoryValueKind Kind { get; init; } = MemoryValueKind.Int32;

    /// <summary>
    /// A second independent pointer that must be XOR-ed with the first to yield the value. This is how
    /// the current mods are obtained in stable (two XOR keys); hard-coding one key would break on the
    /// next build.
    /// </summary>
    public ulong XorWithAddress { get; init; }

    /// <summary>Names of the other fields this one is derived from (XOR keys), for reporting.</summary>
    public IReadOnlyList<string> DerivedFrom { get; init; } = Array.Empty<string>();

    /// <summary>Upper bound for a plausible value, used as a sanity gate (0 = no gate).</summary>
    public long SanityMaximum { get; init; }

    /// <summary>For <see cref="MemoryAccessMode.DirectAddress"/>: a fixed signature that identifies
    /// this address (e.g. <c>canRunSlowlyAddr</c>), so no absolute address is hard-coded.</summary>
    public string? AddressSignature { get; init; }

    /// <summary>Offset applied to the resolved signature address before reading.</summary>
    public long SignatureOffset { get; init; }

    public string? Note { get; init; }

    /// <summary>False for every entry transcribed from public tables but not calibrated on a real build.</summary>
    public bool Verified { get; init; }

    public string Describe()
        => $"{Name}: [{BaseSignature ?? $"0x{BaseAddress:X}"}]" +
           (Chain.Count == 0 ? string.Empty : string.Concat(Chain.Select(c => $"[0x{c:X}]"))) +
           (Offset == 0 ? string.Empty : $" +0x{Offset:X}") +
           $" ({Kind}){(Verified ? string.Empty : " [UNVERIFIED]")}";

    /// <summary>
    /// Builds the runtime pointer chain for a resolved base address.
    ///
    /// The chain covers only the dereference steps. <see cref="Offset"/> is deliberately <b>not</b>
    /// folded into the last step: it is an offset inside the final object, so it must be applied after
    /// the chain resolves and before the value is read. <c>[[Beatmap] + 0x6C]</c> therefore means
    /// "dereference Beatmap, add 0x6C, read the value there". Folding the offset into the dereference
    /// (the earlier behaviour) read one level short and produced garbage for every field in the table.
    /// </summary>
    public PointerChain ToChain(ulong baseAddress)
    {
        var steps = new List<PointerChainStep>();
        foreach (var link in Chain)
        {
            steps.Add(new PointerChainStep(link));
        }

        if (steps.Count == 0)
        {
            // One dereference of the base itself; the field offset is applied by the caller afterwards.
            steps.Add(new PointerChainStep(0));
        }

        var chainBase = Mode == MemoryAccessMode.DirectAddress ? BaseAddress : baseAddress;
        return new PointerChain(Name, chainBase, steps.ToArray());
    }
}

/// <summary>
/// Stable structure fields, calibrated against tosu and live stable b20260924 on 2026-10-17.
/// Instruction operands and object indirection are explicit in SignatureOffset and Chain.
/// </summary>
public static class OsuStableStructure
{
    // --- Beatmap object (rooted at [Base - 0xC]) -------------------------------------------------
    public const string BeatmapMd5 = "BeatmapMd5";
    public const string BeatmapFolder = "BeatmapFolder";
    public const string BeatmapOsuFile = "BeatmapOsuFile";
    public const string BeatmapId = "BeatmapId";
    public const string BeatmapSetId = "BeatmapSetId";
    public const string BeatmapObjectCount = "BeatmapObjectCount";
    public const string BeatmapArtist = "BeatmapArtist";
    public const string BeatmapTitle = "BeatmapTitle";
    public const string BeatmapCreator = "BeatmapCreator";
    public const string BeatmapDifficulty = "BeatmapDifficulty";
    public const string ApproachRate = "ApproachRate";
    public const string CircleSize = "CircleSize";
    public const string HpDrain = "HpDrain";
    public const string OverallDifficulty = "OverallDifficulty";

    // --- Settings -------------------------------------------------------------------------------
    public const string SongsDirectory = "SongsDirectory";

    // --- Score / gameplay -----------------------------------------------------------------------
    public const string Ruleset = "Ruleset";
    public const string GameplayBase = "GameplayBase";
    public const string ScoreBase = "ScoreBase";
    public const string PlayerName = "PlayerName";
    public const string ModsXor1 = "ModsXor1";
    public const string ModsXor2 = "ModsXor2";
    public const string Mods = "Mods";
    public const string Retries = "Retries";
    public const string CanRunSlowly = "CanRunSlowly";
    public const string Status = "Status";
    public const string GameTime = "GameTime";
    public const string SelectedScore = "SelectedScore";

    private static readonly StructureField[] AllFields = Build();

    public static IReadOnlyList<StructureField> Fields => AllFields;

    public static StructureField? Get(string name)
        => AllFields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static StructureField[] Build()
    {
        // Helper so every entry's intent stays readable and the unverified status is uniform.
        static StructureField F(
            string name,
            string? baseSignature,
            long offset,
            MemoryValueKind kind,
            ulong[]? chain = null,
            string? note = null,
            bool verified = false,
            MemoryAccessMode mode = MemoryAccessMode.ChainThenOffset,
            string? addressSignature = null,
            long signatureOffset = 0,
            long sanityMaximum = 0)
            => new()
            {
                Name = name,
                BaseSignature = baseSignature,
                Chain = chain ?? Array.Empty<ulong>(),
                Offset = offset,
                Kind = kind,
                Note = note,
                Verified = verified,
                Mode = mode,
                AddressSignature = addressSignature,
                SignatureOffset = signatureOffset,
                SanityMaximum = sanityMaximum
            };

        // tosu's readPointer(address) reads the operand and then the value at that address.
        // Keep those two dereferences explicit; ProcessMemory.TryReadPointer reads only once.
        StructureField B(string name, long offset, MemoryValueKind kind)
            => F(name, SignatureTables.BaseAddr, offset, kind, chain: new ulong[] { 0, 0 },
                signatureOffset: -0xC, verified: true);
        StructureField S(string name, long offset, MemoryValueKind kind)
            => F(name, ScoreBase, offset, kind, verified: true);

        return new[]
        {
            B(BeatmapMd5, 0x6C, MemoryValueKind.PointerToManagedString),
            B(BeatmapFolder, 0x78, MemoryValueKind.PointerToManagedString),
            B(BeatmapOsuFile, 0x90, MemoryValueKind.PointerToManagedString),
            B(BeatmapId, 0xC8, MemoryValueKind.Int32),
            B(BeatmapSetId, 0xCC, MemoryValueKind.Int32),
            B(BeatmapObjectCount, 0xF8, MemoryValueKind.Int32),
            B(BeatmapArtist, 0x18, MemoryValueKind.PointerToManagedString),
            B(BeatmapTitle, 0x24, MemoryValueKind.PointerToManagedString),
            B(BeatmapCreator, 0x7C, MemoryValueKind.PointerToManagedString),
            B(BeatmapDifficulty, 0xAC, MemoryValueKind.PointerToManagedString),
            B(ApproachRate, 0x2C, MemoryValueKind.Float),
            B(CircleSize, 0x30, MemoryValueKind.Float),
            B(HpDrain, 0x34, MemoryValueKind.Float),
            B(OverallDifficulty, 0x38, MemoryValueKind.Float),
            F(SongsDirectory, SignatureTables.SettingsClassAddr, 0x4, MemoryValueKind.PointerToManagedString,
                chain: new ulong[] { 0x8, 0xB8 }, verified: true),
            F(Ruleset, SignatureTables.RulesetsAddr, 4, MemoryValueKind.Int32,
                chain: new ulong[] { 0 }, signatureOffset: -0xB, verified: true),
            F(GameplayBase, Ruleset, 0x64, MemoryValueKind.Int32),
            S(PlayerName, 0x28, MemoryValueKind.PointerToManagedString),
            F(ModsXor1, ScoreBase, 0xC, MemoryValueKind.Int32, chain: new ulong[] { 0x1C }, verified: true),
            F(ModsXor2, ScoreBase, 0x8, MemoryValueKind.Int32, chain: new ulong[] { 0x1C }, verified: true),
            F(Retries, SignatureTables.BaseAddr, 0x8, MemoryValueKind.Int32,
                chain: new ulong[] { 0 }, signatureOffset: -0x33, verified: true),
            F(CanRunSlowly, SignatureTables.CanRunSlowlyAddr, 0, MemoryValueKind.Byte,
                chain: new ulong[] { 0 }, signatureOffset: 0x46, verified: true),
            F(Status, SignatureTables.StatusPtr, 0, MemoryValueKind.Int32,
                chain: new ulong[] { 0 }, sanityMaximum: 64, verified: true),
            F(GameTime, SignatureTables.GameTimePtr, 0, MemoryValueKind.Int32, verified: true),
            F(SelectedScore, Ruleset, 0x38, MemoryValueKind.Int32, verified: true)
        };
    }
}

/// <summary>
/// Signature tables transcribed from plan §3.4.2 (which quotes the public tosu / gosumemory sources).
///
/// <b>Scanning scope is not optional.</b> The installed <c>osu!.exe</c> was parsed as a PE image and is a
/// <i>managed .NET assembly</i> — its only imports come from <c>mscoree.dll</c>, it has a CLR data
/// directory, and its 4.2 MB <c>.text</c> section holds IL rather than native code. The x86 instruction
/// sequences in these patterns therefore <b>do not occur anywhere in the on-disk image</b> (measured: 0 of
/// 10 entries match the mapped sections; see <c>gamecheck staticsig</c>). They exist only as JIT-compiled
/// code in the CLR's executable heaps while the game runs, which is why every real entry carries
/// <see cref="SignatureEntry.SearchWholeProcess"/> = <c>true</c> — the same whole-process sweep tosu's own
/// scanner performs. A module-image scan silently returns 0 of 10 and disables the memory layer.
///
/// The tables are still treated as data: versioned, replaceable, and every failure is reported per entry so
/// an unrecognised build degrades to the file-system path instead of misreporting the game state. The score and beatmap chains were calibrated against live stable b20260924 on 2026-10-17.
/// </summary>
public static class SignatureTables
{
    public const string GameVersionRange = "osu!stable b20260924 — live-calibrated 2026-10-17";

    // Names used by the field table.
    public const string BaseAddr = "baseAddr";
    public const string StatusPtr = "statusPtr";
    public const string PlayTimeAddr = "playTimeAddr";
    public const string SettingsClassAddr = "settingsClassAddr";
    public const string RulesetsAddr = "rulesetsAddr";
    public const string CanRunSlowlyAddr = "canRunSlowlyAddr";
    public const string GameTimePtr = "gameTimePtr";
    public const string SkinDataAddr = "skinDataAddr";
    public const string MenuModsPtr = "menuModsPtr";
    public const string ManiaSpeed = "MANIA_SPEED";

    // Synthetic names used only by the 32-bit self-test target (see tools/gamecheck/x86target).
    public const string SelfTestRoot = "selftestRoot";

    /// <summary>
    /// An entry of the real table. Parameter names deliberately mirror <see cref="SignatureEntry"/> so the
    /// transcriptions below read literally as the plan's table. Every real entry is whole-process: the code
    /// it points at is JIT-compiled and absent from the module image (see the class remarks).
    /// </summary>
    private static SignatureEntry StableEntry(
        string Name,
        string Pattern,
        SignatureStrategy Strategy = SignatureStrategy.Auto,
        long Offset = 0,
        string? Note = null)
        => new(Name, Pattern, Strategy, Offset, SearchWholeProcess: true, Note: Note);

    /// <summary>The stable table quoted in plan §3.4.2.</summary>
    public static SignatureTable Stable { get; } = new(
        "osu!stable (tosu, calibrated b20260924)",
        GameVersionRange,
        StableEntry(BaseAddr, "F8 01 74 04 83 65",
            Note: "root of every pointer chain"),
        StableEntry(StatusPtr, "48 83 F8 04 73 1E", SignatureStrategy.MatchAddressPlusOffset, -0x4,
            Note: "operand at match - 4 -> state address"),
        StableEntry(PlayTimeAddr, "5E 5F 5D C3 A1 ?? ?? ?? ?? 89 ?? 04",
            Note: "play time; offset 0x1 in the tosu tables"),
        StableEntry(SettingsClassAddr, "83 E0 20 85 C0 7E 2F",
            Note: "settings class -> Songs directory"),
        StableEntry(RulesetsAddr, "7D 15 A1 ?? ?? ?? ?? 85 C0",
            Note: "current ruleset (beatmap) root"),
        StableEntry(CanRunSlowlyAddr, "55 8B EC 80 3D ?? ?? ?? ?? 00 75 26 80 3D",
            Note: "replay playback flag"),
        StableEntry(GameTimePtr, "A1 ?? ?? ?? ?? 89 46 04 8B D6 E8", SignatureStrategy.AbsoluteDataReference,
            Note: "game clock; tosu applies offset 0x1"),
        StableEntry(SkinDataAddr, "74 2C 85 FF 75 28 A1 ?? ?? ?? ?? 8D 15",
            Note: "skin data"),
        StableEntry(MenuModsPtr, "C8 FF ?? ?? ?? ?? ?? 81 0D ?? ?? ?? ?? ?? 08 00 00",
            SignatureStrategy.MatchAddressPlusOffset, 0x9,
            Note: "operand at match + 9 -> menu mod mask; score mods use their own XOR object"),
        StableEntry(ManiaSpeed, "a3 ?? ?? ?? ?? eb ?? dd 45 08 db 5d e8 8b 45 e8 83 f8 28",
            Note: "mania scroll speed"));

    /// <summary>
    /// Table for the synthetic 32-bit target used by the scanner self-test. It mirrors the layout the
    /// real table uses (a static root pointer, an offset, a pointer chain and a string) so that the
    /// self-test exercises the same code paths. Module-scoped on purpose: the pattern really is written
    /// into the target's module image, which is what makes it a useful control for the scanner.
    /// </summary>
    public static SignatureTable SelfTest { get; } = new(
        "gamecheck x86 self-test target",
        "not a game build",
        new SignatureEntry(SelfTestRoot, "A1 ?? ?? ?? ?? DE AD C0 DE 12 34 78",
            Note: "the four wildcard bytes are the absolute address of the self-test root structure"),
        new SignatureEntry(StatusPtr, "11 22 33 44 55 66 77 88",
            Note: "static byte pattern placed inside the target's data section"));

    /// <summary>The table used for real runs.</summary>
    public static SignatureTable Default => Stable;
}

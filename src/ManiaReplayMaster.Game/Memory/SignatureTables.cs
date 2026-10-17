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
    DotNetTicks = 7
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
/// move them (risk R1), and none of them can be validated on this machine because the game is not
/// available for calibration.
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
/// The §3.4.2 field table. Values come straight from the plan's transcription of the public
/// tosu/gosumemory tables; the plan itself flags that "是否正在看回放", the selected-score offset and
/// per-build validity all still need a real game session (§3.4.3, V2–V7), so nothing here is marked
/// <see cref="StructureField.Verified"/>.
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

        return new[]
        {
            // The beatmap object sits at [baseAddr - 0xC]; every field below is an offset inside it.
            F(BeatmapMd5, SignatureTables.BaseAddr, 0x6C, MemoryValueKind.PointerToUtf8String,
                note: "[[Beatmap] + 0x6C], 32 lowercase hex"),
            F(BeatmapFolder, SignatureTables.BaseAddr, 0x78, MemoryValueKind.PointerToUtf8String,
                note: "[[Beatmap] + 0x78]"),
            F(BeatmapOsuFile, SignatureTables.BaseAddr, 0x90, MemoryValueKind.PointerToUtf8String,
                note: "[[Beatmap] + 0x90], should end in .osu"),
            F(BeatmapId, SignatureTables.BaseAddr, 0xC8, MemoryValueKind.Int32, note: "[[Beatmap] + 0xC8]"),
            F(BeatmapSetId, SignatureTables.BaseAddr, 0xCC, MemoryValueKind.Int32, note: "[[Beatmap] + 0xCC]"),
            F(BeatmapObjectCount, SignatureTables.BaseAddr, 0xF8, MemoryValueKind.Int32,
                note: "tosu uses +0xF8; gosumemory (older) used +0xFC"),
            F(BeatmapArtist, SignatureTables.BaseAddr, 0x18, MemoryValueKind.PointerToUtf8String),
            F(BeatmapTitle, SignatureTables.BaseAddr, 0x24, MemoryValueKind.PointerToUtf8String),
            F(BeatmapCreator, SignatureTables.BaseAddr, 0x7C, MemoryValueKind.PointerToUtf8String),
            F(BeatmapDifficulty, SignatureTables.BaseAddr, 0xAC, MemoryValueKind.PointerToUtf8String),
            F(ApproachRate, SignatureTables.BaseAddr, 0x2C, MemoryValueKind.Float),
            F(CircleSize, SignatureTables.BaseAddr, 0x30, MemoryValueKind.Float,
                note: "key count for mania"),
            F(HpDrain, SignatureTables.BaseAddr, 0x34, MemoryValueKind.Float),
            F(OverallDifficulty, SignatureTables.BaseAddr, 0x38, MemoryValueKind.Float),

            // Songs directory: [[[Settings + 0x8] + 0xB8] + 0x4]
            F(SongsDirectory, SignatureTables.SettingsClassAddr, 0x4, MemoryValueKind.PointerToUtf8String,
                chain: new ulong[] { 0x8, 0xB8 },
                note: "[[[Settings + 0x8] + 0xB8] + 0x4]"),

            // Ruleset / gameplay / score.
            F(Ruleset, null, 0, MemoryValueKind.Int32, mode: MemoryAccessMode.DirectAddress,
                addressSignature: SignatureTables.RulesetsAddr, signatureOffset: -0xB,
                note: "readInt(readInt(rulesetsAddr - 0xB) + 0x4); the final +0x4 is applied by the reader"),
            F(GameplayBase, null, 0, MemoryValueKind.Int32, mode: MemoryAccessMode.DirectAddress,
                addressSignature: SignatureTables.RulesetsAddr + "+0x64",
                note: "readInt(ruleset + 0x64)"),
            F(ScoreBase, null, 0, MemoryValueKind.Int32, mode: MemoryAccessMode.DirectAddress,
                addressSignature: SignatureTables.RulesetsAddr + "+0x64+0x38",
                note: "readInt(gameplayBase + 0x38)"),
            F(PlayerName, null, 0x28, MemoryValueKind.PointerToUtf8String, mode: MemoryAccessMode.DirectAddress,
                addressSignature: SignatureTables.RulesetsAddr + "+0x64+0x38+0x28",
                note: "[[[Ruleset + 0x68] + 0x38] + 0x28]; the intermediate chain is not confirmed on this build"),
            F(ModsXor1, null, 0, MemoryValueKind.Int32, mode: MemoryAccessMode.DirectAddress,
                addressSignature: SignatureTables.MenuModsPtr,
                note: "first XOR key of the current mods"),
            F(ModsXor2, null, 0x4, MemoryValueKind.Int32, mode: MemoryAccessMode.DirectAddress,
                addressSignature: SignatureTables.MenuModsPtr,
                note: "second XOR key; mods = key1 ^ key2"),
            F(Retries, SignatureTables.BaseAddr, 0x8, MemoryValueKind.Int32,
                note: "readInt(readInt(baseAddr - 0x33) + 0x8); the -0x33 is applied before this field's chain"),
            F(CanRunSlowly, null, 0x46, MemoryValueKind.Byte, mode: MemoryAccessMode.DirectAddress,
                addressSignature: SignatureTables.CanRunSlowlyAddr,
                note: "readByte(readInt(canRunSlowlyAddr + 0x46)) == 1 means a replay is playing back"),
            F(Status, null, -0x4, MemoryValueKind.Int32, mode: MemoryAccessMode.DirectAddress,
                addressSignature: SignatureTables.StatusPtr, signatureOffset: -0x4,
                note: "[Status - 0x4] -> 0 menu / 1 song select / 2 playing / 7 results",
                sanityMaximum: 64),
            F(GameTime, SignatureTables.GameTimePtr, 0, MemoryValueKind.Int32,
                note: "game clock; used to detect a stalled read"),
            F(SelectedScore, SignatureTables.RulesetsAddr, 0x38, MemoryValueKind.Int32,
                mode: MemoryAccessMode.DirectAddress,
                addressSignature: SignatureTables.RulesetsAddr + "+0x38",
                note: "readInt(ruleset + 0x38) -> selected score object. UNVERIFIED: the plan §3.4.3 " +
                      "unknown #1 asks whether the *selected* local-leaderboard row has a stable offset at all")
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
/// an unrecognised build degrades to the file-system path instead of misreporting the game state. None of
/// the offsets has been calibrated against a live game on this machine (plan §3.4.3, V2–V7).
/// </summary>
public static class SignatureTables
{
    public const string GameVersionRange = "osu!stable 2026 build (b20260924) — scan scope verified, offsets UNVERIFIED";

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
        "osu!stable (plan §3.4.2)",
        GameVersionRange,
        StableEntry(BaseAddr, "F8 01 74 04 83 65",
            Note: "root of every pointer chain"),
        StableEntry(StatusPtr, "48 83 F8 04 73 1E", SignatureStrategy.MatchAddressPlusOffset, -0x4,
            Note: "[Status - 0x4] -> game state"),
        StableEntry(PlayTimeAddr, "5E 5F 5D C3 A1 ?? ?? ?? ?? 89 ?? 04",
            Note: "play time; offset 0x1 in the tosu tables"),
        StableEntry(SettingsClassAddr, "83 E0 20 85 C0 7E 2F",
            Note: "settings class -> Songs directory"),
        StableEntry(RulesetsAddr, "7D 15 A1 ?? ?? ?? ?? 85 C0",
            Note: "current ruleset (beatmap) root"),
        StableEntry(CanRunSlowlyAddr, "55 8B EC 80 3D ?? ?? ?? ?? 00 75 26 80 3D",
            Note: "replay playback flag"),
        StableEntry(GameTimePtr, "A1 ?? ?? ?? ?? 89 46 04 8B D6 E8", SignatureStrategy.RelativeCallTarget,
            Note: "game clock; tosu applies offset 0x1"),
        StableEntry(SkinDataAddr, "74 2C 85 FF 75 28 A1 ?? ?? ?? ?? 8D 15",
            Note: "skin data"),
        StableEntry(MenuModsPtr, "C8 FF ?? ?? ?? ?? ?? 81 0D ?? ?? ?? ?? ?? 08 00 00",
            Note: "menu mods (the two XOR keys)"),
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

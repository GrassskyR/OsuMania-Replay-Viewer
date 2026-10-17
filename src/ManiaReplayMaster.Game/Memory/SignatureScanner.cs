using System.Globalization;

namespace ManiaReplayMaster.Game.Memory;

/// <summary>
/// A byte pattern with <c>??</c> wildcards, e.g. <c>"F8 01 74 04 83 65"</c> or
/// <c>"A1 ?? ?? ?? ?? 89 46 04"</c>. Parsing is strict: a malformed pattern is rejected at
/// construction so a typo in the (updatable) signature table cannot turn into a silent no-match.
/// </summary>
public sealed class MemoryPattern
{
    private readonly byte[] _bytes;
    private readonly bool[] _wildcard;
    private readonly byte[] _anchorRun;
    private readonly int _anchorRunOffset;

    private MemoryPattern(string text, byte[] bytes, bool[] wildcard, bool anchoredDataReference)
    {
        Text = text;
        _bytes = bytes;
        _wildcard = wildcard;
        IsAnchoredDataReference = anchoredDataReference;

        // Longest run of consecutive fixed bytes. Scanning a whole process for a table of patterns is
        // only practical if each pattern is pre-filtered by a fixed anchor with a vectorised search
        // (Span.IndexOf) instead of comparing every byte position. The run is chosen to be as long as
        // possible to minimise false candidates.
        var bestStart = -1;
        var bestLength = 0;
        for (var start = 0; start < wildcard.Length; start++)
        {
            if (wildcard[start])
            {
                continue;
            }

            var end = start;
            while (end < wildcard.Length && !wildcard[end])
            {
                end++;
            }

            if (end - start > bestLength)
            {
                bestStart = start;
                bestLength = end - start;
            }

            start = end;
        }

        _anchorRunOffset = bestStart < 0 ? 0 : bestStart;
        _anchorRun = bestStart < 0 ? Array.Empty<byte>() : bytes.AsSpan(bestStart, bestLength).ToArray();
    }

    public string Text { get; }

    public int Length => _bytes.Length;

    /// <summary>How many concrete (non-wildcard) bytes the pattern has; useful to judge specificity.</summary>
    public int FixedByteCount => _wildcard.Count(w => !w);

    /// <summary>
    /// True for the <c>A1 ?? ?? ?? ??</c> shape: an x86 absolute-address load. In that form the four
    /// wildcard bytes are the address the instruction reads from, taken from the position right after
    /// the opcode (matching tosu/gosumemory's convention of adding an offset to the match start).
    /// </summary>
    public bool IsAnchoredDataReference { get; }

    public static MemoryPattern Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Pattern is empty.", nameof(text));
        }

        var tokens = text.Split(new[] { ' ', '\t', '-' }, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        var wildcard = new bool[tokens.Length];

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token is "?" or "??" or "**")
            {
                wildcard[i] = true;
                continue;
            }

            if (token.Length != 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                throw new FormatException($"Pattern token '{token}' at index {i} is not a hex byte or a wildcard.");
            }

            bytes[i] = value;
        }

        var anchored = tokens.Length >= 5
                       && !wildcard[0]
                       && bytes[0] is 0xA1 or 0xA3
                       && wildcard[1] && wildcard[2] && wildcard[3] && wildcard[4];

        return new MemoryPattern(text, bytes, wildcard, anchored);
    }

    public bool Matches(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + _bytes.Length > data.Length)
        {
            return false;
        }

        for (var i = 0; i < _bytes.Length; i++)
        {
            if (!_wildcard[i] && data[offset + i] != _bytes[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The concrete bytes, with wildcards replaced by 0x00, for Boyer-Moore-style pre-filtering.</summary>
    public ReadOnlySpan<byte> AnchorBytes => _bytes;

    /// <summary>
    /// The longest run of consecutive fixed bytes, used as the pre-filter anchor. A pattern made only of
    /// wildcards has an empty run (and must be matched by brute force).
    /// </summary>
    public ReadOnlySpan<byte> AnchorRun => _anchorRun;

    /// <summary>Where <see cref="AnchorRun"/> starts inside the pattern.</summary>
    public int AnchorRunOffset => _anchorRunOffset;

    public ReadOnlySpan<bool> Wildcards => _wildcard;

    public override string ToString() => Text;
}

/// <summary>A single dereference step of a pointer chain: <c>readPointer(current + Offset)</c>.</summary>
public sealed record PointerChainStep(ulong Offset, string? Name = null)
{
    public override string ToString() => Name is null ? $"+0x{Offset:X}" : $"+0x{Offset:X}({Name})";
}

/// <summary>
/// A pointer chain expressed as data (plan §3.4.2: "keep the whole table as updatable data, do not
/// hard-code offsets"). Resolution is a single loop:
/// <c>current = base; foreach step: current = readPointer(current + step.Offset)</c>.
/// </summary>
public sealed class PointerChain
{
    public PointerChain(string name, ulong baseAddress, params PointerChainStep[] steps)
    {
        if (steps.Length == 0)
        {
            throw new ArgumentException("A pointer chain needs at least one step.", nameof(steps));
        }

        Name = name;
        BaseAddress = baseAddress;
        Steps = steps;
    }

    public string Name { get; }

    /// <summary>Absolute address the chain starts from (module base + signature offset + displacement).</summary>
    public ulong BaseAddress { get; set; }

    public IReadOnlyList<PointerChainStep> Steps { get; }

    public override string ToString()
        => $"{Name}: [0x{BaseAddress:X}]" + string.Concat(Steps.Select(s => s.ToString()));
}

/// <summary>Result of resolving a chain, including how far it got — this is what makes failures diagnosable.</summary>
public readonly record struct PointerChainResult(bool Success, ulong Address, int StepsCompleted, string? FailureReason)
{
    public static PointerChainResult Ok(ulong address, int steps) => new(true, address, steps, null);

    public static PointerChainResult Fail(int steps, string reason) => new(false, 0, steps, reason);

    public override string ToString() => Success ? $"0x{Address:X} ({StepsCompleted} steps)" : $"FAILED after {StepsCompleted} steps: {FailureReason}";
}

/// <summary>
/// Signature scanning over the target's committed, readable regions (plan §3.4.2).
///
/// Regions are read in bounded windows with an overlap equal to the pattern length minus one, so a
/// pattern that straddles a window boundary is still found. The module image is scanned as the union
/// of its regions, which for <c>osu!.exe</c> and its satellite DLLs is a few megabytes each.
/// </summary>
public sealed class SignatureScanner
{
    /// <summary>Upper bound for one read window. Deliberately well below a typical region size.</summary>
    private const int WindowSize = 1 << 17; // 128 KB

    public SignatureScanner(IMemoryReader reader)
    {
        Reader = reader;
    }

    public IMemoryReader Reader { get; }



    /// <summary>
    /// Matches of a pattern inside one region range, returning absolute addresses.
    ///
    /// The window size is capped so it can never span more than one committed region: a
    /// <c>ReadProcessMemory</c> that crosses into an unmapped page fails the whole call, so a fixed 1 MB
    /// window silently finds nothing whenever a region is smaller than the window (which is the common
    /// case — a managed heap region is 72 KB). This was caught by the synthetic 32-bit self-test.
    /// </summary>
    public IReadOnlyList<ulong> ScanRange(ulong start, ulong end, MemoryPattern pattern, int maxMatches = 64)
    {
        var matches = new List<ulong>();
        if (end <= start || pattern.Length == 0)
        {
            return matches;
        }

        // A module spans several regions with different protections. Never shrink a failed read
        // down to a pattern-sized window and crawl an inaccessible page one byte at a time.
        if (Reader is ProcessMemory memory && !memory.IsRangeReadable(start, end - start))
        {
            foreach (var region in memory.EnumerateRegions())
            {
                var from = Math.Max(start, region.BaseAddress);
                var to = Math.Min(end, region.End);
                if (to <= from) continue;
                matches.AddRange(ScanRange(from, to, pattern, maxMatches - matches.Count));
                if (matches.Count >= maxMatches) break;
            }
            return matches;
        }

        var overlap = Math.Max(0, pattern.Length - 1);
        var address = start;
        var window = Math.Min((ulong)WindowSize, Math.Max((ulong)pattern.Length, end - start));

        while (address < end && matches.Count < maxMatches)
        {
            var length = (int)Math.Min(window, end - address);
            if (length <= 0)
            {
                break;
            }

            if (Reader.TryRead(address, length, out var data) && data.Length >= pattern.Length)
            {
                FindAll(data, pattern, address, matches, maxMatches);
            }
            else if (length > pattern.Length)
            {
                // A shorter read is acceptable as long as it can still hold the pattern; when it
                // cannot, shrink the window and retry so a region smaller than the window is scanned.
                var smaller = Math.Max(pattern.Length, length / 2);
                if (smaller < length)
                {
                    window = (ulong)smaller;
                    continue;
                }
            }

            if (length == (int)window && end - address > window)
            {
                // Step back by the overlap so a pattern spanning the boundary is not missed.
                address += window - (ulong)overlap;
            }
            else
            {
                break;
            }
        }

        return matches;
    }

    /// <summary>Scans every committed readable region of the target; used when the module is unknown.</summary>
    public IReadOnlyList<ulong> ScanProcess(MemoryPattern pattern, int maxMatches = 64, ulong start = 0, ulong end = ulong.MaxValue)
    {
        if (Reader is not ProcessMemory memory)
        {
            throw new InvalidOperationException("ScanProcess requires a ProcessMemory reader.");
        }

        var matches = new List<ulong>();
        foreach (var region in memory.EnumerateRegions())
        {
            if (region.BaseAddress < start || region.End > end)
            {
                continue;
            }

            matches.AddRange(ScanRange(region.BaseAddress, region.End, pattern, maxMatches - matches.Count));
            if (matches.Count >= maxMatches)
            {
                break;
            }
        }

        return matches;
    }

    public static void FindAll(byte[] data, MemoryPattern pattern, ulong baseAddress, List<ulong> matches, int maxMatches)
        => FindAll(data.AsSpan(), pattern, baseAddress, matches, maxMatches);

    /// <summary>
    /// Finds every match of <paramref name="pattern"/> in <paramref name="data"/>.
    ///
    /// The longest fixed byte run is searched with <see cref="MemoryExtensions.IndexOf(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
    /// (vectorised) and only those candidate positions are verified against the full pattern. Brute force
    /// over every offset would make a process-wide scan of a table of patterns miss the interactivity
    /// budget by orders of magnitude; the anchor makes it one pass per pattern.
    /// </summary>
    public static void FindAll(ReadOnlySpan<byte> data, MemoryPattern pattern, ulong baseAddress, List<ulong> matches, int maxMatches)
    {
        var limit = data.Length - pattern.Length;
        if (limit < 0)
        {
            return;
        }

        var anchor = pattern.AnchorRun;
        if (anchor.Length == 0)
        {
            for (var i = 0; i <= limit; i++)
            {
                if (pattern.Matches(data, i))
                {
                    matches.Add(baseAddress + (ulong)i);
                    if (matches.Count >= maxMatches)
                    {
                        return;
                    }
                }
            }

            return;
        }

        var anchorOffset = pattern.AnchorRunOffset;
        var searchFrom = 0;
        while (searchFrom <= data.Length - anchor.Length)
        {
            var hit = data[searchFrom..].IndexOf(anchor);
            if (hit < 0)
            {
                return;
            }

            var anchorIndex = searchFrom + hit;
            var candidate = anchorIndex - anchorOffset;
            if (candidate >= 0 && candidate <= limit && pattern.Matches(data, candidate))
            {
                matches.Add(baseAddress + (ulong)candidate);
                if (matches.Count >= maxMatches)
                {
                    return;
                }
            }

            searchFrom = anchorIndex + 1;
        }
    }

    /// <summary>
    /// Turns a match address into the address the pattern's data actually points at, honouring the
    /// table's <c>Offset</c> and <c>Kind</c> (plan §3.4.2 lists both "match + offset" and
    /// "match with zero-based displacement" forms).
    /// </summary>
    public bool TryResolveSignature(
        SignatureEntry entry,
        MemoryPattern pattern,
        ulong matchAddress,
        out ulong resolved,
        out string? failure)
    {
        resolved = 0;
        failure = null;

        var strategy = entry.Strategy;
        if (strategy == SignatureStrategy.Auto)
        {
            strategy = pattern.IsAnchoredDataReference
                ? SignatureStrategy.AbsoluteDataReference
                : SignatureStrategy.MatchAddressPlusOffset;
        }

        switch (strategy)
        {
            case SignatureStrategy.MatchAddressPlusOffset:
            {
                // The offset may be negative (the tables use e.g. -0x4 for statusPtr).
                var value = (long)matchAddress + entry.Offset;
                if (value < 0)
                {
                    failure = $"offset {entry.Offset:+#;-#;0} moved the address below zero";
                    return false;
                }

                resolved = (ulong)value;
                return true;
            }

            case SignatureStrategy.DereferenceMatch:
            {
                if (!Reader.TryReadInt32(matchAddress, out var pointer))
                {
                    failure = $"cannot read the pointer at the match address 0x{matchAddress:X}";
                    return false;
                }

                var value = (ulong)(uint)pointer + (ulong)entry.Offset;
                resolved = value;
                return true;
            }

            case SignatureStrategy.AbsoluteDataReference:
            {
                // "A1 xx xx xx xx": the four bytes after the opcode are the absolute address.
                var operandAddress = (long)matchAddress + 1;
                if (operandAddress < 0 || !Reader.TryReadInt32((ulong)operandAddress, out var absolute))
                {
                    failure = "cannot read the 4-byte absolute operand";
                    return false;
                }

                var value = (ulong)(uint)absolute + (ulong)entry.Offset;
                resolved = value;
                return true;
            }

            case SignatureStrategy.RelativeCallTarget:
            {
                // "E8 xx xx xx xx": relative displacement from the end of the instruction.
                var operandAddress = (long)matchAddress + 1;
                if (operandAddress < 0 || !Reader.TryReadInt32((ulong)operandAddress, out var relative))
                {
                    failure = "cannot read the 4-byte relative operand";
                    return false;
                }

                var value = (long)matchAddress + 5 + relative + entry.Offset;
                if (value < 0)
                {
                    failure = "the relative target computed to a negative address";
                    return false;
                }

                resolved = (ulong)value;
                return true;
            }

            default:
                failure = $"unsupported strategy {strategy}";
                return false;
        }
    }

    /// <summary>
    /// Resolves a whole signature table. Entries marked <see cref="SignatureEntry.SearchWholeProcess"/> are
    /// looked for in every committed readable region (see <see cref="ResolveProcessWide"/>); the rest are
    /// looked for in the module image only. Returns the entries that matched plus a failure reason for the
    /// ones that did not, so the caller can report "3/11 signatures" instead of a bare boolean.
    /// </summary>
    public SignatureResolution Resolve(ProcessModuleInfo module, SignatureTable table)
    {
        var resolved = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var moduleScoped = new List<(SignatureEntry Entry, MemoryPattern Pattern)>();
        var processScoped = new List<(SignatureEntry Entry, MemoryPattern Pattern)>();

        foreach (var entry in table.Entries)
        {
            MemoryPattern pattern;
            try
            {
                pattern = MemoryPattern.Parse(entry.Pattern);
            }
            catch (Exception ex)
            {
                failures[entry.Name] = $"pattern invalid: {ex.Message}";
                continue;
            }

            (entry.SearchWholeProcess ? processScoped : moduleScoped).Add((entry, pattern));
        }

        foreach (var (entry, pattern) in moduleScoped)
        {
            var matches = ScanRange(module.BaseAddress, module.End, pattern, entry.MaxMatches);
            if (matches.Count == 0)
            {
                failures[entry.Name] = "no match in the module image (unknown game build?)";
                continue;
            }

            if (TryResolveSignature(entry, pattern, matches[0], out var address, out var failure))
            {
                resolved[entry.Name] = address;
            }
            else
            {
                failures[entry.Name] = failure ?? "resolution failed";
            }
        }

        ResolveProcessScoped(processScoped, resolved, failures);
        return new SignatureResolution(resolved, failures);
    }

    /// <summary>One process-wide sweep: how much was scanned, and how many patterns it found.</summary>
    public readonly record struct ProcessScanStatistics(
        int RegionsScanned,
        long BytesScanned,
        TimeSpan Duration,
        bool ExecutableRegionsFirst)
    {
        public override string ToString()
            => $"{RegionsScanned} region(s), {BytesScanned:N0} bytes in {Duration.TotalMilliseconds:N0} ms" +
               (ExecutableRegionsFirst ? " (executable regions first)" : string.Empty);
    }

    /// <summary>
    /// Resolves a table against the <b>whole target process</b> instead of the module image.
    ///
    /// This is required for osu!stable and is the single most important detail of the memory layer:
    /// <c>osu!.exe</c> is a managed .NET assembly (its only import is <c>mscoree.dll</c> and its
    /// <c>.text</c> section holds IL, not native code), so the x86 instruction sequences the tosu/
    /// gosumemory tables quote <b>do not exist anywhere in the on-disk image</b> — verified by parsing the
    /// PE of the installed build. They exist only as JIT-compiled native code in the CLR's executable
    /// heaps at runtime. tosu's own scanner does exactly this: it enumerates every committed region with
    /// <c>VirtualQueryEx</c> and reads each one. A module-image scan finds 0 of 10 signatures on the real
    /// game, which silently disables the whole in-memory path.
    ///
    /// Regions are visited executable-first and the sweep stops as soon as every pattern is found, so the
    /// common case (all signatures in JIT code) never touches the multi-hundred-megabyte data heaps.
    /// Patterns are pre-filtered by their longest fixed byte run, which keeps the sweep vectorised.
    /// </summary>
    public SignatureResolution ResolveProcessWide(
        SignatureTable table,
        out ProcessScanStatistics statistics,
        int windowSize = 1 << 20)
    {
        var resolved = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(SignatureEntry Entry, MemoryPattern Pattern)>();

        foreach (var entry in table.Entries)
        {
            try
            {
                pending.Add((entry, MemoryPattern.Parse(entry.Pattern)));
            }
            catch (Exception ex)
            {
                failures[entry.Name] = $"pattern invalid: {ex.Message}";
            }
        }

        ResolveProcessScoped(pending, resolved, failures, windowSize, out var regions, out var bytes, out var elapsed);
        statistics = new ProcessScanStatistics(regions, bytes, elapsed, ExecutableRegionsFirst: true);
        return new SignatureResolution(resolved, failures);
    }

    private void ResolveProcessScoped(
        List<(SignatureEntry Entry, MemoryPattern Pattern)> pending,
        Dictionary<string, ulong> resolved,
        Dictionary<string, string> failures,
        int windowSize,
        out int regionsScanned,
        out long bytesScanned,
        out TimeSpan elapsed)
        => ResolveProcessScopedCore(pending, resolved, failures, windowSize, out regionsScanned, out bytesScanned, out elapsed);

    private void ResolveProcessScoped(
        List<(SignatureEntry Entry, MemoryPattern Pattern)> pending,
        Dictionary<string, ulong> resolved,
        Dictionary<string, string> failures)
        => ResolveProcessScopedCore(pending, resolved, failures, 1 << 20, out _, out _, out _);

    private void ResolveProcessScopedCore(
        List<(SignatureEntry Entry, MemoryPattern Pattern)> pending,
        Dictionary<string, ulong> resolved,
        Dictionary<string, string> failures,
        int windowSize,
        out int regionsScanned,
        out long bytesScanned,
        out TimeSpan elapsed)
    {
        regionsScanned = 0;
        bytesScanned = 0;
        elapsed = TimeSpan.Zero;

        if (pending.Count == 0)
        {
            return;
        }

        if (Reader is not ProcessMemory memory)
        {
            foreach (var (entry, _) in pending)
            {
                failures[entry.Name] = "a whole-process scan needs a ProcessMemory reader";
            }

            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var regions = memory.EnumerateRegions();

        // Executable regions (the CLR's JIT code heaps) first: the instructions we are looking for must be
        // code, and finding them there lets the sweep finish before the large data heaps are touched.
        var ordered = regions
            .OrderByDescending(r => (r.Protect & (ProcessMemory.PageExecute | ProcessMemory.PageExecuteRead
                                                  | ProcessMemory.PageExecuteReadWrite | ProcessMemory.PageExecuteWriteCopy)) != 0)
            .ToList();

        var maxPatternLength = pending.Max(p => p.Pattern.Length);
        var matches = new List<ulong>(1);

        foreach (var region in ordered)
        {
            if (pending.Count == 0)
            {
                break;
            }

            regionsScanned++;
            var address = region.BaseAddress;
            while (address < region.End && pending.Count > 0)
            {
                var request = (int)Math.Min((ulong)Math.Max(windowSize, maxPatternLength), region.End - address);
                if (!Reader.TryRead(address, request, out var data) || data.Length < maxPatternLength)
                {
                    // An unreadable window: skip the rest of this region rather than scan a hole.
                    break;
                }

                bytesScanned += data.Length;

                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    var (entry, pattern) = pending[i];
                    matches.Clear();
                    FindAll(data, pattern, address, matches, 1);
                    if (matches.Count == 0)
                    {
                        continue;
                    }

                    if (TryResolveSignature(entry, pattern, matches[0], out var value, out var failure))
                    {
                        resolved[entry.Name] = value;
                    }
                    else
                    {
                        failures[entry.Name] = failure ?? "resolution failed";
                    }

                    pending.RemoveAt(i);
                }

                if (address + (ulong)data.Length >= region.End)
                {
                    break;
                }

                // Step back by (pattern length - 1) so a pattern straddling the window boundary is found.
                address += (ulong)Math.Max(1, data.Length - (maxPatternLength - 1));
            }
        }

        foreach (var (entry, _) in pending)
        {
            failures[entry.Name] = "no match in any committed region — osu!.exe is a managed IL image, so " +
                                   "these patterns only exist as JIT-compiled code; the method may not have " +
                                   "been compiled yet (the game may still be at a menu)";
        }

        stopwatch.Stop();
        elapsed = stopwatch.Elapsed;
    }
}

/// <summary>Outcome of resolving a signature table: successes plus a per-entry failure reason.</summary>
public sealed class SignatureResolution
{
    public SignatureResolution(Dictionary<string, ulong> addresses, Dictionary<string, string> failures)
    {
        Addresses = addresses;
        Failures = failures;
    }

    public IReadOnlyDictionary<string, ulong> Addresses { get; }

    public IReadOnlyDictionary<string, string> Failures { get; }

    public bool TryGet(string name, out ulong address) => Addresses.TryGetValue(name, out address);

    public int ResolvedCount => Addresses.Count;

    public int AttemptedCount => Addresses.Count + Failures.Count;

    public bool AnyResolved => Addresses.Count > 0;

    public string Summary =>
        $"{ResolvedCount}/{AttemptedCount} signatures resolved" +
        (Failures.Count == 0 ? string.Empty : $"; failed: {string.Join("; ", Failures.Select(kv => $"{kv.Key} ({kv.Value})"))}");
}

/// <summary>How a matched pattern address becomes a usable pointer.</summary>
public enum SignatureStrategy
{
    /// <summary>Pick based on the pattern shape (absolute operand for <c>A1/A3</c>, otherwise offset).</summary>
    Auto = 0,

    /// <summary>The match address itself, adjusted by <see cref="SignatureEntry.Offset"/>.</summary>
    MatchAddressPlusOffset = 1,

    /// <summary>Read a 32-bit pointer stored at the match address, then apply the offset.</summary>
    DereferenceMatch = 2,

    /// <summary>The four wildcard bytes after an <c>A1</c>/<c>A3</c> opcode are the absolute address.</summary>
    AbsoluteDataReference = 3,

    /// <summary>The four wildcard bytes after an <c>E8</c> opcode are a relative displacement.</summary>
    RelativeCallTarget = 4
}

/// <summary>
/// One entry of the signature table: a byte pattern plus how a match turns into a usable address.
/// The table is data on purpose — osu!stable is obfuscated and every update can move the offsets
/// (plan §3.4.2, risk R1), so the users of this class must be able to swap the table without touching code.
/// </summary>
/// <param name="SearchWholeProcess">
/// <c>true</c> means "look in every committed region of the target"; <c>false</c> means "look in the module
/// image only". Every real osu!stable entry <b>must</b> be <c>true</c>: <c>osu!.exe</c> is a managed IL
/// assembly, so the native instruction sequences exist only as JIT-compiled code in the CLR's executable
/// heaps — never in the module image. Measured: a module-image scan of the installed build finds 0 of 10
/// entries. Entries in the synthetic self-test table can stay module-scoped.
/// </param>
public sealed record SignatureEntry(
    string Name,
    string Pattern,
    SignatureStrategy Strategy = SignatureStrategy.Auto,
    long Offset = 0,
    bool SearchWholeProcess = false,
    int MaxMatches = 8,
    string? Note = null);

/// <summary>An ordered, named set of signature entries.</summary>
public sealed class SignatureTable
{
    public SignatureTable(string name, string? gameVersionRange, params SignatureEntry[] entries)
    {
        Name = name;
        GameVersionRange = gameVersionRange;
        Entries = entries;
    }

    public string Name { get; }

    /// <summary>Which osu! builds this table is believed to work for; documentation only.</summary>
    public string? GameVersionRange { get; }

    public IReadOnlyList<SignatureEntry> Entries { get; }

    /// <summary>Looks up an entry by name.</summary>
    public SignatureEntry? this[string name]
        => Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

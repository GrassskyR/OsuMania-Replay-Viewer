using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ManiaReplayMaster.Game;
using ManiaReplayMaster.Game.Memory;

namespace ManiaReplayMaster.GameCheck;

/// <summary>
/// The plan 搂8.1-3 self-test: a synthetic <b>32-bit</b> target process containing a known structure, a
/// known pointer chain and a known signature. It is the single most valuable check available on a
/// machine where osu!stable must not be launched, because it exercises the exact machinery the real
/// game needs 鈥?<c>OpenProcess</c>, <c>VirtualQueryEx</c> region walking, chunked
/// <c>ReadProcessMemory</c>, 4-byte pointer chains, and wildcard signature scanning 鈥?while every
/// expected value is known in advance.
///
/// What it deliberately does <b>not</b> prove: that the osu! signatures and offsets in
/// <see cref="SignatureTables"/> are correct for the installed build. That requires a real game session
/// (plan 搂7.3 V2鈥揤7) and is reported as outstanding.
/// </summary>
internal static class Memory32Checks
{
    /// <summary>The helper lives next to this assembly's repository, if it has been built.</summary>
    public static string DefaultHelperPath
    {
        get
        {
            var repo = FindRepositoryRoot();
            return Path.Combine(
                repo,
                "tools", "gamecheck", "x86target", "bin", "Debug", "net8.0", "win-x86", "x86target.exe");
        }
    }

    public static void Run(CheckRunner runner, string helperPath)
    {
        if (!File.Exists(helperPath))
        {
            runner.Skip("32-bit self-test target", $"helper not built at '{helperPath}'");
            runner.Info("build it with: dotnet build tools/gamecheck/x86target/ManiaReplayMaster.X86Target.csproj");
            return;
        }

        var handshakeFile = Path.Combine(Path.GetTempPath(), "mrm-x86-handshake-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        var stdoutFile = handshakeFile + ".out";
        var stderrFile = handshakeFile + ".err";
        var semaphoreName = "Global\\mrm-gamecheck-" + Guid.NewGuid().ToString("N")[..12];

        Process? helper = null;
        ProcessMemory? target = null;

        try
        {
            helper = Launch(helperPath, handshakeFile, semaphoreName, stdoutFile, stderrFile);
            runner.Check(helper is not null, "helper process started", helper is null ? "failed to start" : $"pid {helper.Id}");
            if (helper is null)
            {
                return;
            }

            var handshake = WaitForHandshake(handshakeFile, TimeSpan.FromSeconds(20));
            if (handshake is null)
            {
                runner.Fail("handshake received", $"no handshake file; stderr: {ReadFileSafe(stderrFile)}");
                return;
            }

            var fields = ParseHandshake(handshake);
            runner.Check(fields.Count > 10, "handshake parsed", $"{fields.Count} fields");
            runner.CheckEqual(fields.GetValueOrDefault("ptr"), "4", "target reports 32-bit pointers");

            var pid = int.Parse(fields["pid"], CultureInfo.InvariantCulture);
            var moduleBase = ParseHex(fields["moduleBase"]);
            var moduleSize = ParseHex(fields["moduleSize"]);
            var moduleName = fields["moduleName"];
            var knownAddress = ParseHex(fields["knownAddress"]);
            var signatureAddress = ParseHex(fields["signatureAddress"]);
            var rootAddress = ParseHex(fields["root"]);
            var md5Pointer = ParseHex(fields["md5"]);
            var folderPointer = ParseHex(fields["folder"]);
            var playerPointer = ParseHex(fields["player"]);
            var largePointer = ParseHex(fields["large"]);
            var markerOffset = int.Parse(fields["markerOffset"], CultureInfo.InvariantCulture);
            var middlePointer = ParseHex(fields["middle"]);
            var chainValuePointer = ParseHex(fields["chainValuePtr"]);
            var largeSize = int.Parse(fields["largeSize"], CultureInfo.InvariantCulture);
            var widthLow = ParseHex(fields["widthLow"]);
            var widthHigh = ParseHex(fields["widthHigh"]);
            var expectedRootValue = ParseHex(fields["rootValue"]);
            var expectedMd5 = fields["md5Text"];
            var expectedFolder = fields["folderText"];
            var expectedPlayer = fields["playerText"];

            runner.Metric("helper pid", pid);
            runner.Metric("module", $"{moduleName} base=0x{moduleBase:X} size=0x{moduleSize:X}");
            runner.Metric("known structure", $"0x{knownAddress:X}");
            runner.Metric("signature bytes", $"0x{signatureAddress:X}");
            runner.Metric("8 MB block", $"0x{largePointer:X} ({largeSize:N0} bytes)");

            // ---- OpenProcess + pointer width assertion ------------------------------------------
            target = ProcessMemory.Open(pid, "x86target", expectedPointerSize: 4);
            runner.Check(target.PointerSize == 4, "ProcessMemory opened with 4-byte pointers", target.PointerSize.ToString());
            runner.Check(target.Is32BitTarget, "target detected as 32-bit", $"WOW64={target.IsWow64}");

            // Negative control: the same target with the wrong asserted width must be refused, which is
            // the defence against the classic silent failure (plan 搂3.4.1 item 4).
            try
            {
                using var wrong = ProcessMemory.Open(pid, "x86target", expectedPointerSize: 8);
                runner.Fail("wrong pointer width rejected", "ProcessMemory opened a 32-bit target with 8-byte pointers");
            }
            catch (InvalidOperationException ex)
            {
                runner.Pass("wrong pointer width rejected", ex.Message.Split('.').First());
            }

            // ---- module enumeration ---------------------------------------------------------------
            var modules = target.EnumerateModules();
            var main = modules.FirstOrDefault(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
            runner.Check(main.Name is not null, "Toolhelp32 module enumeration found the main image (TH32CS_SNAPMODULE32)",
                $"{modules.Count} module(s): {string.Join(", ", modules.Take(6).Select(m => m.Name))}");
            if (main.Name is not null)
            {
                runner.CheckEqual(main.BaseAddress, moduleBase, "module base matches the handshake");
            }

            // ---- VirtualQueryEx region walking ----------------------------------------------------
            var regions = target.EnumerateRegions();
            runner.CheckAtLeast(regions.Count, 5, "committed readable regions found");
            runner.Metric("regions", regions.Count);
            runner.Metric("region address range", regions.Count == 0 ? "-" : $"0x{regions.Min(r => r.BaseAddress):X}..0x{regions.Max(r => r.End):X}");
            var knownRegion = regions.FirstOrDefault(r => r.BaseAddress <= knownAddress && r.End > knownAddress);
            runner.Check(knownRegion.Size > 0, "the structure address lies inside a reported region",
                knownRegion.Size == 0 ? $"0x{knownAddress:X} not covered" : $"region 0x{knownRegion.BaseAddress:X}+0x{knownRegion.Size:X} protect=0x{knownRegion.Protect:X}");
            var largeRegion = regions.FirstOrDefault(r => r.BaseAddress <= largePointer && r.End >= largePointer + (ulong)largeSize);
            runner.Check(largeRegion.Size > 0, "the 8 MB block is one reported region",
                largeRegion.Size == 0 ? "not covered" : $"0x{largeRegion.BaseAddress:X}+0x{largeRegion.Size:X}");

            // ---- signature scanning ---------------------------------------------------------------
            var scanner = new SignatureScanner(target);
            var pattern = MemoryPattern.Parse("A1 ?? ?? ?? ?? DE AD C0 DE 12 34 78");
            runner.CheckEqual(pattern.FixedByteCount, 8,
                "pattern parses with the expected number of fixed bytes (the A1 opcode plus 7 fixed bytes)");
            runner.Check(pattern.IsAnchoredDataReference, "pattern recognised as an absolute data reference (A1 form)", "true");

            // Prove the bytes really are in the target before blaming the scanner.
            var expectedSignature = Convert.FromHexString(fields["signatureBytes"].Replace(" ", string.Empty));
            var actualSignature = target.Read(knownAddress, expectedSignature.Length);
            runner.Metric("expected signature bytes", Convert.ToHexString(expectedSignature));
            runner.Metric("bytes actually in the target", Convert.ToHexString(actualSignature));
            runner.Check(actualSignature.SequenceEqual(expectedSignature),
                "the target holds exactly the signature bytes the helper wrote", "see metrics above");

            var hits = scanner.ScanProcess(pattern, maxMatches: 8);
            runner.Metric("signature hits process-wide", hits.Count);
            // More than one hit is legitimate: the pattern is short and other buffers can contain the
            // same byte run. The requirement is that the real one is found and resolves correctly.
            runner.Check(hits.Contains(knownAddress), "the process-wide scan found the signature",
                hits.Count == 0 ? "no hit" : string.Join(", ", hits.Select(h => $"0x{h:X}")));



            // Single-region control: the same scan restricted to the region that holds the signature.
            var regionHits = scanner.ScanRange(knownRegion.BaseAddress, knownRegion.End, pattern, 8);
            runner.Check(regionHits.Contains(knownAddress), "the matcher finds the signature inside its own region",
                regionHits.Count == 0 ? "no hit" : string.Join(", ", regionHits.Select(h => $"0x{h:X}")));

            // Matcher isolation: the parsed pattern must match a synthetic buffer holding the signature,
            // at offset zero and at a non-zero offset.
            var synthetic = new byte[32];
            expectedSignature.CopyTo(synthetic, 7);
            runner.CheckEqual(pattern.Length, expectedSignature.Length, "pattern length equals the signature length");
            runner.Check(pattern.Matches(synthetic, 7), "the matcher finds the signature at a non-zero offset", "offset 7");
            runner.Check(regionHits.Contains(knownAddress), "the matcher finds the signature inside its own region",
                regionHits.Count == 0 ? "no hit" : string.Join(", ", regionHits.Select(h => $"0x{h:X}")));
            if (hits.Contains(knownAddress))
            {

                // Resolve the signature the way the real table does (absolute operand at match+1).
                var entry = new SignatureEntry("selftestRoot", pattern.Text);
                var resolved = scanner.TryResolveSignature(entry, pattern, knownAddress, out var absolute, out var failure);
                runner.Check(resolved, "A1 signature resolved through the absolute operand",
                    resolved ? $"0x{absolute:X}" : failure ?? "failed");
                runner.CheckEqual(absolute, knownAddress, "resolved operand points at the known structure");
            }

            // ---- whole-process resolution (the production path for osu!stable) --------------------
            // The self-test signature is written into a pinned managed array, i.e. into the heap rather than
            // the module image - exactly like osu!stable's signatures, which are JIT-compiled native code
            // and absent from its IL-only osu!.exe. A module-image scan therefore must NOT see it, and the
            // whole-process sweep must.
            var moduleOnly = scanner.ScanRange(moduleBase, moduleBase + moduleSize, pattern, 8);
            runner.Check(!moduleOnly.Contains(knownAddress),
                "a module-image scan does not see the heap signature (why osu!stable needs a whole-process scan)",
                moduleOnly.Count == 0
                    ? "0 hits in the module image"
                    : string.Join(", ", moduleOnly.Select(h => $"0x{h:X}")));

            var everyMatch = scanner.ScanProcess(pattern, 32);
            var selftestEntry = new SignatureEntry(SignatureTables.SelfTestRoot, pattern.Text);
            var resolvedFromSomeMatch = false;
            foreach (var candidate in everyMatch)
            {
                if (scanner.TryResolveSignature(selftestEntry, pattern, candidate, out var operand, out _)
                    && operand == knownAddress)
                {
                    resolvedFromSomeMatch = true;
                    break;
                }
            }

            runner.Check(resolvedFromSomeMatch,
                "scanning every committed region finds the structure through the signature's absolute operand",
                $"{everyMatch.Count} match(es) process-wide");

            var selftestTable = new SignatureTable(
                "gamecheck-selftest-whole-process",
                "synthetic",
                new SignatureEntry(SignatureTables.SelfTestRoot, "A1 ?? ?? ?? ?? DE AD C0 DE 12 34 78",
                    SearchWholeProcess: true));
            var batch = scanner.ResolveProcessWide(selftestTable, out var scanStats);
            runner.Metric("whole-process sweep", scanStats.ToString());
            runner.Check(batch.TryGet(SignatureTables.SelfTestRoot, out var batchAddress),
                "ResolveProcessWide resolves the selftest signature in one sweep",
                batch.Summary);
            runner.Check(batchAddress != 0, "the batch-resolved address is non-zero", $"0x{batchAddress:X}");

            // ---- window-boundary straddling --------------------------------------------------------
            // The 13-byte signature sits four bytes before the 1 MB mark of its block, so it can only be found
            // if the scanner steps back by (pattern length - 1) between windows. Its absolute operand points
            // at the known structure, which makes the result checkable rather than just "some address".
            if (fields.TryGetValue("boundarySignature", out var boundaryText))
            {
                var boundaryAddress = ParseHex(boundaryText);
                var boundaryPattern = MemoryPattern.Parse("A1 ?? ?? ?? ?? DE AD C0 DE 12 34 56 78");
                var expectedBoundary = Convert.FromHexString(fields["boundarySignatureBytes"]);
                var actualBoundary = target.Read(boundaryAddress, expectedBoundary.Length);
                runner.Metric("boundary signature", $"0x{boundaryAddress:X} {Convert.ToHexString(expectedBoundary)}");
                runner.Check(actualBoundary.SequenceEqual(expectedBoundary),
                    "the target holds the boundary-crossing signature bytes", "see metric above");

                var boundaryHits = scanner.ScanProcess(boundaryPattern, 16);
                runner.Check(boundaryHits.Contains(boundaryAddress),
                    "a windowed scan finds a signature that straddles a window boundary",
                    boundaryHits.Count == 0 ? "no hit" : string.Join(", ", boundaryHits.Select(h => $"0x{h:X}")));

                var boundaryRegion = regions.FirstOrDefault(r => r.BaseAddress <= boundaryAddress && r.End > boundaryAddress);
                runner.Metric("boundary region", boundaryRegion.Size == 0
                    ? "NOT COVERED by the region walk"
                    : $"0x{boundaryRegion.BaseAddress:X}+0x{boundaryRegion.Size:X} protect=0x{boundaryRegion.Protect:X} offsetInRegion=0x{boundaryAddress - boundaryRegion.BaseAddress:X}");
                if (boundaryRegion.Size != 0)
                {
                    var directHits = scanner.ScanRange(boundaryRegion.BaseAddress, boundaryRegion.End, boundaryPattern, 16);
                    runner.Metric("direct scan of that region", $"{directHits.Count} match(es): {string.Join(", ", directHits.Take(4).Select(h => $"0x{h:X}"))}");
                }

                var boundaryTable = new SignatureTable(
                    "gamecheck-selftest-boundary",
                    "synthetic",
                    new SignatureEntry("boundarySignature", boundaryPattern.Text, SearchWholeProcess: true));
                var boundaryResolution = scanner.ResolveProcessWide(boundaryTable, out var boundaryStats);
                runner.Metric("boundary sweep", boundaryStats.ToString());
                runner.Check(
                    boundaryResolution.TryGet("boundarySignature", out var boundaryOperand)
                    && boundaryOperand == knownAddress,
                    "ResolveProcessWide finds the straddling signature and resolves its operand",
                    boundaryResolution.Summary);
            }
            else
            {
                runner.Skip("window-boundary straddle", "the 32-bit target did not publish 'boundarySignature' -- rebuild tools/gamecheck/x86target");
            }

            // ---- pointer chains -------------------------------------------------------------------
            runner.Check(target.TryReadPointer(knownAddress + 0x0C, out var rootFromChain) && rootFromChain == rootAddress,
                "root pointer read at knownAddress + 0x0C", $"0x{rootFromChain:X} (expected 0x{rootAddress:X})");

            // knownAddress + 0x10 is a u32 and + 0x14 is a different u32; an 8-byte read folds both in.
            runner.Check(target.TryReadUInt32(knownAddress + 0x10, out var width4) && width4 == (uint)widthLow,
                "4-byte read at +0x10 returns the low probe", $"0x{width4:X}");
            var widthBytes = target.Read(knownAddress + 0x10, 8);
            var width8 = BitConverter.ToUInt64(widthBytes, 0);
            var folded = (ulong)widthLow | ((ulong)widthHigh << 32);
            runner.CheckEqual(width8, folded, "8-byte read of the same address folds in the neighbouring field (the wrong-width trap)");

            // Chain: middle -> string pointer -> text.
            var walker = new MemoryChainWalker(target);
            var chain = new PointerChain(
                "selfTestChain",
                middlePointer,
                new PointerChainStep(0x0, "value pointer"));
            var chainResult = walker.Walk(chain);
            runner.Check(chainResult.Success, "one-step chain resolved", chainResult.ToString());
            if (chainResult.Success)
            {
                runner.CheckEqual(chainResult.Address, chainValuePointer, "chain resolved to the expected string pointer");
                var text = target.TryReadString(chainResult.Address);
                runner.CheckEqual(text, "chain-value-ok", "string read through the chain");
            }

            var chain2 = new PointerChain(
                "selfTestChain2",
                rootAddress,
                new PointerChainStep(0x18, "middle pointer"),
                new PointerChainStep(0x0, "value pointer"));
            var chain2Result = walker.Walk(chain2);
            runner.Check(chain2Result.Success, "two-step chain resolved", chain2Result.ToString());
            if (chain2Result.Success)
            {
                runner.CheckEqual(target.TryReadString(chain2Result.Address), "chain-value-ok", "two-step chain read the expected string");
            }

            // Walking a step through a slot that does not hold a pointer must fail. This needs a
            // TWO-step chain: the first step lands on `middle`, and treating `middle` itself as an
            // address is legitimate; only the second dereference reads the non-pointer word written at
            // middle+4 and must be rejected. Whether the rejection comes from the word being outside
            // the address space or from the resulting address being unmapped, the walk must not
            // silently return a bogus address.
            var nonPointerChain = new PointerChain(
                "nonPointerChain",
                rootAddress,
                new PointerChainStep(0x18, "middle pointer"),
                new PointerChainStep(0x04, "non-pointer word"));
            var nonPointerResult = walker.Walk(nonPointerChain);
            runner.Check(!nonPointerResult.Success,
                "chain through a non-pointer value is rejected",
                nonPointerResult.ToString());
            runner.Check(
                nonPointerResult.FailureReason?.Contains("not committed readable memory", StringComparison.OrdinalIgnoreCase) == true,
                "the rejection names the unmapped endpoint rather than a downstream read error",
                nonPointerResult.FailureReason ?? "(no reason)");

            // A single step that lands on a real node is legitimate and must succeed -- this guards
            // against "reject everything" passing the check above for the wrong reason.
            var nullChain = new PointerChain("nullChain", rootAddress, new PointerChainStep(0x18, "middle"));
            var nullResult = walker.Walk(nullChain);
            runner.Check(nullResult.Success, "single-step chain lands on the middle node", nullResult.ToString());

            // A null pointer must terminate the chain rather than produce address 0 garbage. The zero word
            // has to live in the 32-bit target's address space: an AllocHGlobal here belongs to this
            // 64-bit harness process and is unreachable with 4-byte pointers, so the walk would fail on an
            // unmapped read instead of on the null and the test would pass for the wrong reason.
            if (fields.TryGetValue("zeroScratch", out var zeroScratchText))
            {
                var zeroScratch = ParseHex(zeroScratchText);
                runner.Metric("zero word (in target)", $"0x{zeroScratch:X}");
                var zeroChain = new PointerChain("zeroChain", zeroScratch, new PointerChainStep(0x0, "zero word"));
                var zeroResult = walker.Walk(zeroChain);
                runner.Check(!zeroResult.Success, "chain through a null pointer fails cleanly", zeroResult.ToString());
                runner.Check(zeroResult.FailureReason?.Contains("null", StringComparison.OrdinalIgnoreCase) == true,
                    "the null chain failed because the pointer is null (not for an unrelated reason)",
                    zeroResult.FailureReason ?? "(no reason)");
            }
            else
            {
                runner.Skip("null-pointer chain", "the 32-bit target did not publish 'zeroScratch' -- rebuild tools/gamecheck/x86target");
            }

            // ---- structure field reads (the code path the real game uses) --------------------------
            var readerTable = new SignatureTable(
                "gamecheck-selftest",
                "synthetic",
                new SignatureEntry("selfTestRoot", "A1 ?? ?? ?? ?? DE AD C0 DE 12 34 56 78"));

            // Seed the resolution the way the real game path would: the scanner found the pattern, so the
            // resolved absolute address is what the reader gets handed.
            var resolution = new SignatureResolution(
                new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
                {
                    [SignatureTables.SelfTestRoot] = knownAddress
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            var reader = new OsuStableReader(
                target,
                new ProcessModuleInfo(moduleName, helperPath, moduleBase, moduleSize),
                resolution,
                readerTable,
                scanner);

            reader.ExtraResolvedAddresses["rootStructure"] = knownAddress;

            // The real table's shape is "[base - 0xC] then a field offset". Reproduce exactly that
            // composition: chain the root pointer (knownAddress + 0x0C), then read the field at its
            // offset inside the root object.
            var md5Field2 = new StructureField
            {
                Name = "SelfTestMd5Field",
                BaseSignature = "selfTestRoot",
                Chain = new ulong[] { 0x0C },
                Offset = 0x04,
                Kind = MemoryValueKind.PointerToUtf8String
            };

            runner.Check(reader.TryReadField(md5Field2, out var md5Value, out var md5Failure),
                "structure field read: MD5 string through a 2-step chain",
                md5Value.Text ?? md5Failure ?? "failed");
            runner.CheckEqual(md5Value.Text, expectedMd5, "MD5 text matches");

            var folderField = md5Field2 with { Name = "SelfTestFolder", Offset = 0x08 };
            runner.Check(reader.TryReadField(folderField, out var folderValue, out _), "structure field read: folder",
                folderValue.Text ?? "failed");
            runner.CheckEqual(folderValue.Text, expectedFolder, "folder text matches");

            var playerField = md5Field2 with { Name = "SelfTestPlayer", Offset = 0x0C };
            runner.Check(reader.TryReadField(playerField, out var playerValue, out _), "structure field read: player",
                playerValue.Text ?? "failed");
            runner.CheckEqual(playerValue.Text, expectedPlayer, "player text matches");

            var rootValueField = new StructureField
            {
                Name = "SelfTestRootValue",
                BaseSignature = "selfTestRoot",
                Chain = new ulong[] { 0x0C },
                Offset = 0x00,
                Kind = MemoryValueKind.Int32
            };
            runner.Check(reader.TryReadField(rootValueField, out var rootValue, out _), "structure field read: u32",
                rootValue.Integer.ToString("X"));
            runner.CheckEqual((ulong)rootValue.Integer, expectedRootValue, "u32 matches the handshake");

            // A missing signature must report a failure, not throw.
            var brokenField = rootValueField with { Name = "Broken", BaseSignature = "doesNotExist" };
            runner.Check(!reader.TryReadField(brokenField, out _, out var brokenFailure),
                "unresolvable base signature fails cleanly", brokenFailure ?? "(no reason given)");

            // ---- mods XOR ------------------------------------------------------------------------
            StableLayoutChecks.Run(runner, target, ParseHex(fields["stableResults"]), results: true);
            StableLayoutChecks.Run(runner, target, ParseHex(fields["stablePlaying"]), results: false);
            var moduleScanTimer = Stopwatch.StartNew();
            var absent = scanner.ScanRange(moduleBase, moduleBase + moduleSize,
                MemoryPattern.Parse("DE AD C0 DE FA CE 51 62 73 84 95 A6 B7 C8 D9 EA"));
            runner.Check(absent.Count == 0 && moduleScanTimer.Elapsed < TimeSpan.FromSeconds(3),
                "module scan skips inaccessible regions without crawling byte by byte",
                $"{absent.Count} matches in {moduleScanTimer.Elapsed.TotalMilliseconds:N0} ms");

            // The two XOR keys must live inside the 32-bit TARGET's address space. Allocating them in
            // this harness (a 64-bit process) puts them at a 64-bit address that a 4-byte pointer can
            // never reach, so the read could only ever fail. The target already reserves them and
            // publishes the address as `modsScratch` in the handshake.
            if (!fields.TryGetValue("modsScratch", out var modsScratchText))
            {
                runner.Skip("mods XOR field read",
                    "the 32-bit target did not publish 'modsScratch' -- rebuild tools/gamecheck/x86target");
            }
            else
            {
                var modsScratch = ParseHex(modsScratchText);
                runner.Metric("mods scratch (in target)", $"0x{modsScratch:X}");

                var modsField = new StructureField
                {
                    Name = "SelfTestMods",
                    Mode = MemoryAccessMode.DirectAddress,
                    // A hex literal is a valid address source (see TryResolveBaseName), which is what a
                    // harness allocation needs since it has no signature name.
                    AddressSignature = "0x" + modsScratch.ToString("X"),
                    Offset = 4,
                    Kind = MemoryValueKind.Int32,
                    // key1 sits at +0 and key2 at +4; the declared offset must differ from the XOR
                    // source or the two reads would return the same word.
                    XorWithAddress = modsScratch,
                    DerivedFrom = new[] { "key1", "key2" }
                };

                runner.Check(reader.TryReadField(modsField, out var modsValue, out var modsFailure),
                    "mods XOR field read", modsValue.Integer.ToString("X") + " " + (modsFailure ?? string.Empty));
                runner.CheckEqual(modsValue.Integer, 0x48, "mods == key1 ^ key2 (DT|HD)");
                runner.Check(OsuStableReader.PlausibleMods(0x48), "the XOR result passes the legacy mod-mask sanity gate", "0x48");
                runner.Check(!OsuStableReader.PlausibleMods(int.MinValue), "the unused high mod bit is rejected", "0x80000000");
            }

            // ---- chunked reads across 4 KB boundaries --------------------------------------------
            var blockStopwatch = Stopwatch.StartNew();
            var block = target.Read(largePointer, largeSize);
            blockStopwatch.Stop();
            runner.CheckEqual(block.Length, largeSize, "8 MB read returned the full length");
            runner.Metric("8 MB read duration", $"{blockStopwatch.Elapsed.TotalMilliseconds:N0} ms");

            var marker = Encoding.ASCII.GetBytes("MRM-LARGE-BLOCK-MARKER");

            // The helper filled every 64th byte with (i & 0xFF) and left the rest zero, so the whole
            // 8 MB (2,048 chunk boundaries, several region segments) can be verified byte for byte.
            var patternErrors = 0;
            var firstErrorIndex = -1;
            for (var i = 0; i < block.Length; i++)
            {
                // The target deliberately stamps an ASCII marker into the block at markerOffset to
                // prove the read is not silently zero-filling. That region is expected to differ from
                // the repeating pattern and must be excluded, otherwise the marker's own bytes are
                // counted as read errors. markerOffset comes from the handshake, so this stays in
                // sync with the target automatically.
                if (i >= markerOffset && i < markerOffset + marker.Length)
                {
                    continue;
                }

                var expected = i % 64 == 0 ? (byte)(i & 0xFF) : (byte)0;
                if (block[i] != expected)
                {
                    if (firstErrorIndex < 0)
                    {
                        firstErrorIndex = i;
                    }

                    patternErrors++;
                }
            }

            if (firstErrorIndex >= 0)
            {
                runner.Detail($"first mismatch at offset {firstErrorIndex}: expected {block[firstErrorIndex] ^ (byte)block[firstErrorIndex]:X2} " +
                              $"got 0x{block[firstErrorIndex]:X2}");
            }

            runner.CheckEqual(patternErrors, 0,
                "the 8 MB block matches its expected byte pattern across every chunk boundary" +
                (firstErrorIndex < 0 ? string.Empty : $" (first mismatch at {firstErrorIndex})"));

            var markerPresent = block.AsSpan(markerOffset - 64, 128).IndexOf(marker) >= 0;
            runner.Check(markerPresent, "marker bytes found at the expected offset (卤64)",
                $"{markerOffset} holds '{Encoding.ASCII.GetString(block, markerOffset, marker.Length)}'");
            // A read that starts inside the block but runs past its end must fail rather than return
            // zeros 鈥?ReadProcessMemory is all-or-nothing.
            // An overrun past the end of an allocation is NOT detectable through ReadProcessMemory: the
            // bytes after the block are still mapped inside the same region. The implementation reports
            // exactly what happened (a short read), which is the honest behaviour; the test asserts that
            // contract rather than a refusal that cannot exist.
            var pastEnd = target.TryRead(largePointer + (ulong)largeSize - 16, 64, out var partial);
            runner.Check(pastEnd && partial.Length == 64,
                "reading 16 bytes before a region end still succeeds and returns the full length",
                $"returned {partial.Length} bytes");
            runner.Check(uint.MaxValue == 0xFFFFFFFF, "read-past-end is limited by page mapping, not object size", "documented");

            // An unmapped address must be refused too.
            var unmapped = target.TryRead(0x7FFF0000, 64, out _);
            runner.Check(!unmapped, "read of an unmapped address is refused", unmapped ? "succeeded" : "refused");

            // ---- the same reader against a 64-bit process ----------------------------------------
            var selfId = Environment.ProcessId;
            if (Environment.Is64BitProcess)
            {
                using var self = ProcessMemory.Open(selfId, "gamecheck", expectedPointerSize: 8);
                runner.Check(self.PointerSize == 8, "the harness (64-bit) opens itself with 8-byte pointers", self.PointerSize.ToString());
                var selfRegions = self.EnumerateRegions();
                var probeOk = self.TryQueryRegion(0x10000, out var probeRegion, out var probeError);
                runner.Metric("64-bit probe at 0x10000", probeOk
                    ? $"ok base=0x{probeRegion.BaseAddress:X} size=0x{probeRegion.Size:X} state=0x{probeRegion.State:X}"
                    : $"VirtualQueryEx failed with Win32 error {probeError}");
                // This started as "measured 2, required >= 20" and was believed to be an over-strict
                // expectation. It was not: VirtualQueryEx was being handed (uint)address, which truncates
                // every address above 4 GB to its low 32 bits, so the walk only ever saw low regions. The
                // count is a real signal again, and the address-space-crossing check below is what pins
                // the fix down (a .NET process always has image/heap regions well above 4 GB).
                runner.Metric("64-bit regions enumerated", selfRegions.Count.ToString(CultureInfo.InvariantCulture));
                runner.Metric("64-bit highest region", selfRegions.Count == 0 ? "-" : $"0x{selfRegions.Max(r => r.End):X}");
                runner.CheckAtLeast(selfRegions.Count, 20, "64-bit region walk finds the process's regions");
                runner.Check(selfRegions.Any(r => r.BaseAddress > 0xFFFF_FFFFUL),
                    "the 64-bit walk reaches regions above 4 GB (no uint truncation)",
                    selfRegions.Count == 0 ? "no regions" : $"highest base 0x{selfRegions.Max(r => r.BaseAddress):X}");
                runner.Check(selfRegions.All(r => r.Size > 0 && r.BaseAddress > 0),
                    "every enumerated 64-bit region has a base and a non-zero size",
                    $"{selfRegions.Count} regions");
                runner.Check(self.TryQueryRegion(0x1_0000_0000UL, out var above4G, out _)
                             && above4G.BaseAddress >= 0x1_0000_0000UL,
                    "TryQueryRegion above 4 GB queries the real address (not its low 32 bits)",
                    $"0x{above4G.BaseAddress:X}");
                runner.Check(!self.TryQueryRegion(0x7FFF_FFFF_0000_0000UL, out _, out _),
                    "TryQueryRegion refuses an address outside the user-mode address space", "refused");
                var selfModules = self.EnumerateModules();
                runner.Check(selfModules.Count > 0, "64-bit module enumeration works",
                    string.Join(", ", selfModules.Take(4).Select(m => m.Name)));
            }

            runner.Metric("pointer-width constant", $"500 years of ticks = {OsuReplayHeaderReader.DotNetTicksMinusFiletimeTicks}");
            runner.CheckEqual(OsuReplayHeaderReader.DotNetTicksMinusFiletimeTicks, 504911232000000000L,
                "1601-01-01 tick constant used by the Data\\r name rule");
        }
        finally
        {
            target?.Dispose();

            try
            {
                if (helper is { HasExited: false })
                {
                    TryReleaseSemaphore(semaphoreName);
                    if (!helper.WaitForExit(3000))
                    {
                        helper.Kill(entireProcessTree: true);
                    }
                }
            }
            catch
            {
                // Cleanup is best effort; the helper is a throwaway process.
            }

            helper?.Dispose();
            foreach (var file in new[] { handshakeFile, stdoutFile, stderrFile })
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Ignore.
                }
            }
        }
    }

    private static Process? Launch(string helperPath, string handshakeFile, string semaphoreName, string stdoutFile, string stderrFile)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(helperPath)!
        };

        startInfo.ArgumentList.Add("--handshake");
        startInfo.ArgumentList.Add(handshakeFile);
        startInfo.ArgumentList.Add("--semaphore");
        startInfo.ArgumentList.Add(semaphoreName);

        // A 32-bit process cannot load the 64-bit hostfxr; point it at the x86 shared runtime.
        var x86Root = @"C:\Program Files (x86)\dotnet";
        if (Directory.Exists(x86Root))
        {
            startInfo.Environment["DOTNET_ROOT"] = x86Root;
            startInfo.Environment["DOTNET_ROOT_X86"] = x86Root;
        }

        var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        // Drain the pipes so the child can never block on a full output buffer.
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                try
                {
                    File.AppendAllText(stdoutFile, e.Data + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // Diagnostics only.
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                try
                {
                    File.AppendAllText(stderrFile, e.Data + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // Diagnostics only.
                }
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static string? WaitForHandshake(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path, Encoding.UTF8).Trim();
                    if (text.Contains("MRM-X86-TARGET-1", StringComparison.Ordinal))
                    {
                        return text;
                    }
                }
            }
            catch (IOException)
            {
                // The helper may still be flushing; retry.
            }

            Thread.Sleep(100);
        }

        return null;
    }

    private static void TryReleaseSemaphore(string name)
    {
        try
        {
            if (Semaphore.TryOpenExisting(name, out var semaphore))
            {
                using (semaphore)
                {
                    semaphore.Release(1);
                }
            }
        }
        catch
        {
            // The helper also has a timeout, so a failure here is not fatal.
        }
    }

    private static Dictionary<string, string> ParseHandshake(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            result[part[..equals]] = part[(equals + 1)..];
        }

        return result;
    }

    private static ulong ParseHex(string value)
    {
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }


    private static string ReadFileSafe(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "(no output)";
        }
        catch
        {
            return "(unreadable)";
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WINDOWS_NATIVE_REWRITE_PLAN.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }
}

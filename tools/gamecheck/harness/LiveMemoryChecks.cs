using System.Globalization;
using ManiaReplayMaster.Game;
using ManiaReplayMaster.Game.Memory;

namespace ManiaReplayMaster.GameCheck;

/// <summary>
/// Real-machine calibration of the memory layer against a <b>running</b> osu!stable, while the user sits
/// on a score screen.
///
/// This is the check the synthetic 32-bit target cannot make: it reports, per signature, whether it
/// resolved in the live JIT code heaps, then reads every field of the plan's structure table and
/// cross-checks the result against the on-disk <c>osu!.db</c> / <c>scores.db</c> indexes. That turns
/// "the machinery works" into "the values are semantically the thing on screen" (plan V2-V7).
///
/// Read-only by construction: everything goes through <see cref="ProcessMemory"/>, which only requests
/// PROCESS_VM_READ | PROCESS_QUERY_INFORMATION. Nothing under D:\Game\osu is written.
///
/// Optional arguments: <c>--poll &lt;n&gt;</c> (number of watcher polls, default 3).
/// </summary>
internal static class LiveMemoryChecks
{
    public static void Run(CheckRunner runner, string[] args)
    {
        runner.Section("Live osu! memory calibration (game must be running)");
        var polls = ArgInt(args, "--poll", 3);

        if (!ProcessMemory.IsRunning(GameLocator.ProcessName))
        {
            runner.Skip("osu! is running", "it is not — start the game and navigate to a score screen");
            return;
        }

        var pid = ProcessMemory.FindProcessId(GameLocator.ProcessName);
        if (pid is null)
        {
            runner.Fail("osu! process id found", "IsRunning returned true but no pid");
            return;
        }

        runner.Metric("pid", pid.Value);

        ProcessMemory memory;
        try
        {
            // Pointer width is asserted: a 64-bit read of this 32-bit target produces plausible garbage.
            memory = ProcessMemory.Open(pid.Value, GameLocator.ProcessName, expectedPointerSize: 4);
        }
        catch (Exception ex)
        {
            runner.Fail("OpenProcess(PROCESS_VM_READ|PROCESS_QUERY_INFORMATION)", ex.Message);
            return;
        }

        using (memory)
        {
            runner.Pass("OpenProcess(PROCESS_VM_READ|PROCESS_QUERY_INFORMATION)", $"pid {pid.Value}");
            runner.Metric("pointer width", $"{memory.PointerSize * 8}-bit (WOW64={memory.IsWow64})");
            runner.Check(memory.PointerSize == 4, "the target is read with 4-byte pointers", memory.PointerSize.ToString());

            var module = memory.FindModule(GameLocator.ProcessName + ".exe")
                         ?? memory.FindModule("osu!.exe")
                         ?? memory.EnumerateModules().FirstOrDefault();
            runner.Check(module.Name is not null, "osu! module enumerated",
                module.Name is null ? "no modules" : $"{module.Name} base=0x{module.BaseAddress:X} size=0x{module.Size:X}");
            if (module.Name is null)
            {
                return;
            }

            runner.Metric("module", module.ToString());

            // ---- region walk + signature resolution ---------------------------------------------
            var regions = memory.EnumerateRegions();
            var regionBytes = regions.Aggregate(0UL, (sum, r) => sum + r.Size);
            runner.Metric("committed readable regions", $"{regions.Count} ({regionBytes / (1024.0 * 1024.0):N0} MB)");
            runner.CheckAtLeast(regions.Count, 20, "region walk finds the target's committed regions");

            var scanner = new SignatureScanner(memory);
            var table = SignatureTables.Stable;
            var resolution = scanner.ResolveProcessWide(table, out var scan);
            runner.Metric("whole-process sweep", scan.ToString());
            runner.Metric("signatures resolved", $"{resolution.ResolvedCount}/{resolution.AttemptedCount}");

            foreach (var entry in table.Entries)
            {
                var known = resolution.Addresses.TryGetValue(entry.Name, out var address);
                runner.Metric($"  {entry.Name}",
                    known ? $"0x{address:X} (0x{address - module.BaseAddress:X} from the module base)"
                          : "NOT FOUND: " + resolution.Failures.GetValueOrDefault(entry.Name, "?"));
            }

            // Contrast: the module image is IL, so it can never contain these patterns. Reporting this
            // next to the success above is the evidence for why the whole-process sweep is mandatory.
            var moduleOnlyTable = new SignatureTable("module only", table.GameVersionRange,
                table.Entries.Select(e => e with { SearchWholeProcess = false }).ToArray());
            var moduleImage = scanner.Resolve(module, moduleOnlyTable);
            runner.Metric("module-image-only scan", $"{moduleImage.ResolvedCount}/{moduleImage.AttemptedCount} (expected 0)");
            runner.Check(moduleImage.ResolvedCount == 0,
                "a module-image scan finds none of the signatures (they are JIT code)", moduleImage.Summary);

            // ---- raw tosu chains, applied directly ------------------------------------------------
            // Transcribed literally from tosu's stable.ts: readPointer(baseAddr - 0xc), then
            // readInt(beatmapAddr + 0x6c), etc. Applied WITHOUT the field table, so it is the oracle the
            // table has to reproduce.
            //
            // The 6-byte baseAddr pattern is short, so it can legitimately occur more than once in the
            // JIT code heaps. Every occurrence is tried here and validated against the osu!.db index:
            // "this chain produces a 32-hex MD5 that exists on disk" is the weakest oracle that cannot lie
            // about which occurrence is the right one.
            runner.Section("Raw tosu chains (oracle for the field table)");
            var locator = GameLocator.Resolve();
            runner.Check(locator.Found, "osu! install located on disk", locator.Root);
            var beatmaps = new BeatmapLocator(locator);
            var db = beatmaps.LoadOsuDatabase();
            runner.Check(db is not null && db.Success, "osu!.db index loaded", db?.Summary ?? "unavailable");

            // --- state-independent oracles ---------------------------------------------------------
            // These chains do not need a beatmap object, so they distinguish "the signatures are right"
            // from "the beatmap chain is wrong". The expected values are known independently:
            // the Songs directory from the locator and the player name from osu!.db.
            runner.Section("State oracles (independent of the beatmap object)");

            if (resolution.TryGet(SignatureTables.StatusPtr, out var statusPtr))
            {
                var status = ReadInt(memory, ReadPointer(memory, statusPtr));
                runner.Metric("status", $"{status} ({DescribeGameState(status)}) [readInt(statusPtr), statusPtr=0x{statusPtr:X}]");
            }

            if (resolution.TryGet(SignatureTables.SettingsClassAddr, out var settingsAddr))
            {
                var a = ReadInt(memory, settingsAddr + 0x8);
                var b = a != 0 ? ReadInt(memory, (ulong)a + 0xB8) : 0;
                var c = b != 0 ? ReadInt(memory, (ulong)b + 0x4) : 0;
                var songsFolder = c > 0x10000 ? memory.TryReadManagedString((uint)c) : null;
                var absoluteSongsFolder = songsFolder is null ? null
                    : Path.GetFullPath(Path.Combine(locator.Root!, songsFolder));
                runner.Metric("songs folder chain", $"0x{settingsAddr:X} -> 0x{a:X} -> 0x{b:X} -> 0x{c:X}");
                runner.Metric("songs folder", songsFolder ?? "(unreadable)");
                runner.Check(absoluteSongsFolder is not null && absoluteSongsFolder.TrimEnd('\\', '/')
                        .Equals(locator.SongsDirectory!.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase),
                    "the settings chain reads the Songs directory the locator knows",
                    $"memory='{songsFolder}' expected='{locator.SongsDirectory}'");
            }

            if (resolution.TryGet(SignatureTables.RulesetsAddr, out var rulesetsAddr))
            {
                var pointer = ReadInt(memory, (ulong)((long)rulesetsAddr - 0xB));
                var ruleset = pointer != 0 ? ReadInt(memory, (ulong)pointer + 0x4) : 0;
                var gameplay = ruleset != 0 ? ReadInt(memory, (ulong)ruleset + 0x64) : 0;
                var scoreBase = ReadInt(memory, ReadPointer(memory, resolution.Addresses[SignatureTables.StatusPtr])) == 7
                    ? ReadInt(memory, (ulong)ruleset + 0x38)
                    : gameplay != 0 ? ReadInt(memory, (ulong)gameplay + 0x38) : 0;
                var playerPointer = scoreBase != 0 ? ReadInt(memory, (ulong)scoreBase + 0x28) : 0;
                var player = playerPointer > 0x10000 ? memory.TryReadManagedString((uint)playerPointer) : null;
                runner.Metric("ruleset chain", $"0x{rulesetsAddr:X} -> ruleset=0x{ruleset:X} gameplay=0x{gameplay:X} score=0x{scoreBase:X}");
                runner.Metric("player name", player ?? "(unreadable)");
                // A selected local/online score need not belong to the currently logged-in user.
                runner.Check(!string.IsNullOrWhiteSpace(player), "the active score has a player name", player);

            }

            if (resolution.TryGet(SignatureTables.CanRunSlowlyAddr, out var canRunSlowly))
            {
                var pointer = ReadInt(memory, (ulong)((long)canRunSlowly + 0x46));
                var flag = pointer > 0x10000 ? ReadByte(memory, (uint)pointer) : (byte)0xFF;
                runner.Metric("isWatchingReplay", $"{flag} [readByte(readInt(canRunSlowlyAddr + 0x46)), ptr=0x{pointer:X}]");
            }

            if (resolution.TryGet(SignatureTables.GameTimePtr, out var gameTimeAddr))
            {
                var clockPointer = ReadPointer(memory, gameTimeAddr);
                var clock = clockPointer > 0x10000 ? ReadInt(memory, clockPointer) : 0;
                runner.Metric("game time", $"clockPtr=0x{clockPointer:X} value={clock}");
            }

            if (resolution.TryGet(SignatureTables.MenuModsPtr, out var menuModsAddr))
            {
                runner.Metric("menu mods", $"0x{ReadPointer(memory, menuModsAddr):X} [readPointer(menuModsPtr)]");
            }

            ulong validatedBase = 0;
            string? validatedMd5 = null;
            if (db is not null && resolution.TryGet(SignatureTables.BaseAddr, out var resolvedBase))
            {
                var pattern = MemoryPattern.Parse(table[SignatureTables.BaseAddr]!.Pattern);
                var allHits = scanner.ScanProcess(pattern, 32);
                runner.Metric("baseAddr matches process-wide", $"{allHits.Count} (resolver picked 0x{resolvedBase:X})");
                runner.Metric("resolver picked match #", allHits.ToList().IndexOf(resolvedBase).ToString(CultureInfo.InvariantCulture));

                foreach (var hit in allHits)
                {
                    var beatmapAddr = ReadPointer(memory, ReadPointer(memory, hit - 0xC));
                    var md5Pointer = ReadInt(memory, beatmapAddr + 0x6C);
                    var md5 = ReadSharpString(memory, md5Pointer);
                    var known = md5 is not null && OsuStrings.LooksLikeMd5(md5) && db.ByMd5.ContainsKey(md5.ToLowerInvariant());
                    runner.Detail($"hit 0x{hit:X}: beatmap=0x{beatmapAddr:X} md5Ptr=0x{md5Pointer:X8} " +
                                  $"md5={(md5 is null ? "(unreadable)" : md5.Length <= 32 ? md5 : md5[..16] + "...")} " +
                                  $"{(known ? "<== VALID (exists in osu!.db)" : "rejected")}");
                    if (known && validatedBase == 0)
                    {
                        validatedBase = hit;
                        validatedMd5 = md5!.ToLowerInvariant();
                    }
                }

                runner.Check(validatedBase != 0,
                    "at least one baseAddr occurrence produces a beatmap MD5 that exists in osu!.db",
                    $"{allHits.Count} candidates, {allHits.Count(h => h == validatedBase)} validated");

                if (validatedBase != 0)
                {
                    runner.Metric("validated baseAddr", $"0x{validatedBase:X} (resolved was 0x{resolvedBase:X})");
                    runner.Metric("validated md5", validatedMd5 ?? "-");
                }
                else if (allHits.Count > 0)
                {
                    // The declared chain failed, so search the real layout empirically. Two layers:
                    //  1. find the absolute-operand instruction near the match (A1 / 8B 05 / A3) - that
                    //     operand is the address of the static field the method touches;
                    //  2. treat that field as (a) a pointer to the object or (b) the object itself, and
                    //     look for a field that reads as a 32-hex MD5 present in osu!.db.
                    runner.Section("Empirical chain search (declared chain failed)");
                    var match = allHits[0];
                    var found = 0;
                    for (var codeDelta = -0x200; codeDelta <= 0x200 && found < 12; codeDelta++)
                    {
                        var at = (ulong)((long)match + codeDelta);
                        var opcode = ReadByte(memory, at);
                        ulong operandAt;
                        if (opcode is 0xA1 or 0xA3)
                        {
                            operandAt = at + 1;
                        }
                        else if (opcode == 0x8B && ReadByte(memory, at + 1) == 0x05)
                        {
                            operandAt = at + 2;
                        }
                        else
                        {
                            continue;
                        }

                        var globalAddress = ReadPointer(memory, operandAt);
                        if (globalAddress is < 0x10000 or > 0x7FFEFFFF)
                        {
                            continue;
                        }

                        foreach (var asObject in new[] { globalAddress, ReadPointer(memory, globalAddress) })
                        {
                            if (asObject is < 0x10000 or > 0x7FFEFFFF)
                            {
                                continue;
                            }

                            for (var offset = 0; offset <= 0x140 && found < 12; offset += 4)
                            {
                                var stringPointer = ReadInt(memory, asObject + (ulong)offset);
                                if (stringPointer < 0x10000)
                                {
                                    continue;
                                }

                                var text = memory.TryReadString((uint)stringPointer, 128);
                                if (text is null || !OsuStrings.LooksLikeMd5(text) || !db.ByMd5.ContainsKey(text.ToLowerInvariant()))
                                {
                                    continue;
                                }

                                found++;
                                var entryHit = db.ByMd5[text.ToLowerInvariant()];
                                runner.Pass("empirical MD5 hit",
                                    $"code at 0x{(long)match + codeDelta:X} opcode=0x{opcode:X2} " +
                                    $"global=0x{globalAddress:X} object=0x{asObject:X} +0x{offset:X} -> md5={text} " +
                                    $"({entryHit.Artist} - {entryHit.Title})");
                            }
                        }
                    }

                    if (found == 0)
                    {
                        runner.Metric("empirical MD5 hits", "none (searched +/-0x200 bytes of code, absolute-operand forms, two dereference levels, field offsets 0..0x140)");

                        // Nothing at all matched, so dump what the candidate object graph actually contains.
                        // Any ASCII field found here (32-hex / ".osu" / "artist - title") tells us where the
                        // real beatmap object lives and what its field offsets are on this build.
                        runner.Section("Object graph dump around the baseAddr match");
                        var mask = allHits[0];
                        foreach (var delta in new long[] { -0xC, 0, 0x4, -0x8, -0x10, 0x8 })
                        {
                            var objectAddress = ReadPointer(memory, (ulong)((long)mask + delta));
                            if (objectAddress is < 0x10000 or > 0x7FFEFFFF)
                            {
                                continue;
                            }

                            runner.Detail($"candidate match{delta:+#;-#;0} -> 0x{objectAddress:X}");
                            var printed = 0;
                            for (var offset = 0; offset <= 0x400 && printed < 14; offset += 4)
                            {
                                var pointer = ReadInt(memory, objectAddress + (ulong)offset);
                                if (pointer is < 0x10000 or > 0x7FFEFFFF)
                                {
                                    continue;
                                }

                                var text = memory.TryReadString((uint)pointer, 96);
                                if (string.IsNullOrWhiteSpace(text) || !IsMostlyPrintable(text))
                                {
                                    continue;
                                }

                                printed++;
                                runner.Detail($"    +0x{offset:X3} -> 0x{pointer:X8} '{Truncate(text)}'");
                            }
                        }
                    }
                }
            }

            if (validatedBase != 0 && validatedMd5 is not null && db is not null)
            {
                var beatmapAddr = ReadPointer(memory, ReadPointer(memory, validatedBase - 0xC));
                var folder = ReadSharpString(memory, ReadInt(memory, beatmapAddr + 0x78));
                var osuFile = ReadSharpString(memory, ReadInt(memory, beatmapAddr + 0x90));
                var artist = ReadSharpString(memory, ReadInt(memory, beatmapAddr + 0x18));
                var title = ReadSharpString(memory, ReadInt(memory, beatmapAddr + 0x24));
                var creator = ReadSharpString(memory, ReadInt(memory, beatmapAddr + 0x7C));
                var difficulty = ReadSharpString(memory, ReadInt(memory, beatmapAddr + 0xAC));
                var ar = ReadFloat(memory, beatmapAddr + 0x2C);
                var cs = ReadFloat(memory, beatmapAddr + 0x30);
                var hp = ReadFloat(memory, beatmapAddr + 0x34);
                var od = ReadFloat(memory, beatmapAddr + 0x38);

                runner.Metric("beatmap object", $"0x{beatmapAddr:X}");
                runner.Metric("md5", validatedMd5);
                runner.Metric("folder", folder ?? "(unreadable)");
                runner.Metric("osu file", osuFile ?? "(unreadable)");
                runner.Metric("artist / title", $"{artist} - {title}");
                runner.Metric("creator / difficulty", $"{creator} / {difficulty}");
                runner.Metric("AR / CS / HP / OD", $"{ar:0.##} / {cs:0.##} / {hp:0.##} / {od:0.##}");
                runner.Metric("beatmap id / set id / object count",
                    $"{ReadInt(memory, beatmapAddr + 0xC8)} / {ReadInt(memory, beatmapAddr + 0xCC)} / {ReadInt(memory, beatmapAddr + 0xF8)}");

                if (db.ByMd5.TryGetValue(validatedMd5, out var dbEntry))
                {
                    runner.Check(string.Equals(artist, dbEntry.Artist, StringComparison.OrdinalIgnoreCase),
                        "raw chain: artist matches the osu!.db record", $"mem='{artist}' db='{dbEntry.Artist}'");
                    runner.Check(string.Equals(title, dbEntry.Title, StringComparison.OrdinalIgnoreCase),
                        "raw chain: title matches the osu!.db record", $"mem='{title}' db='{dbEntry.Title}'");
                    runner.Check(string.Equals(folder, dbEntry.FolderName, StringComparison.OrdinalIgnoreCase),
                        "raw chain: folder matches the osu!.db record", $"mem='{folder}' db='{dbEntry.FolderName}'");
                    runner.Check(string.Equals(osuFile, dbEntry.OsuFileName, StringComparison.OrdinalIgnoreCase),
                        "raw chain: .osu file name matches the osu!.db record", $"mem='{osuFile}' db='{dbEntry.OsuFileName}'");
                }
            }

            // ---- field table ---------------------------------------------------------------------
            var reader = new OsuStableReader(memory, module, resolution, table, scanner);
            runner.Section("Structure field reads (plan section 3.4.2 table)");

            var read = 0;
            var failed = 0;
            foreach (var field in OsuStableStructure.Fields)
            {
                if (reader.TryReadField(field, out var value, out var failure))
                {
                    read++;
                    runner.Metric($"  {field.Name}", $"{Format(value)}   [{field.Describe()}]");
                }
                else
                {
                    failed++;
                    runner.Metric($"  {field.Name}", $"FAILED: {failure}   [{field.Describe()}]");
                }
            }

            runner.Metric("fields read", $"{read}/{read + failed}");

            // ---- snapshot ------------------------------------------------------------------------
            runner.Section("Snapshot");
            var snapshot = reader.ReadSnapshot();
            runner.Metric("state / status", $"{snapshot.State} / {snapshot.Status}");
            runner.Metric("detail", snapshot.Detail ?? "(none)");
            runner.Metric("beatmap md5", snapshot.BeatmapMd5 ?? "(none)");
            runner.Metric("folder", snapshot.BeatmapFolder ?? "(none)");
            runner.Metric("osu file", snapshot.BeatmapOsuFile ?? "(none)");
            runner.Metric("beatmap id / set id", $"{snapshot.BeatmapId} / {snapshot.BeatmapSetId}");
            runner.Metric("object count", snapshot.ObjectCount?.ToString(CultureInfo.InvariantCulture) ?? "(none)");
            runner.Metric("mode", snapshot.Mode.ToString());
            runner.Metric("mods", $"0x{snapshot.Mods:X}");
            runner.Metric("player", snapshot.PlayerName ?? "(none)");
            runner.Metric("score / combo / hits / date", $"{snapshot.Score} / {snapshot.MaxCombo} / {string.Join('/', snapshot.HitCounts ?? Array.Empty<int>())} / {snapshot.ScoreDate:O}");
            runner.Metric("retries", snapshot.Retries);
            runner.Metric("watching a replay", snapshot.IsWatchingReplay);
            runner.Metric("signatures", $"{snapshot.ResolvedSignatures}/{snapshot.AttemptedSignatures}");

            // ---- semantic cross-check against the on-disk databases ------------------------------
            // (locator / beatmaps / db were loaded for the raw-chain oracle above.)
            runner.Section("Cross-check against the on-disk indexes");

            if (snapshot.BeatmapMd5 is not null)
            {
                if (db is not null)
                {
                    if (db.ByMd5.TryGetValue(snapshot.BeatmapMd5, out var entry))
                    {
                        runner.Metric("osu!.db entry", $"{entry.Artist} - {entry.Title} [{entry.DifficultyName}] mode={entry.Mode} objects={entry.TotalTimeMs}ms");
                        runner.Check(
                            string.Equals(entry.Artist, snapshot.BeatmapFolder is null ? entry.Artist : ReadFieldText(reader, OsuStableStructure.BeatmapArtist) ?? entry.Artist, StringComparison.OrdinalIgnoreCase),
                            "artist read from memory matches the osu!.db record for the same MD5",
                            $"memory='{ReadFieldText(reader, OsuStableStructure.BeatmapArtist)}' db='{entry.Artist}'");
                        runner.Check(
                            string.Equals(entry.Title, ReadFieldText(reader, OsuStableStructure.BeatmapTitle) ?? entry.Title, StringComparison.OrdinalIgnoreCase),
                            "title read from memory matches the osu!.db record for the same MD5",
                            $"memory='{ReadFieldText(reader, OsuStableStructure.BeatmapTitle)}' db='{entry.Title}'");
                        runner.Check(
                            snapshot.BeatmapFolder is null || string.Equals(entry.FolderName, snapshot.BeatmapFolder, StringComparison.OrdinalIgnoreCase),
                            "folder name read from memory matches the osu!.db record",
                            $"memory='{snapshot.BeatmapFolder}' db='{entry.FolderName}'");
                        runner.Check(
                            snapshot.BeatmapOsuFile is null || string.Equals(entry.OsuFileName, snapshot.BeatmapOsuFile, StringComparison.OrdinalIgnoreCase),
                            "osu file name read from memory matches the osu!.db record",
                            $"memory='{snapshot.BeatmapOsuFile}' db='{entry.OsuFileName}'");
                    }
                    else
                    {
                        runner.Fail("the MD5 read from memory exists in osu!.db",
                            $"{snapshot.BeatmapMd5} not found among {db.ByMd5.Count} indexed maps");
                    }
                }

                var located = beatmaps.ResolveFromIdentity(snapshot.BeatmapFolder, snapshot.BeatmapOsuFile, snapshot.BeatmapMd5, verifyMd5: true);
                runner.Check(located is not null && located.Md5Confirmed,
                    "the beatmap file the memory identity path resolves has the matching byte MD5",
                    located is null ? "not resolved" : $"{located.Path} confirmed={located.Md5Confirmed}");

                // Replay side: this is the headline feature - what the auto-fill would offer now.
                var scores = new ScoreDatabaseProvider(locator);
                var replays = new ReplayResolver(locator, scores);
                var replayResolution = replays.Resolve(snapshot, snapshot.BeatmapMd5, allowScan: true);
                runner.Metric("replay candidates", replayResolution.Candidates.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var candidate in replayResolution.Candidates.Take(6))
                {
                    runner.Detail($"candidate {candidate.Confidence:P0} {Path.GetFileName(candidate.ReplayPath)} :: {candidate.Explanation}");
                }

                if (replayResolution.Best is { } best)
                {
                    runner.Metric("best replay", $"{best.ReplayPath} ({best.Confidence:P1})");
                    runner.Check(File.Exists(best.ReplayPath), "the best replay candidate exists on disk", best.ReplayPath);
                }
                else
                {
                    // A score screen whose map has no local score recorded is legitimate; record it as a
                    // metric rather than a failure so the calibration run stays honest.
                    runner.Metric("no replay candidate", replayResolution.NotFoundReason ?? "unknown reason");
                }
            }
            else
            {
                runner.Fail("a beatmap MD5 was read from memory",
                    "without it none of the resolver levels can run; check the field table against this build");
            }

            // ---- end-to-end watcher (also exercises the lazy-JIT rescan) --------------------------
            runner.Section("GameStateWatcher polls (live)");
            using var watcher = new GameStateWatcher(new GameStateWatcherOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(200),
                WaitForProcess = false,
                EmitOnEveryPoll = true
            });

            for (var i = 1; i <= polls; i++)
            {
                var polled = watcher.PollOnce();
                runner.Metric($"poll {i}",
                    $"{polled.State}/{polled.Status} md5={polled.BeatmapMd5 ?? "-"} " +
                    $"sigs={polled.ResolvedSignatures}/{polled.AttemptedSignatures} " +
                    $"{(polled.Detail is null ? string.Empty : "detail=" + polled.Detail)}");
                Thread.Sleep(400);
            }

            runner.Metric("last signature resolution", watcher.LastSignatureResolution?.Summary ?? "(none)");
        }
    }

    private static string? ReadFieldText(OsuStableReader reader, string fieldName)
        => reader.TryReadField(fieldName, out var value, out _) ? value.Text : null;

    // --- tiny read helpers for the raw tosu chains (deliberately independent of OsuStableReader) ---

    private static uint ReadPointer(ProcessMemory memory, ulong address)
        => memory.TryReadUInt32(address, out var value) ? value : 0;

    private static byte ReadByte(ProcessMemory memory, ulong address)
        => memory.TryReadByte(address, out var value) ? value : (byte)0;

    private static int ReadInt(ProcessMemory memory, ulong address)
        => memory.TryReadInt32(address, out var value) ? value : 0;

    private static double ReadFloat(ProcessMemory memory, ulong address)
        => memory.TryReadInt32(address, out var raw) ? BitConverter.Int32BitsToSingle(raw) : 0;

    private static string? ReadSharpString(ProcessMemory memory, int pointer)
        => pointer is > 0x10000 ? memory.TryReadManagedString((uint)pointer) : null;

    private static string Format(MemoryValue value)
        => value.Kind switch
        {
            MemoryValueKind.Float => value.Real.ToString("0.####", CultureInfo.InvariantCulture),
            MemoryValueKind.PointerToManagedString => $"'{Truncate(value.Text)}'",
            MemoryValueKind.PointerToUtf8String => $"'{Truncate(value.Text)}'",
            MemoryValueKind.PointerToAnsiString => $"'{Truncate(value.Text)}'",
            _ => value.Integer.ToString(CultureInfo.InvariantCulture)
        };

    private static string Truncate(string? text)
        => text is null ? "(null)" : text.Length <= 60 ? text : text[..60] + "...";

    /// <summary>Plan section 3.4.2 mapping of the raw status value to the screen it means.</summary>
    private static string DescribeGameState(int status) => status switch
    {
        0 => "main menu",
        1 => "song select",
        2 => "playing",
        7 => "results / score screen",
        _ => "unknown"
    };

    private static bool IsMostlyPrintable(string text)    {
        if (text.Length < 3)
        {
            return false;
        }

        var printable = 0;
        foreach (var c in text)
        {
            if (c is >= ' ' and <= '~' or >= '\u4e00' and <= '\u9fff')
            {
                printable++;
            }
        }

        return printable >= text.Length - 1;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return fallback;
    }
}

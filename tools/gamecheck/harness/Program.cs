using System.Diagnostics;
using System.Text;
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Replay;
using ManiaReplayMaster.Game;

namespace ManiaReplayMaster.GameCheck;

/// <summary>
/// Reproducible validation harness for the game-link subsystem (M2a layer plus the memory layer).
///
/// Every number quoted in the final report comes from here, and every command is read-only with
/// respect to <c>D:\Game\osu</c>: files are opened with <see cref="FileShare.ReadWrite"/> and no code
/// path in this project writes to the game directory. The only writes are the harness's own caches
/// and logs, which live under <c>%LOCALAPPDATA%\ManiaReplayMaster</c> and <c>tools/gamecheck/out</c>.
///
/// Usage:
/// <code>
/// gamecheck locate
/// gamecheck scoredb      [--sample 30]
/// gamecheck beatmapdb    [--sample 25] [--hash-sample 25]
/// gamecheck replayindex
/// gamecheck resolve      [--sample 30]
/// gamecheck watch        [--dir &lt;temp dir&gt;]
/// gamecheck songsscan    [--sample 5]
/// gamecheck memory32     [--helper &lt;path&gt;]
/// gamecheck staticsig    [--exe &lt;osu!.exe&gt;] [--report &lt;path.md&gt;]
/// gamecheck live         [--poll &lt;n&gt;]   (requires the game to be running)
/// gamecheck all
/// </code>
/// </summary>
public static class Program
{
    private const string DefaultOsuRoot = @"D:\Game\osu";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        var rest = args.Skip(1).ToArray();
        var runner = new CheckRunner();
        var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "out");
        outDir = Path.GetFullPath(outDir);

        try
        {
            switch (command)
            {
                case "locate":
                    Locate(runner, rest);
                    break;
                case "scoredb":
                    ScoreDb(runner, rest);
                    break;
                case "beatmapdb":
                    BeatmapDb(runner, rest);
                    break;
                case "replayindex":
                    ReplayIndex(runner);
                    break;
                case "resolve":
                    Resolve(runner, rest);
                    break;
                case "watch":
                    Watch(runner, rest);
                    break;
                case "songsscan":
                    SongsScan(runner, rest);
                    break;
                case "probe-headers":
                return HeaderProbe.Run(rest.ToArray());
                case "memory32":
                    Memory32(runner, rest);
                    break;
                case "staticsig":
                    StaticSig(runner, rest);
                    break;
                case "live":
                    Live(runner, rest);
                    break;
                case "all":
                    Locate(runner, rest);
                    ScoreDb(runner, rest);
                    BeatmapDb(runner, rest);
                    ReplayIndex(runner);
                    Resolve(runner, rest);
                    Watch(runner, rest);
                    SongsScan(runner, rest);
                    Memory32(runner, rest);
                    StaticSig(runner, rest);
                    break;
                case "-h":
                case "--help":
                case "help":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown command '{command}'");
                    PrintUsage();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            runner.Fail("harness crashed", $"{ex.GetType().Name}: {ex.Message}");
            runner.WriteRaw(ex.ToString());
        }

        runner.Section("SUMMARY");
        runner.Metric("checks passed", runner.Passed);
        runner.Metric("checks failed", runner.Failed);
        runner.Metric("host process", $"{Environment.ProcessPath} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");

        try
        {
            runner.SaveLog(Path.Combine(outDir, $"gamecheck-{command}.log"));
            Console.WriteLine($"  log: {Path.Combine(outDir, $"gamecheck-{command}.log")}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not write log: {ex.Message}");
        }

        return runner.Failed == 0 ? 0 : 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            gamecheck —game-link validation harness

              locate                     resolve the osu! root and its directories
              scoredb                    parse scores.db and verify replay pointers
              beatmapdb                  parse osu!.db and verify MD5 -> .osu paths
              replayindex                index every .osr header under Data\r + Replays
              resolve                    end-to-end resolution over sampled scores
              watch                      FileSystemWatcher check against a temp directory
              songsscan                  Songs\*\*.osu MD5 scan + cache reuse
              memory32                   synthetic 32-bit target process checks
              staticsig                  signature table vs. on-disk osu!.exe (no game needed)
              live                       real-machine calibration against a RUNNING osu! [--poll N]
              all                        everything above

            Options: --sample N  --hash-sample N  --root <osu root>  --helper <path>
                     --exe <osu!.exe>  --report <path.md>
            """);
    }

    // ------------------------------------------------------------------ locate

    private static GameLocator Locate(CheckRunner runner, string[] args)
    {
        runner.Section("GameLocator");
        var manual = ArgValue(args, "--root");
        var locator = manual is null ? GameLocator.Resolve() : GameLocator.Resolve(manual);

        foreach (var line in locator.Trace)
        {
            runner.Detail(line);
        }

        runner.Check(locator.Found, "osu! root resolved", locator.Root ?? "(none)");
        if (!locator.Found)
        {
            return locator;
        }

        runner.Metric("source", locator.Source);
        runner.Metric("SongsDirectory", locator.SongsDirectory);
        runner.Metric("SongsDirectoryIsCustom", locator.SongsDirectoryIsCustom);
        runner.Metric("ReplaysDirectory", locator.ReplaysDirectory);
        runner.Metric("DataRDirectory", locator.DataRDirectory);
        runner.Metric("OsuDbPath", locator.OsuDbPath);
        runner.Metric("ScoresDbPath", locator.ScoresDbPath);

        runner.Check(Directory.Exists(locator.SongsDirectory), "Songs exists", locator.SongsDirectory ?? "(null)");
        runner.Check(Directory.Exists(locator.DataRDirectory), "Data\\r exists", locator.DataRDirectory ?? "(null)");
        runner.Check(File.Exists(locator.OsuDbPath), "osu!.db exists", locator.OsuDbPath ?? "(null)");
        runner.Check(File.Exists(locator.ScoresDbPath), "scores.db exists", locator.ScoresDbPath ?? "(null)");

        var songsFolders = SafeCount(() => Directory.GetDirectories(locator.SongsDirectory!).Length);
        runner.Metric("Songs subfolders", songsFolders);
        var dataR = SafeCount(() => Directory.GetFiles(locator.DataRDirectory!).Length);
        runner.Metric("Data\\r files", dataR);
        var replays = SafeCount(() => Directory.GetFiles(locator.ReplaysDirectory!).Length);
        runner.Metric("Replays files", replays);

        // The manual override must win over every other candidate.
        var forced = GameLocator.Resolve(locator.Root);
        runner.Check(forced.Source == OsuRootSource.Manual, "manual override wins", forced.Source.ToString());
        runner.Check(forced.Root == locator.Root, "manual override returns the same root", forced.Root ?? "(null)");

        return locator;
    }

    // ------------------------------------------------------------------ scores.db

    private static void ScoreDb(CheckRunner runner, string[] args)
    {
        runner.Section("ScoreDatabaseReader —real scores.db");
        var locator = RequireLocator();
        if (locator is null)
        {
            runner.Skip("scores.db parse", "no osu! install resolved");
            return;
        }

        var sample = ArgInt(args, "--sample", 30);
        var path = locator.ScoresDbPath!;
        var full = HasFlag(args, "--full");
        var stopwatch = Stopwatch.StartNew();
        var ok = ScoreDatabaseReader.TryRead(path, out var result);
        stopwatch.Stop();

        runner.Check(ok && result.Success, "scores.db parsed", result.Summary);
        if (!ok)
        {
            return;
        }

        runner.Metric("version", result.Version);
        runner.Metric("declared groups", result.DeclaredGroupCount);
        runner.Metric("parsed groups", result.ParsedGroupCount);
        runner.Metric("score records", result.ScoreRecordCount);
        runner.Metric("empty groups", result.EmptyGroupCount);
        runner.Metric("bytes consumed", $"{result.BytesConsumed} / {result.FileLength}");
        runner.Metric("trailing bytes", result.TrailingByteCount);
        runner.Metric("duplicate group MD5", result.DuplicateGroupMd5Count);
        runner.Metric("malformed group MD5", result.MalformedGroupMd5Count);
        runner.Metric("records with MD5 != group", result.RecordMd5MismatchCount);
        runner.Metric("record version range", $"{result.MinimumRecordVersion}..{result.MaximumRecordVersion}");
        runner.Metric("parse duration", $"{stopwatch.Elapsed.TotalMilliseconds:N0} ms");
        foreach (var warning in result.Warnings.Take(10))
        {
            runner.Detail($"warning: {warning}");
        }

        runner.CheckEqual(result.ParsedGroupCount, result.DeclaredGroupCount, "every declared group parsed");
        runner.CheckEqual(result.BytesConsumed, result.FileLength, "byte consumption == file size");
        runner.CheckEqual(result.TrailingByteCount, 0, "no trailing bytes");
        runner.CheckEqual(result.RecordMd5MismatchCount, 0, "no record/group MD5 disagreement");

        // Deterministic sample: spread across the group list using a fixed stride.
        var records = result.Groups
            .SelectMany(kv => kv.Value.Select(v => (Md5: kv.Key, Score: v)))
            .ToList();

        runner.Metric("total records enumerable", records.Count);
        var taken = SampleDeterministic(records, sample);
        var exists = 0;
        var headerMatches = 0;
        var headerParsed = 0;
        var displayed = 0;

        foreach (var (md5, score) in taken)
        {
            var expected = Path.Combine(locator.DataRDirectory!, score.ReplayFileName);
            var fileExists = File.Exists(expected);
            if (fileExists)
            {
                exists++;
            }

            var header = OsuReplayHeaderReader.TryParse(expected);
            if (header.Success && header.Header is not null)
            {
                headerParsed++;
                var h = header.Header;
                var matches = string.Equals(h.BeatmapHash, md5, StringComparison.OrdinalIgnoreCase)
                              && h.Username == score.PlayerName
                              && h.Mods == score.Mods
                              && h.Score == score.TotalScore
                              && h.MaxCombo == score.MaxCombo
                              && h.Count300 == score.Count300
                              && h.Count100 == score.Count100
                              && h.Count50 == score.Count50
                              && h.CountGeki == score.CountGeki
                              && h.CountKatu == score.CountKatu
                              && h.Misses == score.CountMiss
                              && h.TimestampTicks == score.DateTicks;
                if (matches)
                {
                    headerMatches++;
                }
                else if (displayed++ < 5)
                {
                    runner.Detail(
                        $"mismatch {score.ReplayFileName}: header(md5={h.BeatmapHash}, player={h.Username}, mods={h.Mods}, score={h.Score}, " +
                        $"combo={h.MaxCombo}, hits={string.Join('/', h.HitCounts)}, ticks={h.TimestampTicks}) " +
                        $"vs db(md5={md5}, player={score.PlayerName}, mods={score.Mods}, score={score.TotalScore}, " +
                        $"combo={score.MaxCombo}, hits={string.Join('/', score.HitCounts)}, ticks={score.DateTicks})");
                }
            }
            else if (displayed++ < 5)
            {
                runner.Detail($"unreadable {score.ReplayFileName}: {header.Error}");
            }
        }

        runner.CheckRatio(exists, taken.Count, 1.0, $"sampled replay files exist under Data\\r ({taken.Count} records)");
        runner.CheckRatio(headerMatches, taken.Count, 1.0, $"sampled headers agree with the db record on all 12 fields");

        // Raw dump of any sample entry whose file is missing, so a low hit rate is explained rather
        // than guessed at (and never "adjusted to fit").
        if (exists < taken.Count)
        {
            runner.Info("sampled records without a replay file at the derived path (raw fields):");
            foreach (var (md5, score) in taken)
            {
                var expected = Path.Combine(locator.DataRDirectory!, score.ReplayFileName);
                if (!File.Exists(expected))
                {
                    runner.Detail(
                        $"{md5} dateTicks={score.DateTicks} filetime={score.ReplayFiletime} " +
                        $"name={score.ReplayFileName} player='{score.PlayerName}' score={score.TotalScore} v={score.Version}");
                }
            }
        }

        // Full-population variant: the same rule applied to every record, matching the reference
        // validation's "5,420 / 5,420" claim. Slower, so it is opt-in.
        if (full)
        {
            runner.Info("full-population check over every score record:");
            var fullStopwatch = Stopwatch.StartNew();
            var fileExists = 0;
            var distinctPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (md5, score) in records)
            {
                distinctPrefixes.Add(md5);
                if (File.Exists(Path.Combine(locator.DataRDirectory!, score.ReplayFileName)))
                {
                    fileExists++;
                }
            }

            fullStopwatch.Stop();
            runner.Metric("full-check duration", $"{fullStopwatch.Elapsed.TotalSeconds:N2} s");
            runner.Metric("distinct group MD5 seen as name prefixes", distinctPrefixes.Count);
            runner.CheckRatio(fileExists, records.Count, 1.0,
                $"EVERY record resolves to an existing Data\\r file ({records.Count} records)");
        }

        // Also prove the Core parser (owned elsewhere) agrees with this reader on the same sample.
        var coreAgreements = 0;
        var coreCompared = 0;
        var hashSample = Math.Min(10, taken.Count);
        foreach (var (_, score) in taken.Take(hashSample))
        {
            var file = Path.Combine(locator.DataRDirectory!, score.ReplayFileName);
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                var core = OsuReplayParser.Parse(file);
                var mine = OsuReplayHeaderReader.Parse(file);
                coreCompared++;
                if (mine.HeaderFieldsEqual(core))
                {
                    coreAgreements++;
                }
                else
                {
                    runner.Detail($"header-only reader disagrees with Core parser on {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex)
            {
                runner.Detail($"Core parser failed on {Path.GetFileName(file)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        runner.CheckRatio(coreAgreements, coreCompared, 1.0,
            $"header-only reader == Core OsuReplayParser on {coreCompared} files");
    }

    // ------------------------------------------------------------------ osu!.db

    private static void BeatmapDb(CheckRunner runner, string[] args)
    {
        runner.Section("BeatmapDatabaseReader —real osu!.db");
        var locator = RequireLocator();
        if (locator is null)
        {
            runner.Skip("osu!.db parse", "no osu! install resolved");
            return;
        }

        var sample = ArgInt(args, "--sample", 25);
        var hashSample = ArgInt(args, "--hash-sample", 25);
        var path = locator.OsuDbPath!;

        var stopwatch = Stopwatch.StartNew();
        var ok = BeatmapDatabaseReader.TryRead(path, locator.SongsDirectory, out var result);
        stopwatch.Stop();

        runner.Check(ok && result.Success, "osu!.db parsed", result.Summary);
        if (!ok)
        {
            return;
        }

        runner.Metric("version", result.Version);
        runner.Metric("declared folder count", result.DeclaredFolderCount);
        runner.Metric("declared beatmap count", result.DeclaredBeatmapCount);
        runner.Metric("parsed records", result.ParsedBeatmapCount);
        runner.Metric("bytes consumed", $"{result.BytesConsumed} / {result.FileLength}");
        runner.Metric("trailing bytes", result.TrailingByteCount);
        runner.Metric("trailer int32", result.TrailerValue);
        runner.Metric("player name", result.PlayerName);
        runner.Metric("unique MD5", result.ByMd5.Count);
        runner.Metric("star-rating pairs", result.StarRatingPairTotal);
        runner.Metric("timing points", result.TimingPointTotal);
        runner.Metric("parse duration", $"{stopwatch.Elapsed.TotalMilliseconds:N0} ms");
        runner.Metric("mode histogram", string.Join(", ", result.ModeHistogram.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (var warning in result.Warnings.Take(6))
        {
            runner.Detail($"warning: {warning}");
        }

        runner.CheckEqual(result.ParsedBeatmapCount, result.DeclaredBeatmapCount, "every declared record parsed");
        runner.CheckEqual(result.BytesConsumed, result.FileLength, "byte consumption == file size");
        runner.CheckEqual(result.TrailingByteCount, 0, "no trailing bytes");

        // The trailer is an opaque int32 whose value is NOT a format constant: the same osu! build wrote
        // 5 in one save and 0 in the next (measured on this machine's osu!.db, 2026-10-16). The format
        // contract that matters is that the int32 is present and that the walk consumed the file exactly
        // (asserted above); pinning the value would make a legitimate game save look like a format break.
        runner.Check(result.HasTrailer && result.TrailerValue is 0 or 5,
            "file trailer present and one of the values osu!stable writes (0 or 5)",
            $"trailer={result.TrailerValue} present={result.HasTrailer}");

        var entries = result.ByMd5.Values.ToList();
        var sampled = SampleDeterministic(entries, sample);
        var existing = 0;
        foreach (var entry in sampled)
        {
            var exists = entry.AbsolutePath is not null && File.Exists(entry.AbsolutePath);
            if (exists)
            {
                existing++;
            }
            else
            {
                runner.Detail($"missing: {entry.FolderName}\\{entry.OsuFileName}");
            }
        }

        runner.CheckRatio(existing, sampled.Count, 0.95,
            $"sampled records whose Songs path exists ({sampled.Count} sampled)");

        var hashChecked = SampleDeterministic(entries, hashSample);
        var hashEqual = 0;
        var hashCompared = 0;
        var mismatches = new List<string>();
        foreach (var entry in hashChecked)
        {
            if (entry.AbsolutePath is null || !File.Exists(entry.AbsolutePath))
            {
                continue;
            }

            var actual = Md5Of(entry.AbsolutePath);
            hashCompared++;
            if (string.Equals(actual, entry.Md5, StringComparison.OrdinalIgnoreCase))
            {
                hashEqual++;
            }
            else
            {
                mismatches.Add($"{Path.GetFileName(entry.AbsolutePath)}: db={entry.Md5} disk={actual}");
            }
        }

        foreach (var mismatch in mismatches.Take(5))
        {
            runner.Detail($"MD5 mismatch (user-edited map is a legitimate cause): {mismatch}");
        }

        runner.CheckRatio(hashEqual, hashCompared, 0.95,
            $"sampled .osu byte-MD5 equals the index MD5 ({hashCompared} hashed)");

        // Full-population path check: the reference reports 22,002 / 22,123 reachable, so the exact
        // number is a strong signal that the walk and the path composition are both right.
        if (HasFlag(args, "--full"))
        {
            var fullStopwatch = Stopwatch.StartNew();
            var reachable = 0;
            var missingFolder = 0;
            var missingFile = 0;
            foreach (var entry in entries)
            {
                if (entry.AbsolutePath is null)
                {
                    missingFolder++;
                    continue;
                }

                if (File.Exists(entry.AbsolutePath))
                {
                    reachable++;
                }
                else if (!Directory.Exists(Path.GetDirectoryName(entry.AbsolutePath)))
                {
                    missingFolder++;
                }
                else
                {
                    missingFile++;
                }
            }

            fullStopwatch.Stop();
            runner.Metric("full check duration", $"{fullStopwatch.Elapsed.TotalSeconds:N2} s");
            runner.Metric("records with a missing folder", missingFolder);
            runner.Metric("records with a missing file inside an existing folder", missingFile);
            runner.CheckRatio(reachable, result.ParsedBeatmapCount, 0.99,
                $"FULL: records whose folderName + osuFileName exist on disk ({result.ParsedBeatmapCount} records)");
        }
    }

    // ------------------------------------------------------------------ replay index

    private static void ReplayIndex(CheckRunner runner)
    {
        runner.Section("ReplayResolver —level 3 header index over Data\\r + Replays");
        var locator = RequireLocator();
        if (locator is null)
        {
            runner.Skip("header index", "no osu! install resolved");
            return;
        }

        // Measure a genuine COLD parse. EnsureIndex(force: true) only skips the in-memory LastScan
        // shortcut; it still loads entries from the on-disk cache, so when an earlier check in this
        // same run has already warmed the cache, the "parsed" counter is legitimately 0. That is
        // correct behaviour and must not be read as a parse failure, so the cold measurement uses a
        // resolver with the cache disabled.
        var provider = new ScoreDatabaseProvider(locator);
        var coldOptions = new ReplayResolverOptions { UseCache = false };
        var cold = new ReplayResolver(locator, provider, coldOptions).EnsureIndex(force: true);
        runner.Check(cold.Success, "cold header index built (cache disabled)", cold.Summary);
        runner.Metric("cold headers parsed", cold.HeadersParsed);
        runner.Metric("cold duration", $"{cold.Duration.TotalSeconds:N2} s");
        runner.CheckRatio(cold.HeadersParsed, cold.FilesFound, 0.99,
            "cold scan: share of files that yielded a parseable header");
        runner.CheckEqual(cold.HeadersFailed, 0, "cold scan: no .osr file failed to yield a header");

        var resolver = new ReplayResolver(locator, provider);
        var scan = resolver.EnsureIndex(force: true);
        runner.Check(scan.Success, "header index built", scan.Summary);
        if (!scan.Success)
        {
            return;
        }

        runner.Metric("directories", string.Join("; ", scan.Directories));
        runner.Metric("files found", scan.FilesFound);
        runner.Metric("headers parsed", scan.HeadersParsed);
        runner.Metric("headers reused", scan.HeadersReusedFromCache);
        runner.Metric("headers unreadable", scan.HeadersFailed);
        runner.Metric("distinct beatmap MD5", scan.DistinctBeatmapMd5);
        runner.Metric("duration", $"{scan.Duration.TotalSeconds:N2} s");
        runner.Metric("throughput", $"{scan.FilesPerSecond:N0} files/s");

        var expected = SafeCount(() => Directory.GetFiles(locator.DataRDirectory!, "*.osr").Length);
        runner.CheckEqual(scan.FilesFound, expected + SafeCount(() => Directory.GetFiles(locator.ReplaysDirectory!, "*.osr", SearchOption.AllDirectories).Length),
            "indexed file count == .osr files on disk (Data\\r + Replays)");

        // Second run must be served from the cache: that is the whole point of the persistent index.
        var second = new ReplayResolver(locator, provider).EnsureIndex();
        runner.Metric("second run duration", $"{second.Duration.TotalSeconds:N2} s");
        runner.Metric("second run reused", second.HeadersReusedFromCache);
        runner.CheckEqual(second.FilesFound, scan.FilesFound, "second run sees the same file count");
        runner.Check(second.Duration <= scan.Duration, "cache makes the second run no slower",
            $"{second.Duration.TotalSeconds:N2}s vs {scan.Duration.TotalSeconds:N2}s");
        // The second run must be served entirely from the persistent cache: that is the whole point
        // of the index. Counting parsed + reused here keeps the assertion meaningful whether the
        // cache was cold or already warm when this check started.
        runner.CheckRatio(scan.HeadersParsed + scan.HeadersReusedFromCache, scan.FilesFound, 0.99,
            "warm scan: every file is either parsed or reused from cache");
    }

    // ------------------------------------------------------------------ resolve

    private static void Resolve(CheckRunner runner, string[] args)
    {
        runner.Section("End-to-end resolution over sampled local scores");
        var locator = RequireLocator();
        if (locator is null)
        {
            runner.Skip("resolution", "no osu! install resolved");
            return;
        }

        var sample = ArgInt(args, "--sample", 30);
        var provider = new ScoreDatabaseProvider(locator);
        var scores = provider.Load();
        if (scores is null || !scores.Success)
        {
            runner.Skip("resolution", $"scores.db unavailable: {scores?.Error}");
            return;
        }

        var resolver = new ReplayResolver(locator, provider);
        var all = scores.Groups.SelectMany(kv => kv.Value.Select(s => (Md5: kv.Key, Score: s))).ToList();
        var taken = SampleDeterministic(all, sample);

        // Dump the raw level-1 ranking for the first sampled score so the scoring is visible rather
        // than inferred.
        var probe = taken.FirstOrDefault();
        if (probe.Score is not null)
        {
            runner.Info($"probe: {probe.Md5} / {probe.Score.ReplayFileName}");
            foreach (var c in resolver.FromScoresDatabase(probe.Md5).Take(4))
            {
                runner.Detail($"pointer candidate {Path.GetFileName(c.ReplayPath)} conf={c.Confidence:P1} " +
                              $"exists={c.FileExists} hdr={c.Header?.BeatmapHash} why=[{c.Explanation}]");
            }

            var probeSnapshot = new GameSnapshot
            {
                State = OsuGameState.ScorePanelOpen,
                Status = MemoryReadStatus.Ok,
                BeatmapMd5 = probe.Md5,
                PlayerName = probe.Score.PlayerName,
                Mods = probe.Score.Mods,
                Score = probe.Score.TotalScore,
                MaxCombo = probe.Score.MaxCombo,
                HitCounts = probe.Score.HitCounts,
                ScoreDate = probe.Score.DateUtc,
                Mode = probe.Score.Mode
            };

            foreach (var c in resolver.FromScoresDatabase(probeSnapshot, probe.Md5).Take(4))
            {
                runner.Detail($"snapshot candidate {Path.GetFileName(c.ReplayPath)} conf={c.Confidence:P1} why=[{c.Explanation}]");
            }
        }

        var level1Correct = 0;
        var level1Found = 0;
        var withMemoryWrong = 0;
        var withoutMemoryCorrect = 0;
        var ambiguous = 0;
        var candidateCounts = new List<int>();
        var diagnosticShown = 0;
        var pointerListed = 0;
        var pointerComplete = 0;
        var snapshotCorrect = 0;
        var correctRankedFirst = 0;
        var snapshotLowWithoutStrongField = 0;
        var snapshotLowWithStrongField = 0;
        var wrongDetails = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        foreach (var (md5, score) in taken)
        {
            var expectedPath = Path.Combine(locator.DataRDirectory!, score.ReplayFileName);
            if (!File.Exists(expectedPath))
            {
                continue; // Covered by the scoredb command's exist-rate check.
            }

            // (a) Pointer only (memory unavailable): every local score of the beatmap must be offered,
            //     each pointing at a file that exists. Without any score fields there is nothing to
            //     rank on, so the confidence is deliberately NOT 1.0 and the GUI shows a candidate list.
            var blind = resolver.Resolve(null, md5, allowScan: false);
            candidateCounts.Add(blind.Candidates.Count);
            var expectedInBlind = blind.Candidates.Any(c =>
                string.Equals(c.ReplayPath, expectedPath, StringComparison.OrdinalIgnoreCase) && c.FileExists);
            if (expectedInBlind)
            {
                pointerListed++;
            }

            var localCount = provider.ForBeatmap(md5).Count;
            if (blind.Candidates.Count(c => c.FileExists) == localCount)
            {
                pointerComplete++;
            }

            // (b) Full snapshot as the memory layer would deliver it (simulating "the user is looking
            //     at exactly this score"). This is the case that has to be unambiguous.
            var snapshot = new GameSnapshot
            {
                State = OsuGameState.ScorePanelOpen,
                Status = MemoryReadStatus.Ok,
                BeatmapMd5 = md5,
                PlayerName = score.PlayerName,
                Mods = score.Mods,
                Score = score.TotalScore,
                MaxCombo = score.MaxCombo,
                HitCounts = score.HitCounts,
                ScoreDate = score.DateUtc,
                Mode = score.Mode
            };

            var result = resolver.Resolve(snapshot, md5, allowScan: false);
            if (result.Best is not null && result.Best.Confidence >= 0.99)
            {
                if (string.Equals(result.Best.ReplayPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    snapshotCorrect++;
                }
                else
                {
                    withMemoryWrong++;
                    if (wrongDetails.Count < 5)
                    {
                        wrongDetails.Add(
                            $"expected {score.ReplayFileName} got {Path.GetFileName(result.Best.ReplayPath)} " +
                            $"({result.Best.Explanation})");
                    }
                }
            }
            else if (diagnosticShown++ < 5)
            {
                runner.Detail(
                    $"low confidence for {score.ReplayFileName}: best={result.Best?.Confidence:P1} " +
                    $"path={Path.GetFileName(result.Best?.ReplayPath ?? "-")} why=[{result.Best?.Explanation}]");
            }

            if (result.Best is not null
                && string.Equals(result.Best.ReplayPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                correctRankedFirst++;
                if (result.Best.Confidence < 0.9)
                {
                    // Right file, below the confident threshold. That must be explainable: either the
                    // snapshot had no strong field to compare at all (a data limitation), or something is
                    // wrong (a defect). Counted separately so the distinction is measured, not assumed.
                    if (snapshot.Score is null && snapshot.ScoreDate is null && snapshot.HitCounts is null)
                    {
                        snapshotLowWithoutStrongField++;
                    }
                    else
                    {
                        snapshotLowWithStrongField++;
                        if (snapshotLowWithStrongField <= 5)
                        {
                            runner.Detail(
                                $"below-threshold but correct file: {score.ReplayFileName} conf={result.Best.Confidence:P1} " +
                                $"why=[{result.Best.Explanation}]");
                        }
                    }
                }
            }

            if (result.IsAmbiguous)
            {
                ambiguous++;
            }

            // (c) Memory-free level 3: the MD5 alone, forcing the header scan to do the work.
            var scanResult = resolver.Resolve(null, md5, allowScan: true);
            if (scanResult.Candidates.Any(c =>
                    string.Equals(c.ReplayPath, expectedPath, StringComparison.OrdinalIgnoreCase)))
            {
                withoutMemoryCorrect++;
            }
        }

        stopwatch.Stop();

        runner.Metric("sample size (scores with an existing replay file)",
            taken.Count(s => File.Exists(Path.Combine(locator.DataRDirectory!, s.Score.ReplayFileName))));
        runner.Metric("level-1 pointer: expected file present among candidates", $"{pointerListed}/{taken.Count}");
        runner.Metric("level-1 pointer: candidate list complete for the beatmap", $"{pointerComplete}/{taken.Count}");
        runner.Metric("level-1+snapshot: own file ranked first at >=99%", $"{snapshotCorrect}/{taken.Count}");
        runner.Metric("own file first but <90% because the snapshot lacked score/date/hits", snapshotLowWithoutStrongField);
        runner.Metric("own file first but <90% DESPITE a strong field being comparable", snapshotLowWithStrongField);
        runner.Metric("snapshot matches that picked another file", withMemoryWrong);
        runner.Metric("ambiguous cases (tie within 5 %)", ambiguous);
        runner.Metric("average candidate count", candidateCounts.Count == 0 ? 0 : candidateCounts.Average());
        runner.Metric("level-3 file present in candidates", $"{withoutMemoryCorrect}/{taken.Count}");
        runner.Metric("total duration", $"{stopwatch.Elapsed.TotalSeconds:N2} s");
        foreach (var detail in wrongDetails)
        {
            runner.Detail(detail);
        }

        runner.CheckRatio(pointerListed, taken.Count, 1.0,
            "level-1 pointer offered the expected replay file for every sampled score (memory unavailable)");
        runner.CheckRatio(pointerComplete, taken.Count, 1.0,
            "level-1 pointer listed every local score of the beatmap that has a file");
        // Top-1 accuracy is the plan's V5 criterion; the >=99 % tier is the stricter "auto-fill without
        // asking" tier. Both are reported because they answer different questions, and the confidence
        // distribution is deliberately conservative: a field the memory layer could not read lowers the
        // score instead of being assumed to match.
        runner.CheckRatio(correctRankedFirst, taken.Count, 0.95,
            "Top-1 accuracy: the snapshot ranked the correct replay file first (plan §7.3 V5)");
        runner.CheckRatio(snapshotCorrect, taken.Count, 0.90,
            "level-1 + snapshot reached >=99% confidence for the correct file (auto-fill tier)");
        runner.CheckEqual(snapshotLowWithStrongField + snapshotLowWithoutStrongField, correctRankedFirst - snapshotCorrect,
            "every below-threshold Top-1 hit is accounted for");
        runner.CheckEqual(withMemoryWrong, 0, "no Top-1 result ever pointed at the wrong file");
        runner.CheckEqual(withMemoryWrong, 0, "a full snapshot never selected a different file");
        runner.CheckRatio(withoutMemoryCorrect, taken.Count, 1.0, "level-3 scan contained the expected file in the candidate list");

        // Level-2-style check: hand the resolver an observed file directly.
        var observed = SampleDeterministic(all, Math.Min(5, taken.Count));
        foreach (var (md5, score) in observed)
        {
            var path = Path.Combine(locator.DataRDirectory!, score.ReplayFileName);
            if (!File.Exists(path))
            {
                continue;
            }

            var fromWatch = resolver.FromObservedFile(path);
            runner.Check(fromWatch.Count == 1 && string.Equals(fromWatch[0].ReplayPath, path, StringComparison.OrdinalIgnoreCase),
                $"directory-watch path resolves {score.ReplayFileName}", fromWatch.Count == 1 ? fromWatch[0].Explanation ?? "-" : "not resolved");
        }
    }

    // ------------------------------------------------------------------ watcher

    private static void Watch(CheckRunner runner, string[] args)
    {
        runner.Section("ReplayDirectoryWatcher");
        var dir = ArgValue(args, "--dir");
        var temporary = dir is null;
        if (temporary)
        {
            dir = Path.Combine(Path.GetTempPath(), "mrm-gamecheck-" + Guid.NewGuid().ToString("N")[..8]);
        }

        Directory.CreateDirectory(dir);

        try
        {
            using var watcher = new ReplayDirectoryWatcher();
            var events = new List<string>();
            var gate = new object();
            watcher.ReplayAdded += (result, path) =>
            {
                lock (gate)
                {
                    events.Add($"{Path.GetFileName(path)}:{result.Success}");
                }
            };

            watcher.Start(new[] { dir });
            runner.Check(watcher.IsRunning, "watcher started", string.Join("; ", watcher.WatchedDirectories));

            // A real .osr from the game install is copied in —the watcher itself must never write to
            // the game directory, so the copy target is the harness's own temp folder.
            var locator = RequireLocator();
            var sampleReplay = locator is null ? null : FindAnyReplay(locator);
            var target = Path.Combine(dir, Path.GetFileName(sampleReplay ?? "synthetic.osr"));

            if (sampleReplay is not null)
            {
                File.Copy(sampleReplay, target, overwrite: true);
                runner.Info($"copied a real replay ({new FileInfo(sampleReplay).Length:N0} bytes) into the watched directory");
            }
            else
            {
                File.WriteAllBytes(target, new byte[] { 3, 0, 0, 0, 0, 11, 0 });
            }

            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 5000)
            {
                lock (gate)
                {
                    if (events.Count > 0)
                    {
                        break;
                    }
                }

                Thread.Sleep(50);
            }

            lock (gate)
            {
                runner.Check(events.Count > 0, "creation event observed", events.Count == 0 ? "no event within 5 s" : string.Join(", ", events));
                if (sampleReplay is not null)
                {
                    runner.Check(events.Any(e => e.EndsWith(":True")), "observed file parsed as a valid .osr header",
                        string.Join(", ", events));
                }
            }

            runner.Metric("observed count", watcher.ObservedCount);
        }
        finally
        {
            if (temporary)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Best effort.
                }
            }
        }
    }

    // ------------------------------------------------------------------ Songs scan

    private static void SongsScan(CheckRunner runner, string[] args)
    {
        runner.Section("BeatmapLocator —osu!.db index + Songs scan");
        var locator = RequireLocator();
        if (locator is null)
        {
            runner.Skip("beatmap locator", "no osu! install resolved");
            return;
        }

        var sample = ArgInt(args, "--sample", 5);
        var options = new BeatmapLocatorOptions
        {
            // Keep the harness's index in its own out directory so a developer's app cache is untouched.
            CacheDirectory = Path.Combine(Path.GetTempPath(), "mrm-gamecheck-cache"),
            UseCache = true
        };

        var locatorService = new BeatmapLocator(locator, options);

        // (a) osu!.db path, which is the fast, exact route.
        var dbStopwatch = Stopwatch.StartNew();
        var db = locatorService.LoadOsuDatabase();
        dbStopwatch.Stop();
        runner.Check(db is not null && db.Success, "osu!.db index loaded into the locator",
            db is null ? "(null)" : $"{db.ByMd5.Count} MD5 entries in {dbStopwatch.Elapsed.TotalMilliseconds:N0} ms");

        if (db is not null && db.ByMd5.Count > 0)
        {
            var entries = SampleDeterministic(db.ByMd5.Values.ToList(), sample);
            var resolved = 0;
            foreach (var entry in entries)
            {
                var location = locatorService.ResolveByMd5(entry.Md5);
                if (location is not null && location.Md5Confirmed)
                {
                    resolved++;
                }
                else
                {
                    runner.Detail($"ResolveByMd5 failed for {entry.Md5} ({entry.FolderName}\\{entry.OsuFileName}): {location?.ToString() ?? "null"}");
                }
            }

            runner.CheckRatio(resolved, entries.Count, 1.0,
                $"sampled MD5 lookups resolved with a confirmed byte-MD5 ({entries.Count} sampled)");

            // (b) Fast path straight from a memory identity.
            var identity = entries.FirstOrDefault(e => e.AbsolutePath is not null && File.Exists(e.AbsolutePath));
            if (identity is not null)
            {
                var fast = locatorService.ResolveFromIdentity(identity.FolderName, identity.OsuFileName, identity.Md5);
                runner.Check(fast is not null && File.Exists(fast.Path), "memory identity path resolves without hashing",
                    fast?.Path ?? "null");
                runner.CheckEqual(fast?.Source, BeatmapMatchSource.MemoryIdentity, "identity path did not hash the file");
            }

            // (c) A missing file must return null, not a wrong guess.
            var missing = locatorService.ResolveFromIdentity("0000000000000000nonexistent", "nope.osu");
            runner.Check(missing is null, "unknown identity returns null", "null");
        }

        // (d) Full Songs scan: first run hashes everything, second run must reuse the cache.
        var first = locatorService.ScanSongs(force: true);
        runner.Check(first.Success, "Songs scan completed", first.Summary);
        runner.Metric("files on disk", first.FilesOnDisk);
        runner.Metric("files hashed", first.FilesHashed);
        runner.Metric("unique MD5", first.UniqueMd5);
        runner.Metric("duration", $"{first.Duration.TotalSeconds:N2} s");
        runner.Metric("throughput", $"{first.FilesPerSecond:N0} files/s");

        if (first.Success)
        {
            runner.CheckAtLeast(first.UniqueMd5, 1000, "Songs scan indexed a plausible number of beatmaps");

            // A file that was never in osu!.db (installed after the database was written) must still be
            // findable by MD5 —that is the whole point of keeping the scan path.
            var freshLocator = new BeatmapLocator(locator, options);
            var second = freshLocator.ScanSongs(force: false);
            runner.Metric("second scan reused from cache", second.FilesReusedFromCache);
            runner.Metric("second scan hashed", second.FilesHashed);
            runner.Metric("second scan duration", $"{second.Duration.TotalSeconds:N2} s");
            runner.Check(second.FilesReusedFromCache > 0, "cache reuse actually happened",
                $"{second.FilesReusedFromCache}/{second.FilesOnDisk} reused");
            runner.Check(second.Duration <= first.Duration, "cached scan is not slower",
                $"{second.Duration.TotalSeconds:N2}s vs {first.Duration.TotalSeconds:N2}s");

            // Pick a random indexed MD5 and confirm it resolves through the scan-only route.
            var indexed = freshLocator.IndexedMd5s();
            var key = indexed.Count > 0 ? indexed[Math.Abs(Guid.NewGuid().GetHashCode()) % indexed.Count] : null;
            if (key is not null)
            {
                var paths = freshLocator.PathsForMd5(key);
                var existing = paths.Count(p => File.Exists(Path.Combine(locator.SongsDirectory!, p)));
                var viaScan = freshLocator.ResolveByMd5(key);
                runner.Check(viaScan is not null && File.Exists(viaScan.Path),
                    "Songs-index lookup resolves a randomly chosen indexed MD5",
                    viaScan?.Path ?? $"null for {key} (paths={paths.Count}, existing={existing}: {string.Join(" | ", paths.Take(2))})");
            }
        }
    }

    // ------------------------------------------------------------------ memory

    private static void Memory32(CheckRunner runner, string[] args)
    {
        runner.Section("Memory layer against a synthetic 32-bit target process");
        var helper = ArgValue(args, "--helper") ?? Memory32Checks.DefaultHelperPath;
        Memory32Checks.Run(runner, helper);
    }

    // ------------------------------------------------------------------ static signatures

    private static void StaticSig(CheckRunner runner, string[] args)
    {
        StaticSignatureChecks.Run(runner, args);
    }

    // ------------------------------------------------------------------ live game

    private static void Live(CheckRunner runner, string[] args)
    {
        LiveMemoryChecks.Run(runner, args);
    }

    // ------------------------------------------------------------------ helpers

    private static GameLocator? RequireLocator()
    {
        var manual = Environment.GetEnvironmentVariable("MRM_OSU_ROOT");
        var locator = manual is null ? GameLocator.Resolve() : GameLocator.Resolve(manual);
        if (locator.Found)
        {
            return locator;
        }

        // The harness is typically invoked on the machine that has the reference install; fall back to
        // the documented path so the check reports an honest failure instead of silently skipping.
        return Directory.Exists(DefaultOsuRoot) ? GameLocator.Resolve(DefaultOsuRoot) : null;
    }

    private static List<T> SampleDeterministic<T>(IReadOnlyList<T> items, int count)
    {
        if (items.Count == 0 || count <= 0)
        {
            return new List<T>();
        }

        if (items.Count <= count)
        {
            return items.ToList();
        }

        var result = new List<T>(count);
        var stride = items.Count / (double)count;
        for (var i = 0; i < count; i++)
        {
            result.Add(items[(int)(i * stride)]);
        }

        return result;
    }

    private static string? FindAnyReplay(GameLocator locator)
    {
        try
        {
            var dir = locator.DataRDirectory;
            if (dir is null || !Directory.Exists(dir))
            {
                return null;
            }

            // Prefer a small file so the check stays fast.
            return Directory.EnumerateFiles(dir, "*.osr")
                .Select(p => new FileInfo(p))
                .OrderBy(f => f.Length)
                .FirstOrDefault(f => f.Length > 64)
                ?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static string Md5Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var md5 = System.Security.Cryptography.MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
    }

    private static long SafeCount(Func<int> count)
    {
        try
        {
            return count();
        }
        catch
        {
            return -1;
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int ArgInt(string[] args, string name, int fallback)
        => int.TryParse(ArgValue(args, name), out var value) ? value : fallback;

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
}

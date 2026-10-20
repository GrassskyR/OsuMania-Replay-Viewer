using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManiaReplayMaster.Game;

/// <summary>Options for the header scan and its cache.</summary>
public sealed class ReplayResolverOptions
{
    public static ReplayResolverOptions Default { get; } = new();

    /// <summary>Where the header index cache lives; defaults next to the Songs index.</summary>
    public string? CacheDirectory { get; init; }

    /// <summary>Read parallelism for the header scan. Header-only reads are I/O bound; 8 is plenty.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 4, 12);

    /// <summary>
    /// Reuse a cached header when the file's size and last-write time are unchanged. The scan walks
    /// ~10,000 files, so this is what keeps a second run under a second.
    /// </summary>
    public bool UseCache { get; init; } = true;

    /// <summary>How many candidates to return (the GUI shows the first and offers the rest in a dropdown).</summary>
    public int MaxCandidates { get; init; } = 20;

    /// <summary>When a scan finds more than this many MD5 matches, only the best are materialised.</summary>
    public int MaxMatchesPerQuery { get; init; } = 64;

    public Action<int, int>? Progress { get; init; }
}

/// <summary>Weights of the level-3 scoring described in plan 鎼?.3. Kept as data so they can be tuned.</summary>
public sealed class ReplayMatchWeights
{
    public static ReplayMatchWeights Default { get; } = new();

    /// <summary>A different beatmap MD5 is a hard reject, not a low score.</summary>
    public double BeatmapMd5 { get; init; } = 0;

    public double Mods { get; init; } = 3;

    public double Player { get; init; } = 3;

    public double Score { get; init; } = 5;

    public double MaxCombo { get; init; } = 2;

    public double HitCounts { get; init; } = 4;

    /// <summary>Play timestamp vs the requested date; tolerates a few seconds of clock difference.</summary>
    public double Date { get; init; } = 4;

    /// <summary>File mtime vs the requested date —weakest signal, only used for tie-breaking.</summary>
    public double ModifiedTime { get; init; } = 1;

    public double MaxTotal => Mods + Player + Score + MaxCombo + HitCounts + Date + ModifiedTime;
}

/// <summary>
/// Resolves "which score / which replay file" for a beatmap, implementing the matching strategy of
/// plan 鎼?.3:
///
/// <list type="number">
/// <item><b>scores.db pointer</b> —exact: the record's own <c>Data\r</c> name is derived from its
/// beatmap MD5 and play timestamp, and confirmed by parsing the candidate's header.</item>
/// <item><b>Directory watch</b> —handled by <see cref="ReplayDirectoryWatcher"/>; this class exposes
/// the header-based matching used on the observed file.</item>
/// <item><b>Full header scan + weighted scoring</b> —every <c>.osr</c> under <c>Data\r</c> and
/// <c>Replays\</c> (10,215 files on the reference machine) is indexed header-only, in parallel, with a
/// size+mtime-invalidated cache so repeat runs are cheap.</item>
/// </list>
///
/// The beatmap MD5 is a hard filter: candidates whose header disagrees on the beatmap are never
/// returned, because a wrong replay renders the wrong map silently. All other fields only influence
/// the ranking, and every candidate carries an explanation of what matched.
/// </summary>
public sealed class ReplayResolver
{
    private readonly Dictionary<string, List<ReplayHeaderIndexEntry>> _headerIndexByMd5 =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReplayHeaderIndexEntry> _headerIndex = new();
    private readonly object _gate = new();

    public ReplayResolver(
        GameLocator locator,
        ScoreDatabaseProvider? scoreProvider = null,
        ReplayResolverOptions? options = null)
    {
        Locator = locator;
        ScoreProvider = scoreProvider ?? new ScoreDatabaseProvider(locator);
        Options = options ?? ReplayResolverOptions.Default;
        CachePath = Path.Combine(
            Options.CacheDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ManiaReplayMaster"),
            "replay-header-index.json");
    }

    public GameLocator Locator { get; }

    public ScoreDatabaseProvider ScoreProvider { get; }

    public ReplayResolverOptions Options { get; }

    public string CachePath { get; }

    public ReplayMatchWeights Weights { get; init; } = ReplayMatchWeights.Default;

    /// <summary>Statistics of the last scan.</summary>
    public ReplayIndexScanResult? LastScan { get; private set; }

    /// <summary>Number of headers currently indexed.</summary>
    public int IndexedHeaderCount
    {
        get
        {
            lock (_gate)
            {
                return _headerIndex.Count;
            }
        }
    }

    /// <summary>Statistics of the last <c>scores.db</c> read performed by the resolver.</summary>
    public ScoreDatabaseResult? LastScoreDatabaseResult => ScoreProvider.LastResult;

    // ---------------------------------------------------------------- level 1

    /// <summary>
    /// Level 1: exact candidates from the <c>scores.db</c> pointer. The best candidate is the one
    /// whose record fields agree with the snapshot; when memory gives nothing to compare, all local
    /// scores of that beatmap are returned, newest first, so the GUI can offer them.
    /// </summary>
    public IReadOnlyList<ReplayCandidate> FromScoresDatabase(GameSnapshot? snapshot, string? beatmapMd5)
    {
        var md5 = (beatmapMd5 ?? snapshot?.BeatmapMd5)?.ToLowerInvariant();
        if (md5 is null)
        {
            return Array.Empty<ReplayCandidate>();
        }

        var scores = ScoreProvider.ForBeatmap(md5);
        if (scores.Count == 0)
        {
            return Array.Empty<ReplayCandidate>();
        }

        var candidates = new List<ReplayCandidate>(scores.Count);
        foreach (var score in scores)
        {
            var fileName = score.ReplayFileName;
            var path = Locator.ResolveReplayFile(fileName);
            var exists = path is not null;

            // The pointer is verified by parsing the header: a name collision or a recycled name must
            // not silently produce a wrong replay.
            OsuReplayHeader? header = null;
            string? explanation = null;
            double confidence;

            if (exists)
            {
                var parsed = OsuReplayHeaderReader.TryParse(path!);
                header = parsed.Header;

                if (header is null)
                {
                    explanation = $"file exists but header unreadable: {parsed.Error}";
                    confidence = 0.5;
                }
                else if (!string.Equals(header.BeatmapHash, md5, StringComparison.OrdinalIgnoreCase))
                {
                    // Name matched but the content belongs to another beatmap —do not trust it.
                    explanation = $"header beatmap MD5 {header.BeatmapHash} != requested {md5}";
                    confidence = 0.1;
                }
                else
                {
                    var (score01, why) = Score(snapshot, header, score, null);
                    confidence = score01;
                    explanation = why;
                }
            }
            else
            {
                explanation = "no file at the scores.db pointer";
                confidence = 0;
            }

            var info = path is not null && exists ? new FileInfo(path) : null;
            candidates.Add(new ReplayCandidate
            {
                ReplayPath = path ?? Path.Combine(Locator.DataRDirectory ?? string.Empty, fileName),
                Source = ReplayMatchSource.ScoresDbPointer,
                Confidence = confidence,
                RawScore = confidence * Weights.MaxTotal,
                Header = header,
                Score = score,
                Explanation = explanation,
                FileSizeBytes = info?.Length ?? 0,
                FileModified = info is null ? null : new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                FileExists = exists
            });
        }

        return Order(candidates);
    }

    /// <summary>Level 1 for a beatmap MD5 only (the memory-free path).</summary>
    public IReadOnlyList<ReplayCandidate> FromScoresDatabase(string beatmapMd5)
        => FromScoresDatabase(null, beatmapMd5);

    // ---------------------------------------------------------------- level 2

    /// <summary>
    /// Level 2 helper: turns a newly observed <c>.osr</c> (from <see cref="ReplayDirectoryWatcher"/>)
    /// into candidates. The obfuscated <c>Data\r</c> name already encodes the beatmap MD5, so this
    /// works with no <c>scores.db</c> entry and no memory access.
    /// </summary>
    public IReadOnlyList<ReplayCandidate> FromObservedFile(string replayPath, GameSnapshot? snapshot = null)
    {
        var result = ReplayDirectoryWatcher.ResolveExisting(replayPath, out var nameMd5, out _);
        if (!result.Success || result.Header is null)
        {
            return Array.Empty<ReplayCandidate>();
        }

        var header = result.Header;
        var md5 = header.BeatmapHash?.ToLowerInvariant() ?? nameMd5;

        if (snapshot?.BeatmapMd5 is { } requested
            && md5 is not null
            && !string.Equals(requested, md5, StringComparison.OrdinalIgnoreCase))
        {
            // A freshly written replay for a different beatmap than the one being looked at.
            return Array.Empty<ReplayCandidate>();
        }

        var (confidence, why) = Score(snapshot, header, null, null, freshlyWritten: true);
        var info = new FileInfo(replayPath);

        return new[]
        {
            new ReplayCandidate
            {
                ReplayPath = replayPath,
                Source = ReplayMatchSource.DirectoryWatch,
                Confidence = confidence,
                RawScore = confidence * Weights.MaxTotal,
                Header = header,
                Score = FindScoreByFileName(Path.GetFileName(replayPath)),
                Explanation = why,
                FileSizeBytes = info.Exists ? info.Length : 0,
                FileModified = info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null,
                FileExists = info.Exists
            }
        };
    }

    // ---------------------------------------------------------------- level 3

    /// <summary>
    /// Level 3: indexes every <c>.osr</c> header under <c>Data\r</c> and <c>Replays\</c> (headers only,
    /// in parallel, cached by size+mtime) and ranks the MD5 matches.
    /// </summary>
    public IReadOnlyList<ReplayCandidate> FromHeaderScan(GameSnapshot? snapshot, string? beatmapMd5, bool forceRescan = false)
    {
        var md5 = (beatmapMd5 ?? snapshot?.BeatmapMd5)?.ToLowerInvariant();
        if (md5 is null)
        {
            return Array.Empty<ReplayCandidate>();
        }

        EnsureIndex(forceRescan);

        List<ReplayHeaderIndexEntry> matches;
        lock (_gate)
        {
            matches = _headerIndexByMd5.TryGetValue(md5, out var list)
                ? list.Take(Options.MaxMatchesPerQuery).ToList()
                : new List<ReplayHeaderIndexEntry>();
        }

        if (matches.Count == 0)
        {
            // Fall back to the file-name convention: a Data\r name carries the beatmap MD5 even when
            // the header itself could not be read.
            lock (_gate)
            {
                matches = _headerIndex
                    .Where(e => e.NameBeatmapMd5 is not null
                                && string.Equals(e.NameBeatmapMd5, md5, StringComparison.OrdinalIgnoreCase))
                    .Take(Options.MaxMatchesPerQuery)
                    .ToList();
            }
        }

        var scores = ScoreProvider.ForBeatmap(md5);
        var candidates = new List<ReplayCandidate>(matches.Count);

        foreach (var match in matches)
        {
            ScoreRecord? score = null;
            if (match.NameFiletime is { } ft)
            {
                score = scores.FirstOrDefault(s => s.ReplayFiletime == ft);
            }

            var (confidence, why) = Score(snapshot, match.Header, score, match.FileModified);
            candidates.Add(new ReplayCandidate
            {
                ReplayPath = match.Path,
                Source = ReplayMatchSource.HeaderScan,
                Confidence = confidence,
                RawScore = confidence * Weights.MaxTotal,
                Header = match.Header,
                Score = score,
                Explanation = why,
                FileSizeBytes = match.FileSizeBytes,
                FileModified = match.FileModified,
                FileExists = File.Exists(match.Path)
            });
        }

        return Order(candidates);
    }

    // ---------------------------------------------------------------- combined

    /// <summary>
    /// Runs the strategy in order and stops at the first level that produces a *verified* candidate
    /// (a <c>scores.db</c> pointer whose header matched). Falls through to a full header scan when the
    /// pointer is missing or unverified, so a score whose <c>scores.db</c> entry was lost is still
    /// found. Returns ranked candidates, never a single answer.
    /// </summary>
    public ReplayResolution Resolve(GameSnapshot? snapshot, string? beatmapMd5 = null, bool allowScan = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var md5 = (beatmapMd5 ?? snapshot?.BeatmapMd5)?.ToLowerInvariant();
        var attempts = new List<string>();

        if (md5 is null)
        {
            return new ReplayResolution
            {
                RequestedMd5 = null,
                Candidates = Array.Empty<ReplayCandidate>(),
                NotFoundReason = "no beatmap MD5 available (memory unavailable and none supplied)",
                Attempts = attempts,
                Duration = stopwatch.Elapsed
            };
        }

        var candidates = new List<ReplayCandidate>();

        // Level 1.
        var pointer = FromScoresDatabase(snapshot, md5);
        candidates.AddRange(pointer);
        attempts.Add($"scores.db pointer: {pointer.Count} candidate(s)");

        var verified = pointer.Where(c => c.Confidence >= 0.99).ToList();
        if (verified.Count > 0)
        {
            stopwatch.Stop();
            return new ReplayResolution
            {
                RequestedMd5 = md5,
                Candidates = Order(candidates),
                ResolvedBy = ReplayMatchSource.ScoresDbPointer,
                Attempts = attempts,
                Duration = stopwatch.Elapsed
            };
        }

        if (!allowScan)
        {
            stopwatch.Stop();
            return new ReplayResolution
            {
                RequestedMd5 = md5,
                Candidates = Order(candidates),
                ResolvedBy = candidates.Count > 0 ? ReplayMatchSource.ScoresDbPointer : null,
                NotFoundReason = candidates.Count > 0 ? null : "scores.db has no entry for this beatmap",
                Attempts = attempts,
                Duration = stopwatch.Elapsed
            };
        }

        // Level 3.
        var scan = FromHeaderScan(snapshot, md5);
        attempts.Add($"header scan: {scan.Count} candidate(s) of {IndexedHeaderCount} indexed");

        foreach (var candidate in scan)
        {
            if (!candidates.Any(c => string.Equals(c.ReplayPath, candidate.ReplayPath, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(candidate);
            }
        }

        stopwatch.Stop();
        var ordered = Order(candidates);

        return new ReplayResolution
        {
            RequestedMd5 = md5,
            Candidates = ordered,
            ResolvedBy = ordered.Count == 0
                ? null
                : ordered.MaxBy(c => c.Confidence)!.Source,
            NotFoundReason = ordered.Count == 0
                ? "no replay file on disk matches this beatmap (score was never saved, or Data\\r was cleared); press F2 in game to export it"
                : null,
            Attempts = attempts,
            Duration = stopwatch.Elapsed
        };
    }

    // ---------------------------------------------------------------- scan plumbing

    /// <summary>Builds or refreshes the header index. Cheap on repeat runs thanks to the mtime cache.</summary>
    public ReplayIndexScanResult EnsureIndex(bool force = false)
    {
        if (!force && LastScan is not null && LastScan.Success)
        {
            return LastScan;
        }

        var stopwatch = Stopwatch.StartNew();
        var directories = new[] { Locator.DataRDirectory, Locator.ReplaysDirectory }
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Select(d => d!)
            .ToList();

        if (directories.Count == 0)
        {
            var none = new ReplayIndexScanResult
            {
                Error = "neither Data\\r nor Replays\\ exists",
                Duration = stopwatch.Elapsed
            };
            LastScan = none;
            return none;
        }

        var cached = LoadCache();

        var files = new List<string>();
        foreach (var dir in directories)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(dir, "*.osr", SearchOption.AllDirectories));
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var error = new ReplayIndexScanResult
                {
                    Error = $"{dir}: {ex.GetType().Name}: {ex.Message}",
                    Duration = stopwatch.Elapsed
                };
                LastScan = error;
                return error;
            }
        }

        var entries = new ConcurrentDictionary<string, ReplayHeaderIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var reused = 0;
        var parsed = 0;
        var failed = 0;
        var done = 0;

        Parallel.ForEach(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Options.MaxDegreeOfParallelism) },
            file =>
            {
                var current = Interlocked.Increment(ref done);
                if (Options.Progress is not null && current % 250 == 0)
                {
                    Options.Progress(current, files.Count);
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists)
                    {
                        Interlocked.Increment(ref failed);
                        return;
                    }
                }
                catch
                {
                    Interlocked.Increment(ref failed);
                    return;
                }

                if (cached.TryGetValue(file, out var hit)
                    && hit.FileSizeBytes == info.Length
                    && hit.ModifiedUtcTicks == info.LastWriteTimeUtc.Ticks)
                {
                    entries[file] = hit;
                    Interlocked.Increment(ref reused);
                    return;
                }

                var result = OsuReplayHeaderReader.TryParse(file);
                Interlocked.Increment(ref parsed);

                ReplayHeaderIndexEntry entry;
                if (result.Success && result.Header is not null)
                {
                    var header = result.Header;
                    OsuReplayHeaderReader.IsObfuscatedReplayName(Path.GetFileName(file), out var nameMd5, out var nameFiletime);
                    entry = new ReplayHeaderIndexEntry
                    {
                        Path = file,
                        FileSizeBytes = info.Length,
                        ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks,
                        FileModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                        Header = header,
                        NameBeatmapMd5 = nameMd5,
                        NameFiletime = nameFiletime
                    };
                }
                else
                {
                    Interlocked.Increment(ref failed);
                    OsuReplayHeaderReader.IsObfuscatedReplayName(Path.GetFileName(file), out var nameMd5, out var nameFiletime);
                    entry = new ReplayHeaderIndexEntry
                    {
                        Path = file,
                        FileSizeBytes = info.Length,
                        ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks,
                        FileModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                        Header = null,
                        NameBeatmapMd5 = nameMd5,
                        NameFiletime = nameFiletime,
                        Error = result.Error
                    };
                }

                entries[file] = entry;
            });

        var byMd5 = new Dictionary<string, List<ReplayHeaderIndexEntry>>(StringComparer.OrdinalIgnoreCase);
        var all = new List<ReplayHeaderIndexEntry>(entries.Count);
        foreach (var entry in entries.Values)
        {
            all.Add(entry);
            var md5 = entry.Header?.BeatmapHash?.ToLowerInvariant() ?? entry.NameBeatmapMd5;
            if (md5 is null)
            {
                continue;
            }

            if (!byMd5.TryGetValue(md5, out var list))
            {
                list = new List<ReplayHeaderIndexEntry>();
                byMd5[md5] = list;
            }

            list.Add(entry);
        }

        lock (_gate)
        {
            _headerIndex.Clear();
            _headerIndex.AddRange(all);
            _headerIndexByMd5.Clear();
            foreach (var (md5, list) in byMd5)
            {
                list.Sort((a, b) =>
                {
                    var am = a.FileModified ?? DateTimeOffset.MinValue;
                    var bm = b.FileModified ?? DateTimeOffset.MinValue;
                    return bm.CompareTo(am);
                });
                _headerIndexByMd5[md5] = list;
            }
        }

        stopwatch.Stop();

        var scan = new ReplayIndexScanResult
        {
            Directories = directories,
            FilesFound = files.Count,
            HeadersParsed = parsed,
            HeadersReusedFromCache = reused,
            HeadersFailed = failed,
            DistinctBeatmapMd5 = byMd5.Count,
            Duration = stopwatch.Elapsed
        };

        LastScan = scan;
        SaveCache(scan.Duration.TotalSeconds);
        return scan;
    }

    private Dictionary<string, ReplayHeaderIndexEntry> LoadCache()
    {
        var empty = new Dictionary<string, ReplayHeaderIndexEntry>(StringComparer.OrdinalIgnoreCase);
        if (!Options.UseCache || !File.Exists(CachePath))
        {
            return empty;
        }

        try
        {
            using var stream = File.OpenRead(CachePath);
            var cache = JsonSerializer.Deserialize(stream, GameJsonContext.Default.ReplayHeaderIndexCache);
            if (cache is null || cache.SchemaVersion != ReplayHeaderIndexCache.CurrentSchemaVersion)
            {
                return empty;
            }

            var result = new Dictionary<string, ReplayHeaderIndexEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, entry) in cache.Entries)
            {
                // The dictionary key IS the file path; the DTO does not carry it separately.
                // Passing it through matters because the entry is written back out using
                // entry.Path as the key -- dropping it here poisons the cache with an
                // empty-string key that can never match again.
                if (string.IsNullOrEmpty(path) || entry is null)
                {
                    continue;
                }

                result[path] = entry.ToIndexEntry(path);
            }

            return result;
        }
        catch
        {
            return empty;
        }
    }

    private void SaveCache(double seconds)
    {
        if (!Options.UseCache)
        {
            return;
        }

        try
        {
            List<ReplayHeaderIndexEntry> snapshot;
            lock (_gate)
            {
                snapshot = _headerIndex.ToList();
            }

            var cache = new ReplayHeaderIndexCache
            {
                GeneratedUtc = DateTimeOffset.UtcNow,
                ScanSeconds = seconds
            };

            foreach (var entry in snapshot)
            {
                // Guard the invariant at the write side too: an entry with no path can never be
                // matched on reload, and would be resurrected under an empty-string key.
                if (string.IsNullOrEmpty(entry.Path))
                {
                    continue;
                }

                cache.Entries[entry.Path] = ReplayHeaderIndexEntryDto.From(entry);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache, GameJsonContext.Default.ReplayHeaderIndexCache));
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch
        {
            // Caching is best-effort only.
        }
    }

    private ScoreRecord? FindScoreByFileName(string fileName)
    {
        if (!OsuReplayHeaderReader.IsObfuscatedReplayName(fileName, out var md5, out var filetime) || md5 is null || filetime is null)
        {
            return null;
        }

        return ScoreProvider.ForBeatmap(md5).FirstOrDefault(s => s.ReplayFiletime == filetime.Value);
    }

    private static IReadOnlyList<ReplayCandidate> Order(IEnumerable<ReplayCandidate> candidates)
        => candidates
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.FileExists)
            .ThenByDescending(c => c.Score?.DateTicks ?? c.Header?.TimestampTicks ?? 0)
            .ToList();

    /// <summary>
    /// The plan 鎼?.3 level-3 score. Returns a 0..1 confidence and a per-field explanation. The
    /// beatmap MD5 is assumed equal (a mismatch is filtered before this is called, and reported
    /// separately when it is discovered during verification).
    /// </summary>
    internal (double Confidence, string Explanation) Score(
        GameSnapshot? snapshot,
        OsuReplayHeader? header,
        ScoreRecord? score,
        DateTimeOffset? fileModified,
        bool freshlyWritten = false)
    {
        var why = new List<string>();
        double total = 0;
        double max = 0;
        var strongEvidence = new List<string>();

        void Compare(string field, double weight, bool? equal, string? detail = null, bool strong = false)
        {
            max += weight;
            if (equal is null)
            {
                return;
            }

            if (equal.Value)
            {
                total += weight;
                why.Add($"{field}=match{(detail is null ? string.Empty : $"({detail})")}");
                if (strong)
                {
                    strongEvidence.Add(field);
                }
            }
            else
            {
                why.Add($"{field}=DIFF{(detail is null ? string.Empty : $"({detail})")}");
            }
        }

        var mods = snapshot?.Mods;
        var headerMods = header?.Mods ?? score?.Mods;
        Compare("mods", Weights.Mods, mods is null || headerMods is null ? null : mods == headerMods,
            headerMods is null ? null : ScoreRecord.ModsToString(headerMods.Value));

        var player = snapshot?.PlayerName;
        // Empty names are valid for local/offline scores. A parsed file's null string marker also
        // represents an empty name; a null snapshot name may mean the memory read was unavailable.
        var headerPlayer = header is not null ? header.Username ?? string.Empty
            : score is not null ? score.PlayerName ?? string.Empty : null;
        Compare("player", Weights.Player,
            player is null || headerPlayer is null
                ? null
                : string.Equals(player, headerPlayer, StringComparison.OrdinalIgnoreCase),
            headerPlayer);

        var scoreValue = snapshot?.Score;
        var headerScore = header?.Score ?? score?.TotalScore;
        Compare("score", Weights.Score, scoreValue is null || headerScore is null ? null : scoreValue == headerScore,
            headerScore?.ToString("N0"), strong: true);

        var combo = snapshot?.MaxCombo;
        var headerCombo = header?.MaxCombo ?? score?.MaxCombo;
        Compare("combo", Weights.MaxCombo, combo is null || headerCombo is null ? null : combo == headerCombo,
            headerCombo?.ToString());

        var hits = snapshot?.HitCounts;
        if (hits is not null)
        {
            var candidateHits = header is not null
                ? header.HitCounts
                : score is not null
                    ? score.HitCounts
                    : null;
            if (candidateHits is not null)
            {
                Compare("hits", Weights.HitCounts, hits.SequenceEqual(candidateHits), string.Join('/', candidateHits),
                    strong: true);
            }
        }

        var date = snapshot?.ScoreDate;
        var ticks = header?.TimestampTicks ?? score?.DateTicks;
        if (date is not null && ticks is > 0)
        {
            var delta = Math.Abs((new DateTimeOffset(ticks.Value, TimeSpan.Zero) - date.Value).TotalSeconds);
            Compare("date", Weights.Date, delta <= 2, $"delta {delta:N1}s", strong: true);
        }

        if (fileModified is not null && date is not null)
        {
            // The Data\r file is written immediately after the play, so mtime tracks the play time.
            var delta = Math.Abs((fileModified.Value - date.Value).TotalSeconds);
            Compare("mtime", Weights.ModifiedTime, delta <= 300, $"delta {delta:N0}s");
        }

        // A null name can also come from an offline score's null CLR string. Only waive that
        // missing field when score, full hit counts and play date independently agree.
        if (player is null && strongEvidence.Contains("score") && strongEvidence.Contains("hits")
            && strongEvidence.Contains("date"))
        {
            max -= Weights.Player;
            why.Add("player=unavailable (score/hits/date match)");
        }

        if (max <= 0)
        {
            // Nothing to compare: the score is only as good as the structural match. A freshly
            // observed file is a strong signal, an arbitrary MD5 match is not.
            var fallback = freshlyWritten ? 0.75 : 0.4;
            why.Add(freshlyWritten ? "freshly written file" : "no comparable fields (memory unavailable)");
            return (fallback, string.Join(", ", why));
        }

        var confidence = Math.Clamp(total / max, 0, 1);

        // A field the snapshot could not provide is not evidence either way. It lowers the ratio because
        // the denominator keeps its weight — deliberately, since a missing player name really does make
        // the match weaker. The ratio alone must not, however, be turned into "this is the score you are
        // looking at": that decision requires at least one *strong* discriminator (score, date, or the
        // hit counts) to have actually been compared. Without one, the candidate list stays usable but
        // auto-fill is capped below the confident threshold.
        if (strongEvidence.Count == 0)
        {
            why.Add("no strong discriminator (score/date/hits) available to compare");
            confidence = Math.Min(confidence, 0.7);
        }
        else
        {
            why.Add($"strong={string.Join('+', strongEvidence)}");
            if (confidence >= 0.999 && snapshot is not null)
            {
                // Every comparable field agreed, including at least one strong discriminator.
                confidence = 1.0;
            }
        }

        return (confidence, string.Join(", ", why));
    }
}

/// <summary>Ranked candidates plus the reason nothing was found — never just "null".</summary>
public sealed class ReplayResolution
{
    public string? RequestedMd5 { get; init; }

    public required IReadOnlyList<ReplayCandidate> Candidates { get; init; }

    /// <summary>Which level produced the best candidate; null when nothing was found.</summary>
    public ReplayMatchSource? ResolvedBy { get; init; }

    public string? NotFoundReason { get; init; }

    public IReadOnlyList<string> Attempts { get; init; } = Array.Empty<string>();

    public TimeSpan Duration { get; init; }

    public ReplayCandidate? Best => Candidates.Count > 0 ? Candidates[0] : null;

    public bool Found => Candidates.Count > 0;

    /// <summary>True when the top candidate is exact enough to auto-fill without asking the user.</summary>
    public bool IsConfident => Best is not null && Best.Confidence >= 0.9;

    /// <summary>True when several candidates are close enough that the GUI should offer a dropdown.</summary>
    public bool IsAmbiguous
        => Candidates.Count > 1 && Best is not null && Candidates[1].Confidence >= Best.Confidence - 0.05;

    public string Summary
    {
        get
        {
            if (Best is null)
            {
                return NotFoundReason ?? "no candidate";
            }

            return $"{Best.Confidence:P0} via {Best.Source} -> {Path.GetFileName(Best.ReplayPath)}" +
                   (IsAmbiguous ? $" (ambiguous, {Candidates.Count} candidates)" : string.Empty) +
                   (Explanation is null ? string.Empty : $" [{Explanation}]");
        }
    }

    public string? Explanation => Best?.Explanation;
}

/// <summary>One indexed <c>.osr</c> header.</summary>
public sealed class ReplayHeaderIndexEntry
{
    public required string Path { get; init; }

    public long FileSizeBytes { get; init; }

    public long ModifiedUtcTicks { get; init; }

    public DateTimeOffset? FileModified { get; init; }

    public OsuReplayHeader? Header { get; init; }

    /// <summary>Beatmap MD5 decoded from the <c>&lt;md5&gt;-&lt;filetime&gt;.osr</c> file name, when it has that shape.</summary>
    public string? NameBeatmapMd5 { get; init; }

    public long? NameFiletime { get; init; }

    public string? Error { get; init; }

    public string? EffectiveMd5 => Header?.BeatmapHash?.ToLowerInvariant() ?? NameBeatmapMd5;
}

/// <summary>Counters of a header scan, so the cost of level 3 can be reported honestly.</summary>
public sealed class ReplayIndexScanResult
{
    public IReadOnlyList<string> Directories { get; init; } = Array.Empty<string>();

    public int FilesFound { get; init; }

    public int HeadersParsed { get; init; }

    public int HeadersReusedFromCache { get; init; }

    public int HeadersFailed { get; init; }

    public int DistinctBeatmapMd5 { get; init; }

    public TimeSpan Duration { get; init; }

    public string? Error { get; init; }

    public bool Success => Error is null;

    public double FilesPerSecond => Duration.TotalSeconds > 0 ? FilesFound / Duration.TotalSeconds : 0;

    public string Summary =>
        $"{FilesFound} .osr found, {HeadersParsed} headers parsed, {HeadersReusedFromCache} from cache, " +
        $"{HeadersFailed} unreadable, {DistinctBeatmapMd5} distinct beatmaps, {Duration.TotalSeconds:N2}s " +
        $"({FilesPerSecond:N0} files/s){(Error is null ? string.Empty : $" —{Error}")}";
}

/// <summary>Serializable form of the header index; only the fields needed for matching are stored.</summary>
public sealed class ReplayHeaderIndexCache
{
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("generatedUtc")]
    public DateTimeOffset GeneratedUtc { get; set; }

    [JsonPropertyName("scanSeconds")]
    public double ScanSeconds { get; set; }

    [JsonPropertyName("entries")]
    public Dictionary<string, ReplayHeaderIndexEntryDto> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Compact persisted header record.</summary>
public sealed class ReplayHeaderIndexEntryDto
{
    [JsonPropertyName("md5")]
    public string? BeatmapHash { get; set; }

    [JsonPropertyName("size")]
    public long FileSizeBytes { get; set; }

    [JsonPropertyName("mtime")]
    public long ModifiedUtcTicks { get; set; }

    [JsonPropertyName("nameMd5")]
    public string? NameBeatmapMd5 { get; set; }

    [JsonPropertyName("nameFiletime")]
    public long? NameFiletime { get; set; }

    [JsonPropertyName("player")]
    public string? Username { get; set; }

    [JsonPropertyName("mode")]
    public byte Mode { get; set; }

    [JsonPropertyName("version")]
    public int FileVersion { get; set; }

    [JsonPropertyName("replayHash")]
    public string? ReplayHash { get; set; }

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("maxCombo")]
    public int MaxCombo { get; set; }

    [JsonPropertyName("mods")]
    public int Mods { get; set; }

    [JsonPropertyName("hits")]
    public int[]? Hits { get; set; }

    [JsonPropertyName("perfect")]
    public bool Perfect { get; set; }

    [JsonPropertyName("ticks")]
    public long TimestampTicks { get; set; }

    [JsonPropertyName("headerLength")]
    public long HeaderLength { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public static ReplayHeaderIndexEntryDto From(ReplayHeaderIndexEntry entry)
    {
        var header = entry.Header;
        return new ReplayHeaderIndexEntryDto
        {
            BeatmapHash = header?.BeatmapHash,
            FileSizeBytes = entry.FileSizeBytes,
            ModifiedUtcTicks = entry.ModifiedUtcTicks,
            NameBeatmapMd5 = entry.NameBeatmapMd5,
            NameFiletime = entry.NameFiletime,
            Username = header?.Username,
            Mode = (byte)(header?.Mode ?? GameMode.Unknown),
            FileVersion = header?.FileVersion ?? 0,
            ReplayHash = header?.ReplayHash,
            Score = header?.Score ?? 0,
            MaxCombo = header?.MaxCombo ?? 0,
            Mods = header?.Mods ?? 0,
            Hits = header?.HitCounts,
            Perfect = header?.Perfect ?? false,
            TimestampTicks = header?.TimestampTicks ?? 0,
            HeaderLength = header?.HeaderLength ?? 0,
            Error = entry.Error
        };
    }

    /// <summary>
    /// Rebuilds the in-memory entry. A cache hit is only trusted for size+mtime comparison, so the
    /// header reconstructed here is used for ranking; the caller re-parses whenever the file moved.
    /// </summary>
    public ReplayHeaderIndexEntry ToIndexEntry(string? path = null)
    {
        OsuReplayHeader? header = null;
        if (BeatmapHash is not null && Error is null && TimestampTicks > 0)
        {
            var hits = Hits ?? new[] { 0, 0, 0, 0, 0, 0 };
            header = new OsuReplayHeader
            {
                FilePath = path ?? string.Empty,
                FileSizeBytes = FileSizeBytes,
                Mode = (GameMode)Mode,
                FileVersion = FileVersion,
                BeatmapHash = BeatmapHash,
                Username = Username,
                ReplayHash = ReplayHash,
                Count300 = hits.Length > 0 ? hits[0] : 0,
                Count100 = hits.Length > 1 ? hits[1] : 0,
                Count50 = hits.Length > 2 ? hits[2] : 0,
                CountGeki = hits.Length > 3 ? hits[3] : 0,
                CountKatu = hits.Length > 4 ? hits[4] : 0,
                Misses = hits.Length > 5 ? hits[5] : 0,
                Score = Score,
                MaxCombo = MaxCombo,
                Perfect = Perfect,
                Mods = Mods,
                TimestampTicks = TimestampTicks,
                HeaderLength = HeaderLength
            };
        }

        return new ReplayHeaderIndexEntry
        {
            Path = path ?? string.Empty,
            FileSizeBytes = FileSizeBytes,
            ModifiedUtcTicks = ModifiedUtcTicks,
            FileModified = ModifiedUtcTicks > 0
                ? new DateTimeOffset(new DateTime(ModifiedUtcTicks, DateTimeKind.Utc))
                : null,
            Header = header,
            NameBeatmapMd5 = NameBeatmapMd5,
            NameFiletime = NameFiletime,
            Error = Error
        };
    }
}

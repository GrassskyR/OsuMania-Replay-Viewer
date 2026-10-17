using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ManiaReplayMaster.Core;

namespace ManiaReplayMaster.Game;

/// <summary>How a beatmap file was located (plan §3.2).</summary>
public enum BeatmapMatchSource
{
    /// <summary>Folder + <c>.osu</c> file name read straight out of the game's memory — no hashing at all.</summary>
    MemoryIdentity,

    /// <summary>MD5 looked up in the <c>osu!.db</c> index.</summary>
    OsuDatabase,

    /// <summary>MD5 found in the <c>Songs\*\*.osu</c> scan cache.</summary>
    SongsScanCache,

    /// <summary>MD5 found by a fresh walk of <c>Songs</c>.</summary>
    SongsScanFresh,

    /// <summary>Only the path resolved; the MD5 could not be confirmed on disk.</summary>
    PathOnly,

    /// <summary>Nothing found.</summary>
    NotFound
}

/// <summary>A resolved beatmap, including how confident the resolution is.</summary>
public sealed record BeatmapLocation
{
    public required string Path { get; init; }

    public required BeatmapMatchSource Source { get; init; }

    /// <summary>MD5 the caller asked for, when there was one.</summary>
    public string? RequestedMd5 { get; init; }

    /// <summary>MD5 actually observed on disk, when the file was hashed (may differ for user-edited maps).</summary>
    public string? VerifiedMd5 { get; init; }

    public long FileSizeBytes { get; init; }

    public DateTimeOffset? FileModified { get; init; }

    /// <summary>True when the on-disk byte MD5 equals <see cref="RequestedMd5"/>.</summary>
    public bool Md5Confirmed => RequestedMd5 is not null
                                && VerifiedMd5 is not null
                                && string.Equals(RequestedMd5, VerifiedMd5, StringComparison.OrdinalIgnoreCase);

    public string FileName => System.IO.Path.GetFileName(Path);

    public override string ToString() => $"{Source} {Path}";
}

/// <summary>Persisted <c>Songs</c> index, invalidated per entry by size + last-write time.</summary>
public sealed class SongsIndexCache
{
    /// <summary>Schema 2 keys <see cref="Entries"/> by relative path instead of by MD5, because two
    /// files with identical bytes share an MD5 and an MD5-keyed cache drops one of them.</summary>
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("root")]
    public string? Root { get; set; }

    [JsonPropertyName("generatedUtc")]
    public DateTimeOffset GeneratedUtc { get; set; }

    /// <summary>Relative path from the Songs root → entry.</summary>
    [JsonPropertyName("entries")]
    public Dictionary<string, SongsIndexEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("scanDurationSeconds")]
    public double ScanDurationSeconds { get; set; }

    [JsonIgnore]
    public int Count => Entries.Count;
}

/// <summary>One cached <c>.osu</c> file.</summary>
public sealed class SongsIndexEntry
{
    /// <summary>Lowercase byte MD5 recorded for this file.</summary>
    [JsonPropertyName("md5")]
    public string Md5 { get; set; } = string.Empty;

    /// <summary>Path relative to the Songs root (portable across a moved installation).</summary>
    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("modifiedUtcTicks")]
    public long ModifiedUtcTicks { get; set; }
}

/// <summary>Options for <see cref="BeatmapLocator"/>.</summary>
public sealed class BeatmapLocatorOptions
{
    public static BeatmapLocatorOptions Default { get; } = new();

    /// <summary>Where the persistent Songs index lives. Defaults to <c>%LOCALAPPDATA%\ManiaReplayMaster</c>.</summary>
    public string? CacheDirectory { get; init; }

    /// <summary>Hashing parallelism for a full <c>Songs</c> scan. 4–8 is right for an SSD/HDD mix.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>Reuse an existing index and only hash files whose size/mtime changed.</summary>
    public bool UseCache { get; init; } = true;

    public Action<int>? Progress { get; init; }
}

/// <summary>
/// Resolves a beatmap MD5 (or a folder + file name straight out of the game's memory) to an absolute
/// <c>.osu</c> path (plan §3.2).
///
/// Priority: in-memory identity path (free) → <c>osu!.db</c> index (one 26 MB walk, cached in RAM) →
/// persistent <c>Songs</c> scan index (incremental, invalidated per file by size + mtime) → a fresh
/// parallel walk. The cache is what makes the "MD5 only" fallback usable: hashing ~22,000 files takes
/// a few seconds once, and nothing afterwards.
/// </summary>
public sealed class BeatmapLocator
{
    private readonly Dictionary<string, List<string>> _songsByMd5 = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SongsIndexEntry> _songsCacheByRelative = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BeatmapIndexEntry> _dbByMd5 = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string?> _md5Provider;
    private readonly object _gate = new();

    public BeatmapLocator(
        GameLocator locator,
        BeatmapLocatorOptions? options = null,
        Func<string, string?>? md5Provider = null)
    {
        Locator = locator;
        Options = options ?? BeatmapLocatorOptions.Default;
        _md5Provider = md5Provider ?? Utils.GetMd5;
        CachePath = Path.Combine(
            Options.CacheDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ManiaReplayMaster"),
            "songs-index.json");
    }

    public GameLocator Locator { get; }

    public BeatmapLocatorOptions Options { get; }

    public string CachePath { get; }

    public string? SongsRoot => Locator.SongsDirectory;

    /// <summary>Number of MD5 → path entries known from the persistent cache.</summary>
    public int CachedEntryCount
    {
        get
        {
            lock (_gate)
            {
                return _songsByMd5.Count;
            }
        }
    }

    /// <summary>True once an <c>osu!.db</c> index has been loaded into memory.</summary>
    public bool HasOsuDatabaseIndex { get; private set; }

    /// <summary>Statistics of the last <c>osu!.db</c> load.</summary>
    public BeatmapDatabaseResult? DatabaseResult { get; private set; }

    /// <summary>Statistics of the last <c>Songs</c> scan.</summary>
    public SongsScanResult? LastScan { get; private set; }

    /// <summary>Loads the persistent Songs index (no hashing, just a JSON read).</summary>
    public bool LoadCache()
    {
        if (!Options.UseCache || !File.Exists(CachePath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(CachePath);
            var cache = JsonSerializer.Deserialize<SongsIndexCache>(stream);
            if (cache is null || cache.SchemaVersion != SongsIndexCache.CurrentSchemaVersion)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(SongsRoot)
                && !string.IsNullOrEmpty(cache.Root)
                && !string.Equals(Path.GetFullPath(cache.Root), Path.GetFullPath(SongsRoot), StringComparison.OrdinalIgnoreCase))
            {
                // A different installation; the cached relative paths are meaningless.
                return false;
            }

            lock (_gate)
            {
                _songsByMd5.Clear();
                _songsCacheByRelative.Clear();
                foreach (var (relative, entry) in cache.Entries)
                {
                    if (!string.IsNullOrEmpty(entry.Md5))
                    {
                        AddMd5Path(entry.Md5, relative);
                    }

                    _songsCacheByRelative[relative] = entry;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SaveCache(double scanSeconds)
    {
        if (!Options.UseCache || string.IsNullOrEmpty(SongsRoot))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var cache = new SongsIndexCache
            {
                Root = Path.GetFullPath(SongsRoot),
                GeneratedUtc = DateTimeOffset.UtcNow,
                ScanDurationSeconds = scanSeconds
            };

            lock (_gate)
            {
                foreach (var (relative, entry) in _songsCacheByRelative)
                {
                    var absolute = Path.Combine(SongsRoot, relative);
                    long size = 0;
                    long mtime = 0;
                    try
                    {
                        var info = new FileInfo(absolute);
                        if (info.Exists)
                        {
                            size = info.Length;
                            mtime = info.LastWriteTimeUtc.Ticks;
                        }
                    }
                    catch
                    {
                        // Keep the entry; the recorded MD5 is still the best information available.
                    }

                    cache.Entries[relative] = new SongsIndexEntry
                    {
                        Md5 = entry.Md5,
                        RelativePath = relative,
                        Size = size,
                        ModifiedUtcTicks = mtime
                    };
                }
            }

            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch
        {
            // A cache write failure must never fail a render.
        }
    }

    /// <summary>
    /// Loads the <c>osu!.db</c> index once. Failures are non-fatal: the Songs scan remains available.
    /// </summary>
    public BeatmapDatabaseResult? LoadOsuDatabase(bool force = false)
    {
        if (HasOsuDatabaseIndex && !force)
        {
            return DatabaseResult;
        }

        var path = Locator.OsuDbPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        var result = BeatmapDatabaseReader.Read(path, SongsRoot);
        DatabaseResult = result;
        lock (_gate)
        {
            _dbByMd5.Clear();
            foreach (var (md5, entry) in result.ByMd5)
            {
                _dbByMd5[md5] = entry;
            }
        }

        HasOsuDatabaseIndex = true;
        return result;
    }

    /// <summary>
    /// Fast path from the memory layer: the game already knows the folder and the <c>.osu</c> file
    /// name, so no hashing happens at all. The MD5 is still verified when
    /// <paramref name="verifyMd5"/> is set, because a user-edited map changes the bytes without
    /// changing the database record.
    /// </summary>
    public BeatmapLocation? ResolveFromIdentity(string? folder, string? osuFileName, string? beatmapMd5 = null, bool verifyMd5 = false)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(osuFileName))
        {
            return null;
        }

        var root = SongsRoot;
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        string path;
        try
        {
            path = Path.GetFullPath(Path.Combine(root, folder.Trim(), osuFileName.Trim()));
        }
        catch
        {
            return null;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        string? verified = null;
        if (verifyMd5 || beatmapMd5 is null)
        {
            verified = _md5Provider(path);
        }

        return new BeatmapLocation
        {
            Path = path,
            Source = verifyMd5 ? BeatmapMatchSource.SongsScanFresh : BeatmapMatchSource.MemoryIdentity,
            RequestedMd5 = beatmapMd5?.ToLowerInvariant(),
            VerifiedMd5 = verified ?? beatmapMd5?.ToLowerInvariant(),
            FileSizeBytes = info.Length,
            FileModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
        };
    }

    /// <summary>Resolves <paramref name="beatmapMd5"/> to a file, using every available strategy.</summary>
    public BeatmapLocation? ResolveByMd5(string beatmapMd5, bool allowFreshScan = false)
    {
        if (!OsuStrings.LooksLikeMd5(beatmapMd5))
        {
            return null;
        }

        var key = beatmapMd5.ToLowerInvariant();

        // 1. osu!.db index (loaded once).
        var db = LoadOsuDatabase();
        if (db is not null)
        {
            var entry = db.FindByMd5(key);
            var path = entry?.AbsolutePath;
            if (path is not null && File.Exists(path))
            {
                // The database records the MD5 at import time; a user-edited map no longer matches.
                var verified = _md5Provider(path);
                if (verified is not null && !string.Equals(verified, key, StringComparison.OrdinalIgnoreCase))
                {
                    // Trust the bytes, not the stale record: keep looking.
                    var viaScan = ResolveViaSongsIndex(key, allowFreshScan);
                    if (viaScan is not null)
                    {
                        return viaScan;
                    }

                    var info2 = new FileInfo(path);
                    return new BeatmapLocation
                    {
                        Path = path,
                        Source = BeatmapMatchSource.OsuDatabase,
                        RequestedMd5 = key,
                        VerifiedMd5 = verified,
                        FileSizeBytes = info2.Length,
                        FileModified = new DateTimeOffset(info2.LastWriteTimeUtc, TimeSpan.Zero)
                    };
                }

                var info = new FileInfo(path);
                return new BeatmapLocation
                {
                    Path = path,
                    Source = BeatmapMatchSource.OsuDatabase,
                    RequestedMd5 = key,
                    VerifiedMd5 = verified,
                    FileSizeBytes = info.Length,
                    FileModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                };
            }
        }

        // 2. Persistent Songs index / fresh walk.
        return ResolveViaSongsIndex(key, allowFreshScan);
    }

    private BeatmapLocation? ResolveViaSongsIndex(string key, bool allowFreshScan)
    {
        LoadCache();

        if (TryFromIndex(key, out var location))
        {
            return location;
        }

        if (!allowFreshScan)
        {
            return null;
        }

        ScanSongs(force: true);
        return TryFromIndex(key, out location) ? location : null;
    }

    private bool TryFromIndex(string key, out BeatmapLocation? location)
    {
        location = null;
        List<string>? relatives;
        lock (_gate)
        {
            _songsByMd5.TryGetValue(key, out relatives);
        }

        var root = SongsRoot;
        if (relatives is null || relatives.Count == 0 || string.IsNullOrEmpty(root))
        {
            return false;
        }

        foreach (var relative in relatives)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path))
            {
                // One of the duplicate-content copies was moved or deleted; try the next one.
                continue;
            }



            var info = new FileInfo(path);
            location = new BeatmapLocation
            {
                Path = path,
                Source = BeatmapMatchSource.SongsScanCache,
                RequestedMd5 = key,
                VerifiedMd5 = key,
                FileSizeBytes = info.Length,
                FileModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
            };
            return true;
        }

        return false;
    }

    /// <summary>Records one (MD5, relative path) pair, keeping every path for a repeated MD5.</summary>
    private void AddMd5Path(string md5, string relative)
    {
        if (!_songsByMd5.TryGetValue(md5, out var list))
        {
            list = new List<string>(1);
            _songsByMd5[md5] = list;
        }

        if (!list.Contains(relative, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(relative);
        }
    }

    /// <summary>
    /// Walks <c>Songs\*\*.osu</c>, hashing files in parallel and reusing cached results whose size and
    /// last-write time are unchanged. Returns per-run counters so the cost can be reported honestly.
    /// </summary>
    public SongsScanResult ScanSongs(bool force = false)
    {
        var root = SongsRoot;
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            var missing = new SongsScanResult
            {
                Root = root ?? string.Empty,
                Error = $"Songs directory '{root}' does not exist.",
                Duration = stopwatch.Elapsed
            };
            LastScan = missing;
            return missing;
        }

        if (!force && LastScan is not null && LastScan.Success)
        {
            return LastScan;
        }

        var previous = new Dictionary<string, SongsIndexEntry>(StringComparer.OrdinalIgnoreCase);
        if (Options.UseCache && File.Exists(CachePath))
        {
            try
            {
                using var stream = File.OpenRead(CachePath);
                var cache = JsonSerializer.Deserialize<SongsIndexCache>(stream);
                if (cache is not null && cache.SchemaVersion == SongsIndexCache.CurrentSchemaVersion)
                {
                    if (!string.IsNullOrEmpty(cache.Root)
                        && string.Equals(Path.GetFullPath(cache.Root), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                    {
                        previous = cache.Entries;
                    }
                }
            }
            catch
            {
                previous = new Dictionary<string, SongsIndexEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        // Reverse view of the cache so a file can be matched by relative path before deciding whether
        // its bytes have to be hashed again. Note the cache is keyed by relative path and *stores* the
        // MD5 — the two must not be swapped here, or every subsequent scan re-persists a path as an MD5.
        var previousByRelative = new Dictionary<string, (string Md5, long Size, long Mtime)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relativePath, entry) in previous)
        {
            previousByRelative[relativePath] = (entry.Md5, entry.Size, entry.ModifiedUtcTicks);
        }

        var found = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var freshRows = new ConcurrentDictionary<string, SongsIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var distinctMd5 = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var reused = 0;
        var hashed = 0;
        var failed = 0;
        var scanned = 0;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.osu", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            var failedEnumeration = new SongsScanResult
            {
                Root = root,
                Error = $"{ex.GetType().Name}: {ex.Message}",
                Duration = stopwatch.Elapsed
            };
            LastScan = failedEnumeration;
            return failedEnumeration;
        }

        var prefixLength = root.TrimEnd(Path.DirectorySeparatorChar).Length + 1;

        Parallel.ForEach(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Options.MaxDegreeOfParallelism) },
            file =>
            {
                var current = Interlocked.Increment(ref scanned);
                if (Options.Progress is not null && current % 500 == 0)
                {
                    Options.Progress(current);
                }

                string relative;
                try
                {
                    relative = file.Length > prefixLength ? file[prefixLength..] : Path.GetFileName(file);
                }
                catch
                {
                    relative = Path.GetFileName(file);
                }

                long size;
                long mtime;
                try
                {
                    var info = new FileInfo(file);
                    size = info.Length;
                    mtime = info.LastWriteTimeUtc.Ticks;
                }
                catch
                {
                    Interlocked.Increment(ref failed);
                    return;
                }

                string? md5 = null;

                // Size + last-write time unchanged => the cached MD5 is still the byte MD5, so the
                // file is not read at all. This is what makes the incremental rescan cheap.
                if (previousByRelative.TryGetValue(relative, out var cached)
                    && cached.Size == size
                    && cached.Mtime == mtime)
                {
                    md5 = cached.Md5;
                    distinctMd5[md5] = 1;
                    Interlocked.Increment(ref reused);
                }
                else
                {
                    md5 = _md5Provider(file);
                    if (md5 is not null)
                    {
                        Interlocked.Increment(ref hashed);
                    }
                }

                if (md5 is null)
                {
                    Interlocked.Increment(ref failed);
                    return;
                }

                found[relative] = md5;
                distinctMd5[md5] = 1;
                freshRows[relative] = new SongsIndexEntry
                {
                    Md5 = md5,
                    RelativePath = relative,
                    Size = size,
                    ModifiedUtcTicks = mtime
                };
            });

        lock (_gate)
        {
            _songsByMd5.Clear();
            foreach (var (relative, md5) in found)
            {
                // One MD5 can belong to several files (identical bytes in different folders), so all
                // paths are kept and the first one that exists on disk wins at lookup time.
                AddMd5Path(md5, relative);
            }

            // The cache is kept per *file*, not per MD5: two files can share one MD5 when their bytes are
            // identical, and keying the cache only by MD5 would silently drop one of them and force a
            // rehash on every later scan (296 such files exist on the reference install).
            // Reused files keep their previous cache row (which is still valid by definition: size and
            // mtime are unchanged), so the next scan can skip them again.
            _songsCacheByRelative.Clear();
            foreach (var (relative, known) in previousByRelative)
            {
                _songsCacheByRelative[relative] = new SongsIndexEntry
                {
                    Md5 = known.Md5,
                    RelativePath = relative,
                    Size = known.Size,
                    ModifiedUtcTicks = known.Mtime
                };
            }

            foreach (var (relative, entry) in freshRows)
            {
                _songsCacheByRelative[relative] = entry;
            }
        }

        stopwatch.Stop();

        var result = new SongsScanResult
        {
            Root = root,
            FilesOnDisk = files.Count,
            FilesHashed = hashed,
            FilesReusedFromCache = reused,
            FilesFailed = failed,
            UniqueMd5 = distinctMd5.Count,
            PreviousCacheEntries = previous.Count,
            Duration = stopwatch.Elapsed
        };

        LastScan = result;
        SaveCache(result.Duration.TotalSeconds);
        return result;
    }

    /// <summary>Convenience: MD5 of a file, using the same provider the locator uses.</summary>
    public string? HashFile(string path) => _md5Provider(path);

    /// <summary>Any MD5 currently known to the Songs index (used by tests and the harness).</summary>
    public string? FirstIndexedMd5()
    {
        lock (_gate)
        {
            foreach (var md5 in _songsByMd5.Keys)
            {
                return md5;
            }
        }

        return null;
    }

    /// <summary>All MD5s currently known to the Songs index.</summary>
    public IReadOnlyList<string> IndexedMd5s()
    {
        lock (_gate)
        {
            return _songsByMd5.Keys.ToList();
        }
    }

    /// <summary>All relative paths the index knows for one MD5 (usually exactly one).</summary>
    public IReadOnlyList<string> PathsForMd5(string md5)
    {
        lock (_gate)
        {
            return _songsByMd5.TryGetValue(md5, out var list) ? list.ToList() : Array.Empty<string>();
        }
    }

    /// <summary>Incremental variant used by the UI: rehash only the files whose size/mtime moved.</summary>
    public SongsScanResult RefreshSongs(bool force = false) => ScanSongs(force);
}

/// <summary>Counters of a <c>Songs</c> walk, for the "why is this slow" question.</summary>
public sealed class SongsScanResult
{
    public required string Root { get; init; }

    public int FilesOnDisk { get; init; }

    public int FilesHashed { get; init; }

    public int FilesReusedFromCache { get; init; }

    public int FilesFailed { get; init; }

    public int UniqueMd5 { get; init; }

    public int PreviousCacheEntries { get; init; }

    public TimeSpan Duration { get; init; }

    public string? Error { get; init; }

    public bool Success => Error is null;

    public double FilesPerSecond => Duration.TotalSeconds > 0 ? FilesHashed / Duration.TotalSeconds : 0;

    public string Summary =>
        $"{FilesOnDisk} .osu on disk, {FilesHashed} hashed ({FilesPerSecond:N0}/s), " +
        $"{FilesReusedFromCache} reused from cache, {UniqueMd5} unique MD5, " +
        $"{FilesFailed} failed, {Duration.TotalSeconds:N2}s{(Error is null ? string.Empty : $" — {Error}")}";
}

/// <summary>Utility used by the harness to compute a file MD5 without pulling in the locator.</summary>
internal static class Md5Util
{
    public static string Compute(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var md5 = MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
    }
}

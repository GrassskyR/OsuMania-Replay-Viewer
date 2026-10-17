using System.Diagnostics;

namespace ManiaReplayMaster.Game.Memory;

/// <summary>Options for <see cref="GameStateWatcher"/>.</summary>
public sealed class GameStateWatcherOptions
{
    public static GameStateWatcherOptions Default { get; } = new();

    /// <summary>Poll interval; the plan asks for ~100–200 ms.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How long to wait before re-trying after a signature/access failure (avoids busy-logging).</summary>
    public TimeSpan FailureBackoff { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Emit a snapshot on every poll instead of only when something changed.</summary>
    public bool EmitOnEveryPoll { get; init; }

    /// <summary>Watch for a game process that is not running yet.</summary>
    public bool WaitForProcess { get; init; } = true;

    public string ProcessName { get; init; } = GameLocator.ProcessName;

    /// <summary>Expected target pointer width; 4 for osu!stable (32-bit WOW64).</summary>
    public int ExpectedPointerSize { get; init; } = 4;

    /// <summary>Signature table to use; replaceable so a new game build only needs new data.</summary>
    public SignatureTable? Table { get; init; }
}

/// <summary>
/// Polls osu!stable and emits <see cref="GameSnapshot"/> values (plan §5.2 / §5.1).
///
/// The loop is deliberately boring and total: it never throws, it reports every failure mode through
/// <see cref="GameSnapshot.Status"/>, and it keeps the rest of the application usable when memory
/// reading is impossible (unknown build, no permission, game not running). Level 1 of the degradation
/// chain (plan §5.4) is unaffected by anything that happens here.
/// </summary>
public sealed class GameStateWatcher : IDisposable
{
    private readonly object _gate = new();
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private GameSnapshot _last = GameSnapshot.NotRunning();
    private ProcessMemory? _memory;
    private OsuStableReader? _reader;
    private DateTime _nextAttemptUtc = DateTime.MinValue;

    // Signature follow-up state. osu!.exe is a managed IL image, so a pattern only exists once the CLR has
    // JIT-compiled the method containing it — which may happen minutes after attach (gameplay, results
    // screen, settings). The watcher therefore re-sweeps for the entries that are still missing, with a
    // growing back-off and a bounded number of attempts.
    private ProcessModuleInfo _attachedModule;
    private SignatureTable? _attachedTable;
    private DateTime _nextRescanUtc = DateTime.MinValue;
    private TimeSpan _rescanBackoff = TimeSpan.FromSeconds(2);
    private int _rescanAttempts;

    public GameStateWatcher(GameStateWatcherOptions? options = null)
    {
        Options = options ?? GameStateWatcherOptions.Default;
        _last = GameSnapshot.NotRunning();
    }

    public GameStateWatcherOptions Options { get; }

    /// <summary>Raised for each new snapshot. Handlers must be fast and must not throw.</summary>
    public event Action<GameSnapshot>? SnapshotChanged;

    /// <summary>Raised once per failure-mode change (unknown build, access denied, process gone).</summary>
    public event Action<string>? Diagnostic;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _thread is { IsAlive: true };
            }
        }
    }

    /// <summary>Most recent snapshot; starts as <see cref="OsuGameState.NotRunning"/>.</summary>
    public GameSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>Number of polls performed (a cheap liveness metric for the status bar).</summary>
    public long PollCount { get; private set; }

    /// <summary>Resolution report of the most recent successful attach, for diagnostics.</summary>
    public SignatureResolution? LastSignatureResolution { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is { IsAlive: true })
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _thread = new Thread(() => Loop(token))
            {
                IsBackground = true,
                Name = "osu-game-state-watcher"
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            _cts?.Cancel();
            thread = _thread;
            _thread = null;
        }

        try
        {
            thread?.Join(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Joining must never fail the caller.
        }

        Detach();
    }

    private void Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            GameSnapshot snapshot;
            try
            {
                snapshot = PollOnce();
            }
            catch (Exception ex)
            {
                snapshot = new GameSnapshot
                {
                    State = OsuGameState.Unknown,
                    Status = MemoryReadStatus.Error,
                    Detail = $"{ex.GetType().Name}: {ex.Message}"
                };
            }

            PollCount++;
            Publish(snapshot);

            try
            {
                token.WaitHandle.WaitOne(Options.PollInterval);
            }
            catch
            {
                break;
            }
        }
    }

    /// <summary>
    /// One poll. Establishes or drops the process attachment and produces a snapshot. Public so tests
    /// and the harness can drive it without a background thread.
    /// </summary>
    public GameSnapshot PollOnce()
    {
        var processId = ProcessMemory.FindProcessId(Options.ProcessName);
        if (processId is null)
        {
            Detach();
            return GameSnapshot.NotRunning();
        }

        if (_memory is not null && _memory.ProcessId != processId.Value)
        {
            Detach();
            _nextAttemptUtc = DateTime.MinValue;
        }

        if (_reader is null)
        {
            if (DateTime.UtcNow < _nextAttemptUtc)
            {
                return _last;
            }

            if (!TryAttach(out var failure))
            {
                _nextAttemptUtc = DateTime.UtcNow + Options.FailureBackoff;
                return failure;
            }
        }

        try
        {
            TryRescanSignatures();
            // CLR heaps can grow after attach. Refresh the map before validating moving objects.
            _memory!.EnumerateRegions();
            return _reader!.ReadSnapshot();
        }
        catch (Exception ex)        {
            Detach();
            return new GameSnapshot
            {
                State = OsuGameState.Unknown,
                Status = MemoryReadStatus.Error,
                Detail = $"snapshot read failed: {ex.GetType().Name}: {ex.Message}"
            };
        }
    }

    private bool TryAttach(out GameSnapshot failure)
    {
        failure = GameSnapshot.NotRunning();
        ProcessMemory memory;
        try
        {
            memory = ProcessMemory.Open(Options.ProcessName, Options.ExpectedPointerSize, cacheRegions: false);
        }
        catch (Exception ex)
        {
            var status = ex is System.ComponentModel.Win32Exception win && win.NativeErrorCode == ProcessMemory.ErrorAccessDenied
                ? MemoryReadStatus.AccessDenied
                : MemoryReadStatus.Error;

            failure = new GameSnapshot
            {
                State = OsuGameState.Unknown,
                Status = status,
                Detail = ex.Message
            };
            return false;
        }

        try
        {
            var module = memory.FindModule("osu!.exe") ?? memory.EnumerateModules().FirstOrDefault();
            if (module.Name is null)
            {
                memory.Dispose();
                failure = new GameSnapshot
                {
                    State = OsuGameState.Unknown,
                    Status = MemoryReadStatus.SignaturesNotFound,
                    Detail = "no module found in the target process"
                };
                return false;
            }

            var table = Options.Table ?? SignatureTables.Default;
            var resolution = OsuStableReader.ResolveSignatures(memory, module, table);
            LastSignatureResolution = resolution;

            if (resolution.ResolvedCount == 0)
            {
                memory.Dispose();
                failure = new GameSnapshot
                {
                    State = OsuGameState.Unknown,
                    Status = MemoryReadStatus.SignaturesNotFound,
                    Detail = "no signature matched — osu! may have been updated; the file-system " +
                             "resolution path (scores.db + Data\\r scan) still works. " + resolution.Summary,
                    AttemptedSignatures = resolution.AttemptedCount,
                    ResolvedSignatures = 0
                };
                Diagnostic?.Invoke(failure.Detail ?? "signatures not found");
                return false;
            }

            _memory = memory;
            _reader = new OsuStableReader(memory, module, resolution, table);
            _attachedModule = module;
            _attachedTable = table;
            _rescanAttempts = 0;
            _rescanBackoff = TimeSpan.FromSeconds(2);
            _nextRescanUtc = resolution.Failures.Count == 0
                ? DateTime.MaxValue
                : DateTime.UtcNow + _rescanBackoff;

            Diagnostic?.Invoke($"attached to pid {memory.ProcessId}: {resolution.Summary}");
            return true;
        }
        catch (Exception ex)
        {
            memory.Dispose();
            failure = new GameSnapshot
            {
                State = OsuGameState.Unknown,
                Status = MemoryReadStatus.Error,
                Detail = $"attach failed: {ex.GetType().Name}: {ex.Message}"
            };
            return false;
        }
    }

    /// <summary>
    /// Re-sweeps for signature entries that had not resolved yet.
    ///
    /// A signature can only be found after the CLR has JIT-compiled the method that contains it, and which
    /// methods are compiled depends on what the player has done (menu, gameplay, results screen). So a
    /// partial table at attach time is normal, and it becomes complete once the relevant screen is reached.
    /// The number of attempts is bounded so a build the table does not match costs a finite amount of work
    /// instead of an endless full-address-space sweep every few seconds.
    /// </summary>
    private void TryRescanSignatures()
    {
        if (_reader is null || _memory is null || _attachedTable is null)
        {
            return;
        }

        if (_reader.Signatures.Failures.Count == 0)
        {
            _nextRescanUtc = DateTime.MaxValue;
            return;
        }

        if (DateTime.UtcNow < _nextRescanUtc)
        {
            return;
        }

        if (_rescanAttempts >= 8)
        {
            _nextRescanUtc = DateTime.MaxValue;
            Diagnostic?.Invoke(
                "stopped re-scanning for missing signatures after 8 attempts; the table likely does not " +
                "match this game build. Missing: " +
                string.Join(", ", _reader.Signatures.Failures.Keys));
            return;
        }

        _rescanAttempts++;
        _nextRescanUtc = DateTime.UtcNow + _rescanBackoff;
        _rescanBackoff = TimeSpan.FromSeconds(Math.Min(60, _rescanBackoff.TotalSeconds * 2));

        try
        {
            var table = _attachedTable;
            var previous = _reader.Signatures;
            var fresh = new SignatureScanner(_memory).ResolveProcessWide(table, out var stats);

            // Keep what already resolved and add what just became available; a rescan never downgrades a
            // known-good address.
            var mergedAddresses = new Dictionary<string, ulong>(previous.Addresses, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, address) in fresh.Addresses)
            {
                mergedAddresses[name] = address;
            }

            if (mergedAddresses.Count == previous.ResolvedCount)
            {
                return;
            }

            var mergedFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, reason) in fresh.Failures)
            {
                if (!mergedAddresses.ContainsKey(name))
                {
                    mergedFailures[name] = reason;
                }
            }

            var merged = new SignatureResolution(mergedAddresses, mergedFailures);
            _reader = new OsuStableReader(_memory, _attachedModule, merged, table);
            LastSignatureResolution = merged;
            Diagnostic?.Invoke(
                $"signatures improved: {merged.ResolvedCount}/{merged.AttemptedCount} resolved " +
                $"(scan: {stats})");

            if (merged.Failures.Count == 0)
            {
                _nextRescanUtc = DateTime.MaxValue;
            }
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"signature re-scan failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Detach()
    {
        var memory = _memory;
        _memory = null;
        _reader = null;
        _attachedTable = null;
        _rescanAttempts = 0;
        _nextRescanUtc = DateTime.MinValue;
        try
        {
            memory?.Dispose();
        }
        catch
        {
            // Ignore; a failed handle close is not actionable.
        }
    }

    private void Publish(GameSnapshot snapshot)
    {
        bool changed;
        lock (_gate)
        {
            changed = Options.EmitOnEveryPoll || HasChanged(_last, snapshot);
            _last = snapshot;
        }

        if (!changed)
        {
            return;
        }

        try
        {
            SnapshotChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"snapshot handler threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Change detection: everything the auto-fill flow reacts to, plus the status so a degradation is
    /// announced once rather than silently.
    /// </summary>
    public static bool HasChanged(GameSnapshot previous, GameSnapshot current)
        => previous.State != current.State
           || previous.Status != current.Status
           || !string.Equals(previous.BeatmapMd5, current.BeatmapMd5, StringComparison.OrdinalIgnoreCase)
           || !string.Equals(previous.BeatmapFolder, current.BeatmapFolder, StringComparison.Ordinal)
           || !string.Equals(previous.BeatmapOsuFile, current.BeatmapOsuFile, StringComparison.Ordinal)
           || previous.Mods != current.Mods
           || previous.IsWatchingReplay != current.IsWatchingReplay
           || previous.Score != current.Score
           || previous.MaxCombo != current.MaxCombo
           || previous.ScoreDate != current.ScoreDate
           || previous.Mode != current.Mode
           || !(previous.HitCounts ?? Array.Empty<int>()).SequenceEqual(current.HitCounts ?? Array.Empty<int>())
           || previous.Retries != current.Retries
           || !string.Equals(previous.PlayerName, current.PlayerName, StringComparison.Ordinal);

    /// <summary>Blocking wait for the next snapshot matching a predicate; used by the harness.</summary>
    public GameSnapshot? WaitFor(Func<GameSnapshot, bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        GameSnapshot? seen = null;
        void Handler(GameSnapshot snapshot) => seen = snapshot;

        SnapshotChanged += Handler;
        try
        {
            if (predicate(Current))
            {
                return Current;
            }

            while (stopwatch.Elapsed < timeout)
            {
                if (seen is not null && predicate(seen))
                {
                    return seen;
                }

                Thread.Sleep(25);
            }

            return seen;
        }
        finally
        {
            SnapshotChanged -= Handler;
        }
    }

    public void Dispose() => Stop();
}

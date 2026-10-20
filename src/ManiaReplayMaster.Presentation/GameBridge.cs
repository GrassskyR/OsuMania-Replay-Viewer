using System.IO;
using ManiaReplayMaster.Game;
using ManiaReplayMaster.Game.Memory;
using static ManiaReplayMaster.App.Localization.Localizer;

namespace ManiaReplayMaster.App;

/// <summary>What auto-detection produced, in UI-friendly terms.</summary>
public sealed class AutoDetectResult
{
    public string? ReplayPath { get; init; }

    public string? BeatmapPath { get; init; }

    public Func<string> Describe { get; init; } = () => string.Empty;
    public string StatusText => Describe();

    public IReadOnlyList<CandidateItem> Candidates { get; init; } = Array.Empty<CandidateItem>();

    public bool HasAny => ReplayPath is not null || BeatmapPath is not null;
}

public sealed class CandidateItem
{
    public required string ReplayPath { get; init; }

    public required Func<string> Describe { get; init; }
    public string Display => Describe();

    public override string ToString() => Display;
}

/// <summary>
/// Thin adapter over <see cref="GameLinkService"/> so the window keeps working even when the game
/// integration cannot be initialised (no osu! installed, no permissions, unknown game version).
/// </summary>
internal sealed class GameBridge : IDisposable
{
    private GameLinkService? _service;
    private GameStateWatcher? _memoryWatcher;
    private string? _initialisationError;

    public bool Available => _service is not null;

    public string Detail => _initialisationError is not null
        ? Text("Game.Unavailable", _initialisationError)
        : _service is null ? Text("Game.NotInitialized")
        : OsuFound ? Text("Game.Located", OsuRoot) : Text("Game.NotFound");

    public bool OsuFound => _service?.Locator.Found == true;

    public string? OsuRoot => _service?.Locator.Found == true ? _service.Locator.Root : null;

    public void SetMemoryEnabled(bool enabled)
    {
        if (enabled) _memoryWatcher?.Start();
        else _memoryWatcher?.Stop();
    }

    public void Initialise()
    {
        if (_service is not null || _initialisationError is not null)
        {
            return;
        }

        try
        {
            _service = new GameLinkService();
            _memoryWatcher = new GameStateWatcher();
            _memoryWatcher.Start();
        }
        catch (Exception ex)
        {
            _initialisationError = ex.Message;
        }
    }

    /// <summary>Resolves a manually picked replay's beatmap by its embedded MD5.</summary>
    public AutoDetectResult FromReplayFile(string replayPath)
    {
        if (_service is null)
        {
            return new AutoDetectResult { Describe = () => Detail };
        }

        try
        {
            var result = _service.AutoFillFromReplayFile(replayPath);
            return Project(result);
        }
        catch (Exception ex)
        {
            return new AutoDetectResult { Describe = () => Text("Game.ReplayLookupFailed", ex.Message) };
        }
    }

    public AutoDetectResult FromSnapshot(GameSnapshot snapshot)
    {
        if (_service is null)
        {
            return new AutoDetectResult { Describe = () => Detail };
        }

        try
        {
            if (!snapshot.HasBeatmapIdentity || snapshot.State is not (OsuGameState.Results or OsuGameState.ScorePanelOpen))
                return new AutoDetectResult { Describe = () => Text(snapshot.Status switch
                {
                    MemoryReadStatus.NotRunning => "Status.NotRunning",
                    MemoryReadStatus.AccessDenied or MemoryReadStatus.SignaturesNotFound
                        or MemoryReadStatus.PointerChainFailed or MemoryReadStatus.Error => "Status.MemoryUnavailable",
                    _ => snapshot.State is OsuGameState.Results or OsuGameState.ScorePanelOpen
                        ? "Status.ScoreUnavailable" : "Status.Waiting"
                }) };
            return Project(_service.AutoFill(snapshot));
        }
        catch (Exception ex)
        {
            return new AutoDetectResult { Describe = () => Text("Game.AutoDetectFailed", ex.Message) };
        }
    }

    /// <summary>
    /// Returns the latest background memory observation without scanning on the WPF dispatcher.
    /// </summary>
    public GameSnapshot PollSnapshot()
    {
        if (_service is null)
        {
            return GameSnapshot.NotRunning(Detail);
        }

        return _memoryWatcher?.Current ?? GameSnapshot.NotRunning(Detail);
    }

    private static AutoDetectResult Project(AutoFillResult result)
    {
        var candidates = result.Alternatives
            .Select((c, i) => new CandidateItem
            {
                ReplayPath = c.ReplayPath,
                Describe = () => c.Header is null
                    ? $"[{i + 1}] {Path.GetFileName(c.ReplayPath)}  ({c.Source}, {c.Confidence:P0})"
                    : Text("Game.Candidate", i + 1, PlayerDisplayName(c.Header.Username), c.Header.Score,
                        c.Header.Mods == 0 ? Text("Game.NoMods") : "0x" + c.Header.Mods.ToString("X"),
                        c.Header.MaxCombo, c.Header.Mode)
            })
            .ToList();

        return new AutoDetectResult
        {
            ReplayPath = result.Replay.IsConfident ? result.Replay.Best?.ReplayPath : null,
            BeatmapPath = result.Beatmap?.Path,
            Describe = () => DescribeResult(result),
            Candidates = candidates
        };
    }

    private static string DescribeResult(AutoFillResult result)
    {
        if (string.IsNullOrEmpty(result.BeatmapMd5)) return Text("Game.InvalidReplay");
        var parts = new List<string>
        {
            result.Beatmap is null ? Text("Game.BeatmapMissing") : Text("Game.BeatmapFound", result.Beatmap.FileName),
            result.Replay.Best is not { } best ? Text("Game.ReplayMissing")
                : Text("Game.ReplayFound", Path.GetFileName(best.ReplayPath), best.Confidence)
        };
        if (result.Replay.Best?.Header is { } header)
            parts.Add(Text("Game.Score", PlayerDisplayName(header.Username), header.Score, header.MaxCombo));
        return string.Join("  ·  ", parts);
    }

    private static string PlayerDisplayName(string? player)
        => string.IsNullOrWhiteSpace(player) ? Text("Game.NoPlayer") : player;

    public void Dispose()
    {
        _memoryWatcher?.Dispose();
        _memoryWatcher = null;
        _service?.Dispose();
        _service = null;
    }
}

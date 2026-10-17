using System.IO;
using ManiaReplayMaster.Game;
using ManiaReplayMaster.Game.Memory;

namespace ManiaReplayMaster.App;

/// <summary>What auto-detection produced, in UI-friendly terms.</summary>
internal sealed class AutoDetectResult
{
    public string? ReplayPath { get; init; }

    public string? BeatmapPath { get; init; }

    public string StatusText { get; init; } = string.Empty;

    public IReadOnlyList<CandidateItem> Candidates { get; init; } = Array.Empty<CandidateItem>();

    public bool HasAny => ReplayPath is not null || BeatmapPath is not null;
}

internal sealed class CandidateItem
{
    public required string ReplayPath { get; init; }

    public required string Display { get; init; }

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

    public string Detail { get; private set; } = "not initialised";

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
            Detail = _service.Locator.Found
                ? "osu! located"
                : "osu! not found (manual mode only)";
        }
        catch (Exception ex)
        {
            _initialisationError = ex.Message;
            Detail = "game integration unavailable: " + ex.Message;
        }
    }

    /// <summary>Resolves a manually picked replay's beatmap by its embedded MD5.</summary>
    public AutoDetectResult FromReplayFile(string replayPath)
    {
        if (_service is null)
        {
            return new AutoDetectResult { StatusText = Detail };
        }

        try
        {
            var result = _service.AutoFillFromReplayFile(replayPath);
            return Project(result);
        }
        catch (Exception ex)
        {
            return new AutoDetectResult { StatusText = "replay lookup failed: " + ex.Message };
        }
    }

    public AutoDetectResult FromSnapshot(GameSnapshot snapshot)
    {
        if (_service is null)
        {
            return new AutoDetectResult { StatusText = Detail };
        }

        try
        {
            if (!snapshot.HasBeatmapIdentity || snapshot.State is not (OsuGameState.Results or OsuGameState.ScorePanelOpen))
                return new AutoDetectResult { StatusText = snapshot.Detail ?? $"osu!: {snapshot.State}" };
            return Project(_service.AutoFill(snapshot));
        }
        catch (Exception ex)
        {
            return new AutoDetectResult { StatusText = "auto-detect failed: " + ex.Message };
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
                Display = c.Header is null
                    ? $"[{i + 1}] {Path.GetFileName(c.ReplayPath)}  ({c.Source}, {c.Confidence:P0})"
                    : $"[{i + 1}] {c.Header.Username ?? "(no player)"} · {c.Header.Score:N0} · " +
                      $"{(c.Header.Mods == 0 ? "no mods" : "0x" + c.Header.Mods.ToString("X"))} · " +
                      $"combo {c.Header.MaxCombo} · {c.Header.Mode}"
            })
            .ToList();

        return new AutoDetectResult
        {
            ReplayPath = result.Replay.IsConfident ? result.Replay.Best?.ReplayPath : null,
            BeatmapPath = result.Beatmap?.Path,
            StatusText = result.StatusText,
            Candidates = candidates
        };
    }

    public void Dispose()
    {
        _memoryWatcher?.Dispose();
        _memoryWatcher = null;
        _service?.Dispose();
        _service = null;
    }
}

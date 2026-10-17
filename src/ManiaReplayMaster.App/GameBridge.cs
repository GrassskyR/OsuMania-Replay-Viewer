using System.IO;
using ManiaReplayMaster.Game;

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
    private string? _initialisationError;

    public bool Available => _service is not null;

    public string Detail { get; private set; } = "not initialised";

    public bool OsuFound => _service?.Locator.Found == true;

    public string? OsuRoot => _service?.Locator.Found == true ? _service.Locator.Root : null;

    public void Initialise()
    {
        if (_service is not null || _initialisationError is not null)
        {
            return;
        }

        try
        {
            _service = new GameLinkService();
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
            return Project(_service.AutoFill(snapshot));
        }
        catch (Exception ex)
        {
            return new AutoDetectResult { StatusText = "auto-detect failed: " + ex.Message };
        }
    }

    /// <summary>
    /// Reads the current game snapshot.
    ///
    /// NOTE: the polling half of the memory layer (<c>GameStateWatcher</c> + <c>OsuStableReader</c>)
    /// is not wired into <see cref="GameLinkService"/> yet, so this deliberately reports
    /// "not available" instead of pretending to know the game state. File-system detection --
    /// watching <c>Data\r</c> and reading <c>scores.db</c>/<c>osu!.db</c> -- still works and is what
    /// the auto-detect path actually uses today.
    /// </summary>
    public GameSnapshot PollSnapshot()
    {
        if (_service is null)
        {
            return GameSnapshot.NotRunning(Detail);
        }

        return GameSnapshot.NotRunning(
            _service.Locator.Found
                ? "in-memory score detection not wired up yet; file-system detection is active"
                : "osu! executable not found");
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
            ReplayPath = result.Replay.Best?.ReplayPath,
            BeatmapPath = result.Beatmap?.Path,
            StatusText = result.StatusText,
            Candidates = candidates
        };
    }

    public void Dispose()
    {
        _service?.Dispose();
        _service = null;
    }
}

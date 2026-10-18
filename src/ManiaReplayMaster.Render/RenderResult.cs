namespace ManiaReplayMaster.Render;

/// <summary>
/// Geometry of a finished render, plus the paths of the long-strip PNGs it wrote.
///
/// <see cref="WindowFrameCount"/> and <see cref="GameWidth"/> are the two values the reference's
/// <c>Render.start()</c> returns (<c>Pair&lt;Int, Int&gt;</c>) and are exactly what the encoder
/// needs; the remaining members are exposed because they are what a parity check against the
/// reference has to compare (window count, canvas size, slice count).
/// </summary>
public sealed class RenderResult
{
    /// <summary>Exact number of output frames covering one scroll step.</summary>
    public required int WindowFrameCount { get; init; }

    /// <summary>Canvas width: <c>Config.width</c>, or <c>Config.width / 4</c> for taiko single column.</summary>
    public required int GameWidth { get; init; }

    /// <summary>Canvas height of the visible game area: <c>Config.height</c>.</summary>
    public required int GameHeight { get; init; }

    /// <summary>Height of one long-strip PNG: <c>stepSize + gameHeight</c>.</summary>
    public required int WindowSize { get; init; }

    /// <summary>Scroll step between windows, aligned to whole pixels and output frames.</summary>
    public required int StepSize { get; init; }

    /// <summary>Duration expressed in pixels, rounded up to a whole-frame boundary.</summary>
    public required int TotalHeight { get; init; }

    /// <summary>Width of one judgement column: <c>Config.width / key</c>.</summary>
    public required int ColumnWidth { get; init; }

    /// <summary><c>ceil((totalHeight - gameHeight) / stepSize)</c> -- progress denominator only.</summary>
    public required int WindowCount { get; init; }

    /// <summary>Number of PNG slices actually written.</summary>
    public required int ImageCount { get; init; }

    /// <summary>The written slice paths, in window order.</summary>
    public required IReadOnlyList<string> ImagePaths { get; init; }
}

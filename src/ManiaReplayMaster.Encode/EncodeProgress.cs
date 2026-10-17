namespace ManiaReplayMaster.Encode;

/// <summary>
/// Progress notification for the encode stage.
///
/// <paramref name="Stage"/> mirrors the stage names the reference passes to
/// <c>ReplayMaster.printProgress</c>: <c>render</c> (performed by the renderer),
/// <c>split_&lt;n&gt;</c> and <c>merge</c> (performed here).
///
/// <paramref name="Percentage"/> is the *overall* pipeline percentage, exactly as the reference
/// computes it (<c>ReplayMaster.printProgress</c> maps a stage-relative 0..100 into the stage's
/// slice of the global bar). The original's stage boundaries are preserved: the render stage
/// occupies 0..30 and the ffmpeg stages cover 30..100 (30..60 for the split passes, 60..100 for
/// the final merge). Values are clamped to 0..100 and never decrease within one encoder run,
/// which is the original's <c>preProgress</c> behaviour.
/// </summary>
public readonly record struct EncodeProgress(string Stage, double Percentage)
{
    public override string ToString()
        => FormattableString.Invariant($"{Stage}: {Percentage:0.##}%");
}

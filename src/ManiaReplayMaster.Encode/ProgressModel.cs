namespace ManiaReplayMaster.Encode;

/// <summary>
/// Port of the progress arithmetic in <c>ReplayMaster.kt</c>.
///
/// <code>
/// progress = max(0.0, min(interProgress, 100.0)) / 100 * (endProgress - startProgress) + startProgress
/// progress = max(preProgress, progress)
/// </code>
/// </summary>
public static class ProgressModel
{
    /// <summary>End of the render stage (<c>ReplayMaster.STAGE_RENDER</c>).</summary>
    public const int StageRender = 30;

    /// <summary>End of the per-slice ffmpeg stage / start of the final merge (<c>ReplayMaster.STAGE_SPLICT</c>).</summary>
    public const int StageSplice = 60;

    /// <summary>100%.</summary>
    public const int StageComplete = 100;

    public const string RenderStageName = "render";
    public const string MergeStageName = "merge";

    public static string SplitStageName(int index) => "split_" + index;

    public static double Combine(int startProgress, int endProgress, double interProgress)
    {
        var normalized = Math.Max(0.0, Math.Min(interProgress, 100.0));
        return normalized / 100 * (endProgress - startProgress) + startProgress;
    }
}

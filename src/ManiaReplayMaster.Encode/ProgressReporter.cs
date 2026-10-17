namespace ManiaReplayMaster.Encode;

/// <summary>
/// Applies the reference's monotonic <c>preProgress</c> clamp and forwards to an
/// <see cref="IProgress{T}"/>. One instance per encode run, mirroring the fact that
/// <c>ReplayMaster.preProgress</c> is a single object-level field.
/// </summary>
public sealed class ProgressReporter
{
    private readonly IProgress<EncodeProgress>? _sink;
    private double _preProgress;

    public ProgressReporter(IProgress<EncodeProgress>? sink = null)
    {
        _sink = sink;
    }

    public string LastStage { get; private set; } = string.Empty;

    public double LastPercentage => _preProgress;

    public void Report(string stage, int startProgress, int endProgress, double interProgress)
    {
        var progress = ProgressModel.Combine(startProgress, endProgress, interProgress);
        _preProgress = Math.Max(_preProgress, progress);
        LastStage = stage;
        _sink?.Report(new EncodeProgress(stage, _preProgress));
    }
}

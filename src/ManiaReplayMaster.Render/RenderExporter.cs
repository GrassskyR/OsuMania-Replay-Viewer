using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Encode;
using ManiaReplayMaster.Encode.Diagnostics;
using PipelineRunner = ManiaReplayMaster.Core.Pipeline.Pipeline;

namespace ManiaReplayMaster.Render;

/// <summary>
/// Offline orchestration: parse + judge (Core), render the long-strip PNGs (this assembly), then
/// hand them to ffmpeg (<c>ManiaReplayMaster.Encode</c>). This is the single call the CLI and the
/// GUI need, and it mirrors the sequence in <c>Main.kt</c>:
///
/// <code>
/// prepareJudgements(beatmap, replay)   ->  Render(...).start()  ->  ReplayMaster.generateVideo(...)
/// </code>
///
/// Deliberate differences from <c>Main.kt</c>, all of them additive:
/// <list type="bullet">
/// <item>the temporary directory is created and removed by this class when the caller does not
/// supply one (the original used a temp dir owned by main);</item>
/// <item>progress is forwarded as <see cref="EncodeProgress"/> across all stages: the renderer
/// contributes 0..30 (<c>"render"</c>) and the encoder 30..100 (<c>"split_n"</c> / <c>"merge"</c>);</item>
/// <item>a <see cref="CancellationToken"/> aborts rendering between windows and kills the running
/// ffmpeg process.</item>
/// </list>
/// </summary>
public static class RenderExporter
{
    public sealed class Result
    {
        public required RenderResult Render { get; init; }

        /// <summary>Path of the produced mp4.</summary>
        public required string VideoPath { get; init; }

        /// <summary>The judged beatmap that was rendered (already delayed by 1000 ms).</summary>
        public required BeatMap BeatMap { get; init; }

        public required ReplayModel ReplayModel { get; init; }

        /// <summary>The long-strip PNG the renderer wrote (<c>&lt;name&gt;.png</c>, slices are <c>_i.png</c>).</summary>
        public required string ImagePath { get; init; }

        public required string BeatmapPath { get; init; }

        public required string ReplayPath { get; init; }
    }

    /// <summary>
    /// Exports to an explicit path, or uses Config.OutputDir with an osu! export-style name
    /// when outputVideoPath is omitted.
    /// </summary>
    public static Result Run(
        string beatMapPath,
        string replayPath,
        string? outputVideoPath = null,
        string? tempDirectory = null,
        Config? config = null,
        IProgress<EncodeProgress>? progress = null,
        string? ffmpegPath = null,
        PortLogger? log = null,
        CancellationToken cancellationToken = default,
        Action<EncoderSelection>? encoderSelected = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(beatMapPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(replayPath);
        if (outputVideoPath is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(outputVideoPath);

        config ??= Config.Instance;
        log ??= PortLogger.FromConfig(config);

        var ownsTempDirectory = tempDirectory is null;
        var temp = tempDirectory ?? Path.Combine(Path.GetTempPath(), "mrm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var preparation = PipelineRunner.Prepare(beatMapPath, replayPath, temp,
                (key, args) => log.Debug(key, args), config);

            // Resolve archive/directory inputs before reading the beatmap's display metadata.
            if (outputVideoPath is null)
            {
                var outputDirectory = Path.GetFullPath(config.OutputDir);
                Directory.CreateDirectory(outputDirectory);
                outputVideoPath = Path.Combine(outputDirectory,
                    ReplayVideoFileName.Create(preparation.BeatmapPath, preparation.ReplayPath));
            }

            var imagePath = Path.Combine(temp, Path.GetFileNameWithoutExtension(replayPath) + ".png");

            RenderResult rendered;
            using (var renderer = new Renderer(preparation.BeatMap, preparation.ReplayModel, imagePath,
                       config, log))
            {
                rendered = renderer.Start(
                    progress is null ? null : new RenderProgressAdapter(progress),
                    cancellationToken);
            }

            var encoder = new Encoder(
                preparation.BeatMap,
                rendered.ImagePaths,
                outputVideoPath,
                preparation.ReplayModel.Rate,
                (int)preparation.Delay,
                rendered.WindowFrameCount,
                ffmpegPath ?? FfmpegLocator.Locate(),
                rendered.GameWidth,
                config,
                log);

            encoder.GenerateVideo(progress, cancellationToken, encoderSelected);

            return new Result
            {
                Render = rendered,
                VideoPath = Path.GetFullPath(outputVideoPath),
                BeatMap = preparation.BeatMap,
                ReplayModel = preparation.ReplayModel,
                ImagePath = imagePath,
                BeatmapPath = preparation.BeatmapPath,
                ReplayPath = preparation.ReplayPath
            };
        }
        finally
        {
            if (ownsTempDirectory)
            {
                TryDeleteDirectory(temp);
            }
        }
    }

    /// <summary>Adapts the renderer's own <c>0..30</c> percentage into the shared progress channel.</summary>
    private sealed class RenderProgressAdapter : IProgress<double>
    {
        private readonly IProgress<EncodeProgress> _sink;

        public RenderProgressAdapter(IProgress<EncodeProgress> sink) => _sink = sink;

        public void Report(double value)
            => _sink.Report(new EncodeProgress(ProgressModel.RenderStageName, value));
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Leaving a temp directory behind must never fail a render that already succeeded.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

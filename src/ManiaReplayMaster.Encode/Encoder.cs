// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Encode.Diagnostics;

namespace ManiaReplayMaster.Encode;

/// <summary>
/// Port of <c>me.replaymaster.Encoder</c> with automatic hardware selection and stream-copy merging.
///
/// The class consumes the long-strip PNGs written by the renderer (<c>name_0.png</c>,
/// <c>name_1.png</c>, ...) and produces the final mp4:
/// <list type="number">
/// <item>when the beatmap has no playable BGM, a 1 s silence placeholder is generated first via
/// <c>-f lavfi -t 1 -i anullsrc</c> next to the images (this rewrites
/// <c>BeatMap.BgmPath</c>, exactly like the reference mutates the model);</item>
/// <item>with at most <c>Config.ffmpegMaxProcessingSize</c> (N = 20) images everything is encoded
/// in a single pass that also mixes the audio (<c>atempo</c> + <c>adelay</c>);</item>
/// <item>with more images the windows are grouped into <c>ceil(count / N)</c> silent slices
/// (<c>name_0.mp4</c>, <c>name_1.mp4</c>, ...) which are then concatenated and audio-muxed in a
/// final stream-copy pass, avoiding a second video encode and <c>-filter_complex</c> size limits.</item>
/// </list>
///
/// Added over the reference: <see cref="IProgress{T}"/> reporting and
/// <see cref="CancellationToken"/> support (the reference had neither). Progress semantics are
/// unchanged (see <see cref="EncodeProgress"/>).
/// </summary>
public sealed class Encoder
{
    private readonly BeatMap _beatMap;
    private readonly string _baseImageFile;
    private readonly string _outFile;
    private readonly double _rate;
    private readonly int _windowFrameCount;
    private readonly string _ffmpegPath;
    private readonly int _gameWidth;
    private readonly Config _config;
    private readonly PortLogger _log;

    private readonly double _duration;
    private readonly int _audioDelay;
    private readonly int _maxProcessingSize;

    private readonly List<IReadOnlyList<string>> _executedCommands = new();
    private EncoderSelection _selection = new("libx264", "ultrafast");

    public EncoderSelection SelectedEncoder => _selection;

    public Encoder(
        BeatMap beatMap,
        string baseImageFile,
        string outFile,
        double rate,
        int delay,
        int windowFrameCount,
        string ffmpegPath,
        int gameWidth,
        Config? config = null,
        PortLogger? log = null)
    {
        _beatMap = beatMap ?? throw new ArgumentNullException(nameof(beatMap));
        _baseImageFile = Path.GetFullPath(baseImageFile);
        _outFile = Path.GetFullPath(outFile);
        _rate = rate;
        _windowFrameCount = windowFrameCount;
        _ffmpegPath = ffmpegPath;
        _gameWidth = gameWidth;
        _config = config ?? Config.Instance;
        _log = log ?? PortLogger.FromConfig(_config);

        _duration = beatMap.Duration / 1000.0;
        _audioDelay = delay + beatMap.BgmOffset;
        _maxProcessingSize = _config.FfmpegMaxProcessingSize;
    }

    /// <summary>The image path prefix, i.e. the renderer's <c>outputImage</c> argument (absolute).</summary>
    public string BaseImageFile => _baseImageFile;

    /// <summary>Expected whole-video duration in seconds (<c>beatMap.duration / 1000.0</c>).</summary>
    public double Duration => _duration;

    /// <summary><c>delay + beatMap.bgmOffset</c>, the <c>adelay</c> value in milliseconds.</summary>
    public int AudioDelay => _audioDelay;

    /// <summary>
    /// Every ffmpeg argv this instance actually executed, executable included. Diagnostics/test hook;
    /// the reference only printed these when <c>isServer</c> was set.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> ExecutedCommands => _executedCommands;

    /// <summary>
    /// Port of <c>Encoder.generateVideo()</c>.
    /// </summary>
    /// <param name="progress">Optional progress sink; <c>null</c> disables reporting.</param>
    /// <param name="cancellationToken">
    /// Cancels the run. An in-flight ffmpeg process is killed (its process tree included) and
    /// <see cref="OperationCanceledException"/> is thrown.
    /// </param>
    public void GenerateVideo(IProgress<EncodeProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<EncoderSelection>? encoderSelected = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var width = _beatMap.IsTaiko ? _config.Height : _gameWidth;
        var height = _beatMap.IsTaiko ? _gameWidth : _config.Height;
        _selection = VideoEncoderSelector.Detect(_ffmpegPath, width, height, _config.FramePerSecond,
            _config.Codec, _config.CodecPreset, _log, cancellationToken);
        encoderSelected?.Invoke(_selection);
        try
        {
            GenerateVideoCore(progress, cancellationToken);
        }
        catch (VideoGenerationException ex) when (VideoEncoderSelector.IsAutomatic(_config.Codec)
                                                 && _selection.IsHardwareAccelerated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _log.Debug($"Hardware encoding failed; retrying with libx264. {ex}");
            _selection = new EncoderSelection("libx264", "ultrafast", IsFallback: true);
            encoderSelected?.Invoke(_selection);
            File.Delete(_outFile);
            GenerateVideoCore(progress, cancellationToken);
        }
    }

    private void GenerateVideoCore(IProgress<EncodeProgress>? progress, CancellationToken cancellationToken)
    {
        var reporter = new ProgressReporter(progress);

        if (_beatMap.BgmPath == BeatMap.MuteMusic)
        {
            // The reference resolves the placeholder against the image's directory and stores the
            // absolute path back into the model; both the mute pass and the later passes then use it.
            var directory = Path.GetDirectoryName(_baseImageFile);
            _beatMap.BgmPath = string.IsNullOrEmpty(directory)
                ? Path.GetFullPath(_beatMap.BgmPath)
                : Path.Combine(directory, _beatMap.BgmPath);

            _log.Debug($"Generate mute sound: {_beatMap.BgmPath}");
            RunFfmpeg(
                FfmpegCommandBuilder.BuildMuteSound(_beatMap.BgmPath),
                _beatMap.BgmPath,
                0.0,
                string.Empty,
                0,
                0,
                reporter,
                cancellationToken);
        }

        var count = CountImageSlices();

        if (count <= _maxProcessingSize)
        {
            _log.Debug("generate once!");
            GenerateVideoRange(0, count - 1, _outFile, true, ProgressModel.MergeStageName,
                ProgressModel.StageRender, ProgressModel.StageComplete, reporter, cancellationToken);
            return;
        }

        var slices = new List<string>();
        var intervalProgress = (ProgressModel.StageSplice - ProgressModel.StageRender) / (double)count;
        var slicePasses = (int)Math.Ceiling(count / (double)_maxProcessingSize);

        for (var i = 0; i < slicePasses; i++)
        {
            var startIndex = _maxProcessingSize * i;
            var endIndex = Math.Min(startIndex + _maxProcessingSize, count) - 1;
            var sliceVideo = _baseImageFile.Replace(".png", $"_{i}.mp4");
            _log.Debug($"combine video #{startIndex} -> #{endIndex} to {sliceVideo}");
            slices.Add(sliceVideo);
            GenerateVideoRange(
                startIndex,
                endIndex,
                sliceVideo,
                false,
                ProgressModel.SplitStageName(i),
                ProgressModel.StageRender + (int)(intervalProgress * startIndex),
                ProgressModel.StageRender + (int)(intervalProgress * (endIndex + 1)),
                reporter,
                cancellationToken);
        }

        MergeSlices(slices, _duration, ProgressModel.MergeStageName,
            ProgressModel.StageSplice, ProgressModel.StageComplete, reporter, cancellationToken);
    }

    /// <summary>
    /// Port of the reference's <c>getImageSliceName</c>. Note that the reference replaces every
    /// literal <c>.png</c> occurrence of the path, and so does this port.
    /// </summary>
    public string GetImageSliceName(int index) => _baseImageFile.Replace(".png", $"_{index}.png");

    /// <summary>Number of consecutive <c>_0.png</c>, <c>_1.png</c>, ... files present on disk.</summary>
    public int CountImageSlices()
    {
        var count = 0;
        while (File.Exists(GetImageSliceName(count)))
        {
            count++;
        }

        return count;
    }

    private void MergeSlices(
        IReadOnlyList<string> slices,
        double expectedDuration,
        string stage,
        int startProgress,
        int endProgress,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var concatList = Path.Combine(Path.GetDirectoryName(_baseImageFile)!, "concat-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllLines(concatList, FfmpegCommandBuilder.BuildConcatList(slices));
        var arguments = FfmpegCommandBuilder.BuildMerge(new FfmpegMergeRequest
        {
            ConcatListPath = concatList,
            BgmPath = _beatMap.BgmPath,
            Rate = _rate,
            AudioDelay = _audioDelay,
            OutPath = _outFile
        });

        // Note: the reference does NOT delete the output before the merge pass (Main.kt deletes it
        // once before rendering); ffmpeg is invoked without -y, so a stale file would abort the merge.
        try
        {
            RunFfmpeg(arguments, _outFile, expectedDuration, stage, startProgress, endProgress, reporter,
                cancellationToken);
        }
        finally
        {
            File.Delete(concatList);
        }
    }

    private void GenerateVideoRange(
        int startIndex,
        int endIndex,
        string outPath,
        bool isFinal,
        string stage,
        int startProgress,
        int endProgress,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var images = new List<string>();
        for (var index = startIndex; index <= endIndex; index++)
        {
            images.Add(GetImageSliceName(index));
        }

        File.Delete(outPath);

        var sliceDuration = _windowFrameCount / (double)_config.FramePerSecond;
        var arguments = FfmpegCommandBuilder.BuildVideo(new FfmpegVideoRequest
        {
            GameWidth = _gameWidth,
            GameHeight = _config.Height,
            FramePerSecond = _config.FramePerSecond,
            Speed = _config.Speed,
            SliceDuration = sliceDuration,
            ImagePaths = images,
            IsTaiko = _beatMap.IsTaiko,
            IsFinal = isFinal,
            BgmPath = _beatMap.BgmPath,
            Rate = _rate,
            AudioDelay = _audioDelay,
            OutPath = Path.GetFullPath(outPath),
            Codec = _selection.Codec,
            CodecPreset = _selection.Preset,
            CodecProfile = _config.CodecProfile
        });

        RunFfmpeg(arguments, outPath, sliceDuration * images.Count, stage, startProgress, endProgress,
            reporter, cancellationToken);
    }

    private void RunFfmpeg(
        IReadOnlyList<string> arguments,
        string outPath,
        double expectedDuration,
        string stage,
        int startProgress,
        int endProgress,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var command = new List<string>(arguments.Count + 1) { _ffmpegPath };
        command.AddRange(arguments);
        _executedCommands.Add(command);

        _log.Debug(string.Join(" ", command));

        FfmpegRunner.Run(_ffmpegPath, arguments, outPath, expectedDuration, stage, startProgress,
            endProgress, reporter, _log, cancellationToken);
    }
}

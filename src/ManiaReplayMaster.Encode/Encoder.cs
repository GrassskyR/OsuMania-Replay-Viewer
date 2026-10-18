// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Encode.Diagnostics;

namespace ManiaReplayMaster.Encode;

/// <summary>Encodes rendered images, retries failed automatic GPU encoding on CPU, and stream-copies split videos.</summary>
public sealed class Encoder
{
    private readonly BeatMap _beatMap;
    private readonly IReadOnlyList<string> _images;
    private readonly string _directory;
    private readonly string _outFile;
    private readonly double _rate;
    private readonly double _sliceDuration;
    private readonly int _audioDelay;
    private readonly string _ffmpegPath;
    private readonly int _gameWidth;
    private readonly Config _config;
    private readonly PortLogger _log;

    public EncoderSelection SelectedEncoder { get; private set; } = new("libx264", "ultrafast");

    public Encoder(BeatMap beatMap, IReadOnlyList<string> imagePaths, string outFile, double rate,
        int delay, int windowFrameCount, string ffmpegPath, int gameWidth,
        Config? config = null, PortLogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(beatMap);
        ArgumentNullException.ThrowIfNull(imagePaths);
        if (imagePaths.Count == 0) throw new ArgumentException("No rendered images to encode.", nameof(imagePaths));
        _config = config ?? Config.Instance;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_config.FfmpegMaxProcessingSize);
        _beatMap = beatMap;
        _images = imagePaths.Select(Path.GetFullPath).ToArray();
        _directory = Path.GetDirectoryName(_images[0])!;
        _outFile = Path.GetFullPath(outFile);
        _rate = rate;
        _sliceDuration = windowFrameCount / (double)_config.FramePerSecond;
        _audioDelay = delay + beatMap.BgmOffset;
        _ffmpegPath = ffmpegPath;
        _gameWidth = gameWidth;
        _log = log ?? PortLogger.FromConfig(_config);
    }

    public void GenerateVideo(IProgress<EncodeProgress>? progress = null,
        CancellationToken cancellationToken = default, Action<EncoderSelection>? encoderSelected = null)
    {
        var width = _beatMap.IsTaiko ? _config.Height : _gameWidth;
        var height = _beatMap.IsTaiko ? _gameWidth : _config.Height;
        SelectedEncoder = VideoEncoderSelector.Detect(_ffmpegPath, width, height, _config.FramePerSecond,
            _config.Codec, _config.CodecPreset, _log, cancellationToken);
        encoderSelected?.Invoke(SelectedEncoder);

        // Audio preparation is independent of the video encoder and must not be retried as a GPU failure.
        var reporter = new ProgressReporter(progress);
        PrepareAudio(reporter, cancellationToken);
        try
        {
            EncodeVideo(reporter, cancellationToken);
        }
        catch (VideoGenerationException ex) when (VideoEncoderSelector.IsAutomatic(_config.Codec)
                                                 && SelectedEncoder.IsHardwareAccelerated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _log.Debug($"Hardware encoding failed; retrying with libx264. {ex}");
            SelectedEncoder = new EncoderSelection("libx264", "ultrafast", IsFallback: true);
            encoderSelected?.Invoke(SelectedEncoder);
            EncodeVideo(reporter, cancellationToken);
        }
    }

    private void PrepareAudio(ProgressReporter reporter, CancellationToken cancellationToken)
    {
        if (_beatMap.BgmPath != BeatMap.MuteMusic) return;
        _beatMap.BgmPath = Path.Combine(_directory, BeatMap.MuteMusic);
        RunFfmpeg(FfmpegCommandBuilder.BuildMuteSound(_beatMap.BgmPath), _beatMap.BgmPath,
            0, string.Empty, 0, 0, reporter, cancellationToken);
    }

    private void EncodeVideo(ProgressReporter reporter, CancellationToken cancellationToken)
    {
        var maxImages = _config.FfmpegMaxProcessingSize;
        if (_images.Count <= maxImages)
        {
            EncodeImages(_images, _outFile, true, ProgressModel.MergeStageName,
                ProgressModel.StageRender, ProgressModel.StageComplete, reporter, cancellationToken);
            return;
        }

        var slices = new List<string>();
        var progressPerImage = (ProgressModel.StageSplice - ProgressModel.StageRender) / (double)_images.Count;
        for (var start = 0; start < _images.Count; start += maxImages)
        {
            var images = _images.Skip(start).Take(maxImages).ToArray();
            var output = Path.Combine(_directory, $"slice-{slices.Count}.mp4");
            EncodeImages(images, output, false, ProgressModel.SplitStageName(slices.Count),
                ProgressModel.StageRender + (int)(progressPerImage * start),
                ProgressModel.StageRender + (int)(progressPerImage * (start + images.Length)), reporter, cancellationToken);
            slices.Add(output);
        }
        MergeSlices(slices, reporter, cancellationToken);
    }

    private void MergeSlices(IReadOnlyList<string> slices, ProgressReporter reporter, CancellationToken cancellationToken)
    {
        var concatList = Path.Combine(_directory, "concat-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllLines(concatList, FfmpegCommandBuilder.BuildConcatList(slices));
            var arguments = FfmpegCommandBuilder.BuildMerge(new FfmpegMergeRequest
            {
                ConcatListPath = concatList,
                BgmPath = _beatMap.BgmPath,
                Rate = _rate,
                AudioDelay = _audioDelay,
                OutPath = _outFile
            });
            RunFfmpeg(arguments, _outFile, _beatMap.Duration / 1000.0, ProgressModel.MergeStageName,
                ProgressModel.StageSplice, ProgressModel.StageComplete, reporter, cancellationToken);
        }
        finally
        {
            File.Delete(concatList);
        }
    }

    private void EncodeImages(IReadOnlyList<string> images, string outPath, bool isFinal,
        string stage, int startProgress, int endProgress, ProgressReporter reporter, CancellationToken cancellationToken)
    {
        var arguments = FfmpegCommandBuilder.BuildVideo(new FfmpegVideoRequest
        {
            GameWidth = _gameWidth,
            GameHeight = _config.Height,
            FramePerSecond = _config.FramePerSecond,
            Speed = _config.Speed,
            SliceDuration = _sliceDuration,
            ImagePaths = images,
            IsTaiko = _beatMap.IsTaiko,
            IsFinal = isFinal,
            BgmPath = _beatMap.BgmPath,
            Rate = _rate,
            AudioDelay = _audioDelay,
            OutPath = outPath,
            Codec = SelectedEncoder.Codec,
            CodecPreset = SelectedEncoder.Preset,
            CodecProfile = _config.CodecProfile
        });
        RunFfmpeg(arguments, outPath, _sliceDuration * images.Count, stage, startProgress, endProgress,
            reporter, cancellationToken);
    }

    private void RunFfmpeg(IReadOnlyList<string> arguments, string outPath, double expectedDuration,
        string stage, int startProgress, int endProgress, ProgressReporter reporter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(outPath);
        FfmpegRunner.Run(_ffmpegPath, arguments, outPath, expectedDuration, stage, startProgress,
            endProgress, reporter, _log, cancellationToken);
    }
}

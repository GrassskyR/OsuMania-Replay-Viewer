using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Encode;
using ManiaReplayMaster.Encode.Diagnostics;
// System.Drawing.Imaging also defines an `Encoder` type, so alias the one under test.
using EncodeEncoder = ManiaReplayMaster.Encode.Encoder;

namespace ManiaReplayMaster.RenderEncode.Tests;

/// <summary>
/// Error and cancellation behaviour of the encode layer. Both need a real ffmpeg process, but the
/// failing commands abort immediately, so they stay fast.
///
/// Reference behaviour under test: a missing output file becomes
/// <see cref="VideoGenerationException"/> (error code <c>render_failed</c>). Added over the
/// reference: cancelling kills the running ffmpeg and throws
/// <see cref="OperationCanceledException"/>.
/// </summary>
[Trait("Category", "EndToEnd")]
public class EncoderFailureTests
{
    [Fact]
    public void AutomaticMode_RetriesSoftwareWhenHardwareRejectsAnX264OnlyProfile()
    {
        var (ffmpeg, ffprobe) = FfmpegProbe.Resolve();
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            var image = WriteSingleSlice(temp);
            var selections = new List<EncoderSelection>();
            var encoder = new EncodeEncoder(new BeatMap { Duration = 500, BgmPath = BeatMap.MuteMusic },
                new[] { image }, Path.Combine(temp, "fallback.mp4"), 1, 0, 30, ffmpeg, 540,
                new Config { Codec = "auto", CodecProfile = "high444" });
            encoder.GenerateVideo(encoderSelected: selections.Add);

            Assert.Equal("libx264", encoder.SelectedEncoder.Codec);
            Assert.True(encoder.SelectedEncoder.IsFallback);
            if (selections[0].IsHardwareAccelerated) Assert.Equal(2, selections.Count);
            Assert.Single(FfmpegProbe.Inspect(ffprobe, Path.Combine(temp, "fallback.mp4")).VideoStreams);
        }
        finally
        {
            RepoPaths.DeleteDirectory(temp);
        }
    }

    [Fact]
    public void FailedProcessIsNotSuccessEvenWhenAnOutputFileExists()
    {
        var (ffmpeg, _) = FfmpegProbe.Resolve();
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            var output = Path.Combine(temp, "partial.mp4");
            File.WriteAllText(output, "incomplete output");
            Assert.Throws<VideoGenerationException>(() => FfmpegRunner.Run(ffmpeg,
                new[] { "-definitely-not-an-option" }, output, 1, "test", 0, 100,
                new ProgressReporter(null), new PortLogger()));
        }
        finally
        {
            RepoPaths.DeleteDirectory(temp);
        }
    }

    [Fact]
    public void PreCancelledToken_ThrowsOperationCanceledAndDoesNotProduceOutput()
    {
        var (ffmpeg, _) = FfmpegProbe.Resolve();
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            var image = WriteSingleSlice(temp);
            var beatMap = new BeatMap { Key = 4, Duration = 5000, BgmPath = BeatMap.MuteMusic };
            var encoder = new EncodeEncoder(beatMap, new[] { image }, Path.Combine(temp, "v.mp4"), 1.0, 1000, 150, ffmpeg, 540,
                new Config());

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => encoder.GenerateVideo(null, cts.Token));
            Assert.False(File.Exists(Path.Combine(temp, "v.mp4")));
        }
        finally
        {
            RepoPaths.DeleteDirectory(temp);
        }
    }

    [Fact]
    public void CancelRunningExport_StopsFfmpegPromptly()
    {
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var watch = Stopwatch.StartNew();
            Assert.Throws<OperationCanceledException>(() => FfmpegRunner.Run(FfmpegProbe.Resolve().Ffmpeg,
                ["-v", "error", "-re", "-f", "lavfi", "-i", "color=black:s=180x320:r=60", "-t", "30",
                    "-c:v", "libx264", Path.Combine(temp, "cancel.mp4")],
                Path.Combine(temp, "cancel.mp4"), 30, "cancel", 0, 100,
                new ProgressReporter(null), new PortLogger(), cancellation.Token));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
            // FfmpegRunner waits for process exit before returning cancellation; the file must be released.
            using var output = File.Open(Path.Combine(temp, "cancel.mp4"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        finally { RepoPaths.DeleteDirectory(temp); }
    }

    private static string WriteSingleSlice(string directory)
    {
        var imagePath = Path.Combine(directory, "v.png");
        using (var bitmap = new Bitmap(540, 3960, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
            }

            bitmap.Save(imagePath, ImageFormat.Png);
        }

        return imagePath;
    }
}

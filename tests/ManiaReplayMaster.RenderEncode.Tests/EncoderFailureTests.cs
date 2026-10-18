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
                image, Path.Combine(temp, "fallback.mp4"), 1, 0, 30, ffmpeg, 540,
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
    public void MissingOutput_ThrowsVideoGenerationException()
    {
        var (ffmpeg, _) = FfmpegProbe.Resolve();
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            var image = WriteSingleSlice(temp);
            var beatMap = new BeatMap { Key = 4, Duration = 5000, BgmPath = BeatMap.MuteMusic };
            var config = new Config { Codec = "definitely-not-a-codec" };
            var encoder = new EncodeEncoder(beatMap, image, Path.Combine(temp, "v.mp4"), 1.0, 1000, 150, ffmpeg, 540,
                config);

            var exception = Assert.Throws<VideoGenerationException>(() => encoder.GenerateVideo());

            Assert.Equal("render_failed", exception.ErrorCode);
            Assert.False(File.Exists(Path.Combine(temp, "v.mp4")));
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
            var encoder = new EncodeEncoder(beatMap, image, Path.Combine(temp, "v.mp4"), 1.0, 1000, 150, ffmpeg, 540,
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

    private static string WriteSingleSlice(string directory)
    {
        var imagePath = Path.Combine(directory, "v.png");
        using (var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
            }

            bitmap.Save(imagePath, ImageFormat.Png);
        }

        // The encoder looks for v_0.png, not v.png.
        File.Copy(imagePath, Path.Combine(directory, "v_0.png"), true);
        return imagePath;
    }
}

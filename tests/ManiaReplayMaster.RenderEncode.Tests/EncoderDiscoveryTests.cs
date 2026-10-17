using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Encode;

namespace ManiaReplayMaster.RenderEncode.Tests;

/// <summary>
/// File-system behaviour of the encoder that does not require running ffmpeg: the image-slice
/// naming scheme and the two-stage splitting threshold
/// (<c>Config.ffmpegMaxProcessingSize</c>, N = 20).
/// </summary>
public class EncoderDiscoveryTests
{
    private static Encoder CreateEncoder(string imagePath, string outPath, Config? config = null)
    {
        var beatMap = new BeatMap { Key = 4, Duration = 120000, BgmPath = BeatMap.MuteMusic };
        return new Encoder(
            beatMap,
            imagePath,
            outPath,
            rate: 1.0,
            delay: 1000,
            windowFrameCount: 150,
            ffmpegPath: "ffmpeg",
            gameWidth: 540,
            config: config ?? new Config());
    }

    [Fact]
    public void ImageSliceName_ReplacesEveryDotPngOccurrence()
    {
        var encoder = CreateEncoder(@"C:\work\im\v.png", @"C:\work\out\v.mp4");

        Assert.Equal(@"C:\work\im\v_7.png", encoder.GetImageSliceName(7));
    }

    [Fact]
    public void PathsAreNormalisedToAbsoluteLikeTheReference()
    {
        var encoder = CreateEncoder(Path.Combine("rel", "v.png"), Path.Combine("rel", "v.mp4"));

        Assert.Equal(Path.GetFullPath(Path.Combine("rel", "v.png")), encoder.BaseImageFile);
    }

    [Fact]
    public void CountImageSlices_StopsAtTheFirstGap()
    {
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            var image = Path.Combine(temp, "v.png");
            var encoder = CreateEncoder(image, Path.Combine(temp, "v.mp4"));
            File.WriteAllText(Path.Combine(temp, "v_0.png"), "x");
            File.WriteAllText(Path.Combine(temp, "v_1.png"), "x");
            File.WriteAllText(Path.Combine(temp, "v_3.png"), "x");

            Assert.Equal(2, encoder.CountImageSlices());
        }
        finally
        {
            RepoPaths.DeleteDirectory(temp);
        }
    }

    [Theory]
    [InlineData(20, false)] // exactly N -> single pass
    [InlineData(21, true)] // N + 1 -> slice passes + merge
    public void SplittingThresholdIsFfmpegMaxProcessingSize(int imageCount, bool expectSlicing)
    {
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            var image = Path.Combine(temp, "v.png");
            var encoder = CreateEncoder(image, Path.Combine(temp, "v.mp4"), new Config { FfmpegMaxProcessingSize = 20 });
            for (var i = 0; i < imageCount; i++)
            {
                File.WriteAllText(Path.Combine(temp, $"v_{i}.png"), "x");
            }

            var count = encoder.CountImageSlices();
            Assert.Equal(imageCount, count);
            Assert.Equal(expectSlicing, count > 20);
        }
        finally
        {
            RepoPaths.DeleteDirectory(temp);
        }
    }

    [Fact]
    public void AudioDelayIsDelayPlusBgmOffset()
    {
        var beatMap = new BeatMap { Key = 4, Duration = 120000, BgmOffset = 250, BgmPath = BeatMap.MuteMusic };
        var encoder = new Encoder(beatMap, @"C:\work\v.png", @"C:\work\v.mp4", 1.0, 1000, 150, "ffmpeg", 540,
            new Config());

        Assert.Equal(1250, encoder.AudioDelay);
        Assert.Equal(120.0, encoder.Duration, 6);
    }
}

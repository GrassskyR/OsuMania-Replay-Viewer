using System.Drawing;
using System.Drawing.Imaging;
using ManiaReplayMaster.Encode;

namespace ManiaReplayMaster.RenderEncode.Tests;

[Trait("Category", "EndToEnd")]
public sealed class ScrollCadenceTests
{
    [Theory]
    [InlineData(20)]
    [InlineData(29)]
    [InlineData(31)]
    public void EncodedFrames_MoveByConfiguredSpeed_EvenWhenSpeedIsOdd(int speed)
    {
        var (ffmpeg, _) = FfmpegProbe.Resolve();
        var directory = RepoPaths.CreateTempDirectory();
        try
        {
            var image = Path.Combine(directory, "strip.png");
            using (var bitmap = new Bitmap(96, 4000))
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.Black);
                for (var y = 30; y < bitmap.Height; y += 80)
                    graphics.FillRectangle(Brushes.White, 16, y, 64, 1);
                bitmap.Save(image, ImageFormat.Png);
            }

            var video = Path.Combine(directory, "scroll.mp4");
            var arguments = FfmpegCommandBuilder.BuildVideo(new FfmpegVideoRequest
            {
                GameWidth = 96,
                GameHeight = 160,
                FramePerSecond = 60,
                Speed = speed,
                SliceDuration = 2,
                ImagePaths = new[] { image },
                IsTaiko = false,
                IsFinal = false,
                BgmPath = "unused",
                Rate = 1,
                AudioDelay = 0,
                OutPath = video,
                Codec = "libx264",
                CodecPreset = "ultrafast",
                CodecProfile = "unknown"
            });
            FfmpegProbe.Run(ffmpeg, arguments.ToArray());

            var decoded = Path.Combine(directory, "frames.gray");
            FfmpegProbe.Run(ffmpeg, "-v", "error", "-i", video, "-an", "-pix_fmt", "gray",
                "-fps_mode", "passthrough", "-f", "rawvideo", decoded);
            var pixels = File.ReadAllBytes(decoded);
            const int frameBytes = 96 * 160;
            Assert.Equal(120 * frameBytes, pixels.Length);
            for (var frame = 0; frame < 120; frame++)
            {
                var top = Enumerable.Range(0, 160)
                    .First(y => pixels[frame * frameBytes + y * 96 + 48] > 200);
                var expected = (30 + frame * speed) % 80;
                Assert.True(top == expected,
                    $"speed={speed}, frame={frame}: top={top}, expected={expected}");
            }
        }
        finally
        {
            RepoPaths.DeleteDirectory(directory);
        }
    }
}

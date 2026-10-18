using System.Drawing;
using System.Drawing.Imaging;
using ManiaReplayMaster.Encode;
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Model;
using EncodeEncoder = ManiaReplayMaster.Encode.Encoder;

namespace ManiaReplayMaster.RenderEncode.Tests;

[Trait("Category", "EndToEnd")]
public sealed class ScrollCadenceTests
{
    [Theory]
    [InlineData("libx264")]
    [InlineData("auto")]
    public void CopiedSlicesPreserveEveryFrameAndScrollAcrossMergeBoundaries(string codec)
    {
        var (ffmpeg, _) = FfmpegProbe.Resolve();
        var directory = Path.Combine(RepoPaths.CreateTempDirectory(), "渲染 it's fine");
        Directory.CreateDirectory(directory);
        try
        {
            const int width = 128, height = 160, speed = 29, framesPerImage = 30, imageCount = 5;
            for (var index = 0; index < imageCount; index++)
            {
                using var bitmap = new Bitmap(width, 4000);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Black);
                    var offset = (30 + index * framesPerImage * speed) % 80;
                    for (var y = offset; y < bitmap.Height; y += 80)
                        graphics.FillRectangle(Brushes.White, 16, y, width - 32, 1);
                }
                bitmap.Save(Path.Combine(directory, $"strip_{index}.png"), ImageFormat.Png);
            }

            var video = Path.Combine(directory, "scroll.mp4");
            var encoder = new EncodeEncoder(new BeatMap { Duration = 2500, BgmPath = BeatMap.MuteMusic },
                Path.Combine(directory, "strip.png"), video, 1, 0, framesPerImage, ffmpeg, width,
                new Config { Codec = codec, Height = height, Speed = speed, FfmpegMaxProcessingSize = 2 });
            encoder.GenerateVideo();

            var decoded = Path.Combine(directory, "frames.gray");
            FfmpegProbe.Run(ffmpeg, "-v", "error", "-i", video, "-an", "-pix_fmt", "gray",
                "-fps_mode", "passthrough", "-f", "rawvideo", decoded);
            var pixels = File.ReadAllBytes(decoded);
            const int frameBytes = width * height;
            Assert.Equal(imageCount * framesPerImage * frameBytes, pixels.Length);
            for (var frame = 0; frame < imageCount * framesPerImage; frame++)
            {
                var top = Enumerable.Range(0, height)
                    .First(y => pixels[frame * frameBytes + y * width + width / 2] > 200);
                Assert.Equal((30 + frame * speed) % 80, top);
            }
        }
        finally
        {
            RepoPaths.DeleteDirectory(Path.GetDirectoryName(directory)!);
        }
    }

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

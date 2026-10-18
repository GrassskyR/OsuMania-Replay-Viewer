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
    [InlineData("libx264", 59)]
    [InlineData("auto", 60)]
    public void CopiedSlicesPreserveEveryFrameAndScrollAcrossMergeBoundaries(string codec, int fps)
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
                Enumerable.Range(0, imageCount).Select(index => Path.Combine(directory, $"strip_{index}.png")).ToArray(),
                video, 1, 0, framesPerImage, ffmpeg, width,
                new Config { Codec = codec, FramePerSecond = fps, Height = height, Speed = speed, FfmpegMaxProcessingSize = 2 });
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

}

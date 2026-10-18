using System.Drawing;
using System.Drawing.Imaging;
using ManiaReplayMaster.Encode;
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Render;
using EncodeEncoder = ManiaReplayMaster.Encode.Encoder;

namespace ManiaReplayMaster.RenderEncode.Tests;

[Trait("Category", "EndToEnd")]
public sealed class ScrollCadenceTests
{
    [Theory]
    [InlineData("libx264", 59)]
    [InlineData("auto", 60)]
    [InlineData("libx264", 120)]
    public void CopiedSlicesPreserveEveryFrameAndScrollAcrossMergeBoundaries(string codec, int fps)
    {
        var (ffmpeg, _) = FfmpegProbe.Resolve();
        var directory = Path.Combine(RepoPaths.CreateTempDirectory(), "渲染 it's fine");
        Directory.CreateDirectory(directory);
        try
        {
            const int width = 128, height = 160, speed = 29, imageCount = 5;
            var config = new Config
            {
                Codec = codec, FramePerSecond = fps, Width = width, Height = height,
                Speed = speed, MaxStepSize = 870, FfmpegMaxProcessingSize = 2
            };
            var map = new BeatMap { Key = 4, Duration = 2500, BgmPath = BeatMap.MuteMusic };
            using var renderer = new Renderer(map,
                new ReplayModel(new List<Note>(), 1, false, new double[] { 16, 34, 67, 97, 121, 158 }),
                Path.Combine(directory, "strip.png"), config);
            var framesPerImage = renderer.WindowFrameCount;
            for (var index = 0; index < imageCount; index++)
            {
                using var bitmap = new Bitmap(width, renderer.WindowSize);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Black);
                    var offset = (30 + (index + 1) * renderer.StepSize) % 80;
                    for (var y = offset; y < bitmap.Height; y += 80)
                        graphics.FillRectangle(Brushes.White, 16, y, width - 32, 1);
                }
                bitmap.Save(Path.Combine(directory, $"strip_{index}.png"), ImageFormat.Png);
            }

            var video = Path.Combine(directory, "scroll.mp4");
            var encoder = new EncodeEncoder(map,
                Enumerable.Range(0, imageCount).Select(index => Path.Combine(directory, $"strip_{index}.png")).ToArray(),
                video, 1, 0, framesPerImage, ffmpeg, width,
                config);
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
                // Speed is measured at 60 FPS: the same elapsed time must give the same position.
                Assert.Equal((30 + (long)frame * speed * 60 / fps) % 80, top);
            }
        }
        finally
        {
            RepoPaths.DeleteDirectory(Path.GetDirectoryName(directory)!);
        }
    }

}

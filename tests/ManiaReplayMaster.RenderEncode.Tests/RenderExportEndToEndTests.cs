using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Render;

namespace ManiaReplayMaster.RenderEncode.Tests;

/// <summary>Exports a real replay with audio and checks the resulting video streams.</summary>
[Trait("Category", "EndToEnd")]
public class RenderExportEndToEndTests
{
    private const string BeatMapFile = "ck - Carnation (ck remix) (Gekido-) [LN Master].osu";
    private const string ReplayFile = "- ck - Carnation (ck remix) [LN Master] (2021-12-06) OsuMania.osr";
    private const string DeclaredAudioFile = "carnation.mp3";

    // 116.334 s at 1.2 px/ms -> totalHeight 139620, 47 windows of 2.5 s each, windowFrameCount 150.
    private const int ExpectedImages = 47;

    [Fact]
    public void RealBgm_IsMuxedThroughTheFacadeWithAtempoAndAdelay()
    {
        var (ffmpeg, ffprobe) = FfmpegProbe.Resolve();
        var workspace = RepoPaths.CreateTempDirectory();
        try
        {
            // A copy of the fixture beatmap/replay plus a real audio file whose name matches
            // AudioFilename, so Core keeps the declared BGM instead of the mute placeholder.
            var beatMapCopy = Path.Combine(workspace, BeatMapFile);
            var replayCopy = Path.Combine(workspace, ReplayFile);
            File.Copy(RepoPaths.Fixture(BeatMapFile), beatMapCopy);
            File.Copy(RepoPaths.Fixture(ReplayFile), replayCopy);

            var bgm = Path.Combine(workspace, DeclaredAudioFile);
            FfmpegProbe.Run(ffmpeg, "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-t", "130", "-i", "sine=frequency=440:sample_rate=44100",
                "-ac", "2", "-b:a", "128k", bgm, "-y");
            Assert.True(File.Exists(bgm));
            Assert.True(new FileInfo(bgm).Length > 100_000);

            var outFile = Path.Combine(workspace, "carnation-real-bgm.mp4");
            var tempDirectory = Path.Combine(workspace, "render-temp");

            var result = RenderExporter.Run(
                beatMapCopy,
                replayCopy,
                outFile,
                tempDirectory: tempDirectory,
                config: new Config(),
                progress: null,
                ffmpegPath: ffmpeg,
                log: null);

            Assert.Equal(Path.GetFullPath(bgm), result.BeatMap.BgmPath);

            // The mute placeholder must NOT have been generated next to the images.
            Assert.False(File.Exists(Path.Combine(tempDirectory, BeatMap.MuteMusic)));

            Assert.True(File.Exists(outFile), "mp4 was not produced");
            Assert.Equal(ExpectedImages, result.Render.ImageCount);
            Assert.True(new FileInfo(outFile).Length > 100_000);

            var media = FfmpegProbe.Inspect(ffprobe, outFile);
            var video = Assert.Single(media.VideoStreams);
            Assert.Equal("h264", video.CodecName);
            Assert.Equal(540, video.Width);
            Assert.Equal(960, video.Height);

            // A real 130 s BGM proves the audio path: the mute fallback would be ~2 s.
            var audio = Assert.Single(media.AudioStreams);
            Assert.True(audio.DurationSeconds > 60.0,
                $"expected the real BGM in the output, got a {audio.DurationSeconds:F2}s audio track");
        }
        finally
        {
            RepoPaths.DeleteDirectory(workspace);
        }
    }

}

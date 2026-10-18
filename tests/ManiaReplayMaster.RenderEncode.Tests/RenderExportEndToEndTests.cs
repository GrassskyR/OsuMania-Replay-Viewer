using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Encode;
using ManiaReplayMaster.Render;
using PipelineRunner = ManiaReplayMaster.Core.Pipeline.Pipeline;

namespace ManiaReplayMaster.RenderEncode.Tests;

/// <summary>
/// Real end-to-end exports on a repository fixture (route E1: static long-strip PNGs scrolled by
/// ffmpeg). These tests launch ffmpeg and take minutes, hence the <c>EndToEnd</c> trait:
/// <c>dotnet test --filter "Category!=EndToEnd"</c> skips them.
///
/// Fixture: <c>ck - Carnation (ck remix) [LN Master]</c> -- 116.334 s, 4K mania, 1 798 notes /
/// 1 814 actions, rate 1.0. The beatmap's declared audio file is absent from the repository, so the
/// reference's mute-sound path is exercised by default (one test); a second test synthesises a real
/// BGM next to a copy of the beatmap to exercise the audio path.
///
/// NOTE on the codec profile: the reference ships <c>codecProfile: unknown</c> and passes it through
/// verbatim; <c>FfmpegCommandBuilder</c> omits the flag for values that are not real H.264 profile
/// names (a documented divergence, pinned by <c>EncoderCommandLineTests</c>).
/// Modern ffmpeg rejects it for libx264 -- <c>x264 [error]: invalid profile: unknown</c>, exit code
/// -22 -- so these end-to-end runs use the shipped default config unchanged.
/// </summary>
[Trait("Category", "EndToEnd")]
public class RenderExportEndToEndTests
{
    private const string BeatMapFile = "ck - Carnation (ck remix) (Gekido-) [LN Master].osu";
    private const string ReplayFile = "- ck - Carnation (ck remix) [LN Master] (2021-12-06) OsuMania.osr";
    private const string DeclaredAudioFile = "carnation.mp3";

    // 116.334 s at 1.2 px/ms -> totalHeight 139620, 47 windows of 2.5 s each, windowFrameCount 150.
    private const int ExpectedImages = 47;

    /// <summary>
    /// The shipped default config (<c>codecProfile = "unknown"</c>). The encoder has to omit
    /// <c>-profile:v</c> for it, so running the end-to-end tests with the defaults also guards that
    /// divergence: if the flag ever came back, these tests would fail on libx264.
    /// </summary>
    private static Config EncodeConfig() => new();

    [Theory]
    [InlineData("libx264")]
    [InlineData("auto")]
    public void MutedFixture_SplitsIntoThreeSlicePassesAndProducesPlayableMp4(string codec)
    {
        var (ffmpeg, ffprobe) = FfmpegProbe.Resolve();
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            var config = EncodeConfig();
            config.Codec = codec;
            var preparation = PipelineRunner.Prepare(
                RepoPaths.Fixture(BeatMapFile), RepoPaths.Fixture(ReplayFile));

            // The declared BGM is missing from the repository, so Core falls back to the mute placeholder.
            Assert.Equal(BeatMap.MuteMusic, preparation.BeatMap.BgmPath);

            var imagePath = Path.Combine(temp, "carnation.png");
            RenderResult rendered;
            var renderProgress = new CollectingProgress();
            using (var renderer = new Renderer(preparation.BeatMap, preparation.ReplayModel, imagePath, config))
            {
                rendered = renderer.Start(renderProgress);
            }

            Assert.Equal(ExpectedImages, rendered.ImageCount);
            Assert.Equal(150, rendered.WindowFrameCount);
            Assert.Equal(540, rendered.GameWidth);
            Assert.Equal(3960, rendered.WindowSize);
            Assert.Equal(3000, rendered.StepSize);
            Assert.Equal(139620, rendered.TotalHeight);
            Assert.Equal(47, rendered.WindowCount);
            Assert.Equal(30.0, renderProgress.Values[^1], 6);
            Assert.All(renderProgress.Values, value => Assert.InRange(value, 0.0, 30.0));

            foreach (var slice in rendered.ImagePaths)
            {
                Assert.True(File.Exists(slice), "missing slice " + slice);
            }

            var outFile = Path.Combine(temp, "carnation.mp4");
            var encoder = new Encoder(
                preparation.BeatMap,
                imagePath,
                outFile,
                preparation.ReplayModel.Rate,
                (int)preparation.Delay,
                rendered.WindowFrameCount,
                ffmpeg,
                rendered.GameWidth,
                config);

            var encodeProgress = new CollectingEncodeProgress();
            encoder.GenerateVideo(encodeProgress);

            // The mute placeholder is written next to the images and stored back into the model.
            Assert.Equal(Path.Combine(temp, BeatMap.MuteMusic), preparation.BeatMap.BgmPath);
            Assert.True(File.Exists(preparation.BeatMap.BgmPath));

            // 47 > ffmpegMaxProcessingSize (20) -> mute + 3 slice passes + 1 merge.
            var commands = encoder.ExecutedCommands;
            Assert.Equal(5, commands.Count);
            Assert.Contains("anullsrc", commands[0]);
            Assert.Contains("concat=n=20:v=1[video]", FilterGraph(commands[1]));
            Assert.Contains("concat=n=20:v=1[video]", FilterGraph(commands[2]));
            Assert.Contains("concat=n=7:v=1[video]", FilterGraph(commands[3]));
            Assert.Contains("concat", commands[4]);
            Assert.Contains("copy", commands[4]);
            Assert.Equal(2, commands[4].Count(argument => argument == "-i")); // bgm + concat list
            // commands[0] is the bare anullsrc generation, which carries no encoder options.
            Assert.All(commands.Skip(1).Take(3), command =>
                Assert.Contains(encoder.SelectedEncoder.Codec == "h264_amf" ? "-quality:v" : "-preset:v", command));
            Assert.All(commands.Skip(1), command => Assert.Contains("-benchmark", command));

            Assert.True(File.Exists(outFile), "mp4 was not produced");
            var size = new FileInfo(outFile).Length;
            Assert.True(size > 100_000, $"mp4 looks too small: {size} bytes");

            Assert.False(encodeProgress.Values.Count == 0, "no encode progress was reported");
            Assert.All(encodeProgress.Values, value => Assert.InRange(value.Percentage, 0.0, 100.0));
            Assert.Equal(100.0, encodeProgress.Values[^1].Percentage, 6);
            Assert.Equal(ProgressModel.MergeStageName, encodeProgress.Values[^1].Stage);

            var media = FfmpegProbe.Inspect(ffprobe, outFile);
            var video = Assert.Single(media.VideoStreams);
            Assert.Equal("h264", video.CodecName);
            Assert.Equal(540, video.Width);
            Assert.Equal(960, video.Height);

            // The silence placeholder is still mapped as an audio track (1 s + 1000 ms adelay).
            var audio = Assert.Single(media.AudioStreams);
            Assert.InRange(audio.DurationSeconds, 1.5, 4.0);

            // 47 windows * 2.5 s = 117.5 s of video.
            Assert.InRange(media.DurationSeconds, 116.0, 119.0);
        }
        finally
        {
            RepoPaths.DeleteDirectory(temp);
        }
    }

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
                config: EncodeConfig(),
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

    private static string FilterGraph(IReadOnlyList<string> command)
    {
        var index = command.ToList().IndexOf("-filter_complex");
        Assert.True(index >= 0, "no -filter_complex in the command line");
        return command[index + 1];
    }

    private sealed class CollectingProgress : IProgress<double>
    {
        private readonly object _gate = new();
        private readonly List<double> _values = new();

        public IReadOnlyList<double> Values
        {
            get
            {
                lock (_gate)
                {
                    return _values.ToArray();
                }
            }
        }

        public void Report(double value)
        {
            lock (_gate)
            {
                _values.Add(value);
            }
        }
    }

    private sealed class CollectingEncodeProgress : IProgress<EncodeProgress>
    {
        private readonly object _gate = new();
        private readonly List<EncodeProgress> _values = new();

        public IReadOnlyList<EncodeProgress> Values
        {
            get
            {
                lock (_gate)
                {
                    return _values.ToArray();
                }
            }
        }

        public void Report(EncodeProgress value)
        {
            lock (_gate)
            {
                _values.Add(value);
            }
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using ManiaReplayMaster.App;
using ManiaReplayMaster.App.Localization;
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Encode;
using ManiaReplayMaster.Encode.Diagnostics;
using ManiaReplayMaster.Game;
using ManiaReplayMaster.Render;
using ManiaReplayMaster.RenderEncode.Tests;

namespace ManiaReplayMaster.AotCheck;

internal static class IntegrationChecks
{
    public static void Run()
    {
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            Settings(temp);
            Caches(temp);
            Formats(temp);
            RunningCancellation(temp);
            var selected = VideoEncoderSelector.Detect(FfmpegProbe.Resolve().Ffmpeg, 180, 320, 60,
                "auto", "auto", new PortLogger(new Config { Debug = true }, Console.WriteLine));
            Console.WriteLine($"Actual encoder probe: {selected.Codec}, hardware={selected.IsHardwareAccelerated}");
        }
        finally { RepoPaths.DeleteDirectory(temp); }
    }

    private static void Settings(string temp)
    {
        var defaultConfig = new Config { Codec = "auto" };
        new UiSettings(Path.Combine(temp, "missing-settings.json")).ApplyRendering(defaultConfig);
        Assert.Equal("libx264", defaultConfig.Codec);
        var path = Path.Combine(temp, "中文设置.json");
        File.WriteAllText(path, """{"language":"en","speed":29,"codec":"h264_nvenc","codecPreset":"p7","future":{"keep":[1,"中文"]}}""");
        var settings = new UiSettings(path);
        var config = new Config();
        settings.ApplyRendering(config);
        Assert.Equal(29, config.Speed);
        Assert.Equal("p7", config.CodecPreset);
        settings.SetLanguage("zh-Hans");
        config.Codec = "libx264";
        settings.RememberRendering(config);
        Assert.Equal("中文", JsonNode.Parse(File.ReadAllText(path))!["future"]!["keep"]![1]!.GetValue<string>());
        var localizer = new Localizer(CultureInfo.GetCultureInfo("zh-CN"), path);
        Assert.Equal("渲染视频", localizer["Render"]);
        localizer.Preference = "en";
        Assert.Equal("Render", localizer["Render"]);
        localizer.Preference = "auto";
        Assert.Equal("渲染视频", localizer["Render"]);
        Assert.Equal("auto", new UiSettings(path).Language);
        Console.WriteLine("PASS settings schema/unknown fields/Chinese path/live localization/system culture");
    }

    public static void Encoders()
    {
        var temp = RepoPaths.CreateTempDirectory();
        try
        {
            Settings(temp);
            new VideoEncoderSelectorTests().AvailableChecksAllEncodersAndKeepsMultipleWorkingGpus();
            var codecs = VideoEncoderSelector.DetectAvailable(FfmpegProbe.Resolve().Ffmpeg,
                new PortLogger(sink: Console.WriteLine));
            Assert.Contains("libx264", codecs);
            var vm = new MainViewModel();
            try
            {
                var savedCodec = vm.Codec;
                var savedPreset = vm.CodecPreset;
                vm.AddAvailableCodecs(codecs);
                vm.AddAvailableCodecs(codecs);
                Assert.Equal(savedCodec, vm.Codec);
                Assert.Equal(savedPreset, vm.CodecPreset);
                Assert.Equal(vm.CodecChoices.Count, vm.CodecChoices.Distinct().Count());
                Assert.All(codecs, codec => Assert.Contains(codec, vm.CodecChoices));
                Console.WriteLine("PASS available list preserves saved codec/preset and has no duplicates");
            }
            finally { vm.Dispose(); }
            Console.WriteLine("PASS actual available encoders: " + string.Join(", ", codecs));
        }
        finally { RepoPaths.DeleteDirectory(temp); }
    }

    private static void Caches(string temp)
    {
        var game = Path.Combine(temp, "osu-fixture");
        var songs = Path.Combine(game, "Songs", "fixture");
        var replays = Path.Combine(game, "Data", "r");
        Directory.CreateDirectory(songs);
        Directory.CreateDirectory(replays);
        File.Copy(RepoPaths.Fixture("aot-taiko.osu"), Path.Combine(songs, "map.osu"));
        File.Copy(RepoPaths.Fixture("aot-taiko.osr"), Path.Combine(replays, "replay.osr"));
        var locator = GameLocator.Resolve(game);
        var options = new BeatmapLocatorOptions { CacheDirectory = Path.Combine(temp, "cache") };
        var first = new BeatmapLocator(locator, options);
        Assert.True(first.ScanSongs().Success);
        var second = new BeatmapLocator(locator, options).ScanSongs();
        Assert.Equal(1, second.FilesReusedFromCache);
        File.WriteAllText(first.CachePath, "{broken");
        Assert.False(new BeatmapLocator(locator, options).LoadCache());
        Assert.True(new BeatmapLocator(locator, options).ScanSongs().Success);
        var replayOptions = new ReplayResolverOptions { CacheDirectory = options.CacheDirectory };
        var resolver = new ReplayResolver(locator, options: replayOptions);
        Assert.True(resolver.EnsureIndex().Success);
        Assert.Equal(0, new ReplayResolver(locator, options: replayOptions).EnsureIndex().HeadersParsed);
        File.WriteAllText(resolver.CachePath, "{broken");
        Assert.Equal(1, new ReplayResolver(locator, options: replayOptions).EnsureIndex().HeadersParsed);
        Console.WriteLine("PASS Songs/replay caches first-write/reuse/corrupt-cache rebuild");
    }

    private static void Formats(string temp)
    {
        foreach (var format in new[] { "aot-minimal", "aot-taiko" })
        foreach (var fps in new[] { 59, 120 })
        foreach (var bgm in new[] { false, true })
        {
            var dir = Path.Combine(temp, $"中文 it's fine-{format}-{fps}-{bgm}");
            Directory.CreateDirectory(dir);
            var malody = format == "aot-minimal";
            var map = Path.Combine(dir, format + (malody ? ".mc" : ".osu"));
            var replay = RepoPaths.Fixture(format + (malody ? ".mr" : ".osr"));
            File.Copy(RepoPaths.Fixture(Path.GetFileName(map)), map);
            if (bgm)
                FfmpegProbe.Run(FfmpegProbe.Resolve().Ffmpeg, "-v", "error", "-f", "lavfi", "-i",
                    "sine=frequency=440:sample_rate=44100", "-t", "6", Path.Combine(dir, "missing-aot-bgm.wav"));
            var config = new Config { Width = 180, Height = 320, FramePerSecond = fps,
                MaxStepSize = bgm ? 900 : 3000, FfmpegMaxProcessingSize = bgm ? 2 : 99 };
            var result = RenderExporter.Run(map, replay, Path.Combine(dir, "video.mp4"), config: config);
            var media = FfmpegProbe.Inspect(FfmpegProbe.Resolve().Ffprobe, result.VideoPath);
            var video = Assert.Single(media.VideoStreams);
            Assert.Equal(malody ? 180 : 320, video.Width);
            Assert.Equal(malody ? 320 : 180, video.Height);
            var audio = Assert.Single(media.AudioStreams);
            var expectedVideoSeconds = result.Render.ImageCount * result.Render.WindowFrameCount / (double)fps;
            Assert.InRange(video.DurationSeconds, expectedVideoSeconds - 1.0 / fps, expectedVideoSeconds + 1.0 / fps);
            Assert.InRange(audio.DurationSeconds, (bgm ? 7.0 : 2.0) - 0.1, (bgm ? 7.0 : 2.0) + 0.1);
            Console.WriteLine($"PASS {format} export {fps}fps BGM={bgm}, {video.Width}x{video.Height}, {media.DurationSeconds:F3}s");
        }
    }

    private static void RunningCancellation(string temp)
    {
        var before = Process.GetProcessesByName("ffmpeg").Select(p => { using (p) return p.Id; }).ToHashSet();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => FfmpegRunner.Run(FfmpegProbe.Resolve().Ffmpeg,
            ["-v", "error", "-re", "-f", "lavfi", "-i", "testsrc2=s=180x320:r=60", "-t", "30", "-c:v", "libx264", Path.Combine(temp, "cancel.mp4")],
            Path.Combine(temp, "cancel.mp4"), 30, "cancel", 0, 100, new ProgressReporter(null), new PortLogger(), cancellation.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.All(Process.GetProcessesByName("ffmpeg"), p => { using (p) Assert.Contains(p.Id, before); });
        Console.WriteLine("PASS active FFmpeg cancellation/process cleanup");
    }
}

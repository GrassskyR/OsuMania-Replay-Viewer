using System.Globalization;
using System.Text.Json.Nodes;
using ManiaReplayMaster.Presentation;
using ManiaReplayMaster.Presentation.Localization;
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Model;
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
        }
        finally { RepoPaths.DeleteDirectory(temp); }
    }

    private static void Settings(string temp)
    {
        var defaultConfig = new Config { Codec = "auto" };
        new UiSettings(Path.Combine(temp, "missing-settings.json")).ApplyRendering(defaultConfig);
        Equal("libx264", defaultConfig.Codec);
        var path = Path.Combine(temp, "中文设置.json");
        File.WriteAllText(path, """{"language":"en","speed":29,"codec":"h264_nvenc","codecPreset":"p7","future":{"keep":[1,"中文"]}}""");
        var settings = new UiSettings(path);
        var config = new Config();
        settings.ApplyRendering(config);
        Equal(29, config.Speed);
        Equal("p7", config.CodecPreset);
        settings.SetLanguage("zh-Hans");
        config.Codec = "libx264";
        settings.RememberRendering(config);
        Equal("中文", JsonNode.Parse(File.ReadAllText(path))!["future"]!["keep"]![1]!.GetValue<string>());
        var localizer = new Localizer(CultureInfo.GetCultureInfo("zh-CN"), path);
        Equal("渲染视频", localizer["Render"]);
        localizer.Preference = "en";
        Equal("Render", localizer["Render"]);
        localizer.Preference = "auto";
        Equal("渲染视频", localizer["Render"]);
        Equal("auto", new UiSettings(path).Language);
        Console.WriteLine("PASS settings schema/unknown fields/Chinese path/live localization/system culture");
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
        var mapPath = Path.Combine(songs, "map.osu");
        var replayPath = Path.Combine(replays, "replay.osr");
        var header = OsuReplayHeaderReader.Parse(replayPath);
        var snapshot = new GameSnapshot
        {
            BeatmapMd5 = header.BeatmapHash, PlayerName = header.Username, Mods = header.Mods,
            Score = header.Score, MaxCombo = header.MaxCombo, HitCounts = header.HitCounts,
            ScoreDate = header.TimestampUtc, Mode = header.Mode
        };
        var replayOptions = new ReplayResolverOptions { CacheDirectory = options.CacheDirectory };
        for (var phase = 0; phase < 3; phase++) // Fresh index, reopened cache, damaged cache.
        {
            var maps = new BeatmapLocator(locator, options);
            var resolver = new ReplayResolver(locator, options: replayOptions);
            Equal(mapPath, maps.ResolveByMd5(header.BeatmapHash!, allowFreshScan: true)?.Path);
            Equal(replayPath, resolver.Resolve(snapshot).Best?.ReplayPath);
            if (phase == 1)
            {
                File.WriteAllText(maps.CachePath, "{broken");
                File.WriteAllText(resolver.CachePath, "{broken");
            }
        }
        Console.WriteLine("PASS map/replay lookup survives restart and damaged caches");
    }

    private static void Formats(string temp)
    {
        foreach (var format in new[] { "aot-minimal", "aot-taiko" })
        {
            const int fps = 60;
            var dir = Path.Combine(temp, $"中文 it's fine-{format}");
            Directory.CreateDirectory(dir);
            var malody = format == "aot-minimal";
            var map = Path.Combine(dir, format + (malody ? ".mc" : ".osu"));
            var replay = RepoPaths.Fixture(format + (malody ? ".mr" : ".osr"));
            File.Copy(RepoPaths.Fixture(Path.GetFileName(map)), map);
            var config = new Config { Width = 180, Height = 320, FramePerSecond = fps,
                MaxStepSize = 3000 };
            var result = RenderExporter.Run(map, replay, Path.Combine(dir, "video.mp4"), config: config,
                ffmpegPath: FfmpegProbe.Resolve().Ffmpeg);
            var media = FfmpegProbe.Inspect(FfmpegProbe.Resolve().Ffprobe, result.VideoPath);
            var video = media.VideoStreams.Single();
            Equal(malody ? 180 : 320, video.Width);
            Equal(malody ? 320 : 180, video.Height);
            var audio = media.AudioStreams.Single();
            Equal("aac", audio.CodecName);
            var expectedVideoSeconds = result.Render.ImageCount * result.Render.WindowFrameCount / (double)fps;
            Near(video.DurationSeconds, expectedVideoSeconds, 1.0 / fps);
            Near(audio.DurationSeconds, 2.0, 0.1);
            Console.WriteLine($"PASS {format} export {fps}fps, {video.Width}x{video.Height}, {media.DurationSeconds:F3}s");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}, got {actual}");
    }

    private static void Near(double actual, double expected, double tolerance)
    {
        if (Math.Abs(actual - expected) > tolerance)
            throw new Exception($"Expected {expected} +/- {tolerance}, got {actual}");
    }
}

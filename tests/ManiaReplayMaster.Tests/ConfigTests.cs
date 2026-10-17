using ManiaReplayMaster.Core.Model;
using Xunit;

namespace ManiaReplayMaster.Tests;

public sealed class ConfigTests
{
    [Fact]
    public void Defaults_match_the_reference_implementation()
    {
        var config = new Config();

        Assert.Equal(20, config.Speed);
        Assert.Equal(540, config.Width);
        Assert.Equal(960, config.Height);
        Assert.Equal(3, config.Stroke);
        Assert.Equal(7, config.ActionHeight);
        Assert.Equal(40, config.BlockHeight);
        Assert.Equal(60, config.FramePerSecond);
        Assert.True(config.IsMalodyPE);
        Assert.True(config.OmScoreV2Adjust);
        Assert.Equal(new List<string> { "FFFFFF", "FFD237", "79D020", "1E68C5", "E1349B" }, config.JudgementColor);
        Assert.Equal("30ED402F", config.TaikoBackgroundRed);
        Assert.Equal("30488FAD", config.TaikoBackgroundBlue);
        Assert.False(config.TaikoSingleColumn);
        Assert.Equal("88ED402F", config.TaikoForegroundRed);
        Assert.Equal("88488FAD", config.TaikoForegroundBlue);
        Assert.Equal("FF0000", config.MissColor);
        Assert.Equal("646464", config.LongNoteColor);
        Assert.False(config.Debug);
        Assert.Equal("libx264", config.Codec);
        Assert.Equal("ultrafast", config.CodecPreset);
        Assert.Equal("unknown", config.CodecProfile);
        Assert.Equal("out", config.OutputDir);
        Assert.False(config.IsServer);
        Assert.False(config.IsDesktop);
        Assert.Equal(3000, config.MaxStepSize);
        Assert.False(config.ShowHolding);
        Assert.False(config.ShowTimestamp);
        Assert.Equal(20, config.FfmpegMaxProcessingSize);
        Assert.Equal(-1, config.MaxEnabledDuration);
        Assert.False(config.ExportJudgementResults);
        Assert.False(config.ViewJudgementStatistics);
        Assert.Equal("judgementStatistics.exe", config.ViewJudgementStatisticsPath);
    }

    [Fact]
    public void DefaultYaml_round_trips_through_the_parser()
    {
        var path = Path.Combine(Path.GetTempPath(), "mrm-config-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, Config.DefaultSetting);
            var original = Config.Instance;
            try
            {
                Config.Refresh(path);
                var parsed = Config.Instance;

                Assert.Equal(20, parsed.Speed);
                Assert.Equal(60, parsed.FramePerSecond);
                Assert.True(parsed.IsMalodyPE);
                Assert.Equal(new List<string> { "FFFFFF", "FFD237", "79D020", "1E68C5", "E1349B" }, parsed.JudgementColor);
                Assert.Equal("646464", parsed.LongNoteColor);
                Assert.Equal("libx264", parsed.Codec);
                Assert.False(parsed.Debug);
            }
            finally
            {
                Config.Instance = original;
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Trailing_comments_and_quotes_are_handled()
    {
        var path = Path.Combine(Path.GetTempPath(), "mrm-config-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, """
            speed: 30 # comment after value
            longNoteColor: 'AABBCC'
            blockHeight: 40 # note height
            taikoSingleColumn: true
            showTimestamp: true
            judgementColor:
              - '111111'
              - 222222
            """);
        try
        {
            var original = Config.Instance;
            try
            {
                Config.Refresh(path);
                Assert.Equal(30, Config.Instance.Speed);
                Assert.Equal("AABBCC", Config.Instance.LongNoteColor);
                Assert.Equal(40, Config.Instance.BlockHeight);
                Assert.True(Config.Instance.TaikoSingleColumn);
                Assert.True(Config.Instance.ShowTimestamp);
                Assert.Equal(new List<string> { "111111", "222222" }, Config.Instance.JudgementColor);
            }
            finally
            {
                Config.Instance = original;
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Compressed_osu_maps_are_detected()
    {
        Assert.True(Core.Beatmap.MapReaderRegistry.IsZipMap("song.osz"));
        Assert.True(Core.Beatmap.MapReaderRegistry.IsZipMap("chart.mcz"));
        Assert.True(Core.Beatmap.MapReaderRegistry.IsZipMap("bundle.zip"));
        Assert.False(Core.Beatmap.MapReaderRegistry.IsZipMap("chart.osu"));
    }
}

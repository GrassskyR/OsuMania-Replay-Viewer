using ManiaReplayMaster.Core.Judger;
using ManiaReplayMaster.Core.Model;
using Xunit;

namespace ManiaReplayMaster.Tests;

/// <summary>
/// Port of the only test in the reference repository that had real assertions
/// (<c>OsuManiaJudgementTest.testRice</c>), plus the OD/ScoreV2 window derivations.
/// </summary>
public sealed class OsuManiaJudgementTests
{
    private static readonly double[] Window = { 16.0, 34.0, 67.0, 97.0, 121.0, 158.0 };

    [Fact]
    public void Rice_hits_land_in_the_expected_window()
    {
        for (var i = 0; i < Window.Length; i++)
        {
            var beatMap = new BeatMap { Key = 1, Notes = { new Note(1000) } };
            var startTime = (long)(1000 - Window[i] + 1);
            var replay = new ReplayModel(new List<Note> { new Note(startTime) }, 1.0, false, Window);

            new OsuManiaJudger(beatMap, replay).Judge();

            var expected = i == Window.Length - 1 ? -1 : i;
            Assert.Equal(expected, beatMap.Notes[0].JudgementStart);
        }
    }

    [Fact]
    public void Rice_at_exact_window_edge_hits_that_window()
    {
        // diff == window[i] must map to i (the comparison is `diff <= window[i]`).
        for (var i = 0; i < Window.Length - 1; i++)
        {
            var beatMap = new BeatMap { Key = 1, Notes = { new Note(1000) } };
            var replay = new ReplayModel(
                new List<Note> { new Note((long)(1000 - Window[i])) }, 1.0, false, Window);

            new OsuManiaJudger(beatMap, replay).Judge();

            Assert.Equal(i, beatMap.Notes[0].JudgementStart);
        }
    }

    /// <summary>Windows for OD 8.0 with no mods: [16, 40, 73, 103, 127, 164].</summary>
    [Fact]
    public void Mania_windows_are_derived_from_od_and_mods()
    {
        var replay = new Core.Replay.OsuReplay { Mods = 0 };

        var windows = Core.Replay.OsuReplayReader.GetJudgement(8.0, replay, BeatMap.TypeMania, isScoreV2: false);

        Assert.Equal(new double[] { 16, 40, 73, 103, 127, 164 }, windows);
    }

    [Fact]
    public void HardRock_scales_rice_windows_inward()
    {
        var replay = new Core.Replay.OsuReplay { Mods = (int)Core.Replay.OsuMods.HardRock };

        var windows = Core.Replay.OsuReplayReader.GetJudgement(8.0, replay, BeatMap.TypeMania, isScoreV2: false);

        // modeRate = 1/1.4, each element truncated to int; state the formula rather than a
        // hand-computed table so a transcription error cannot hide behind a matching literal.
        var expected = new double[] { 16, 40, 73, 103, 127, 164 }
            .Select(d => (double)(int)(d * (1.0 / 1.4)))
            .ToArray();
        Assert.Equal(expected, windows);
    }

    [Fact]
    public void Easy_scales_rice_windows_outward()
    {
        var replay = new Core.Replay.OsuReplay { Mods = (int)Core.Replay.OsuMods.Easy };

        var windows = Core.Replay.OsuReplayReader.GetJudgement(8.0, replay, BeatMap.TypeMania, isScoreV2: false);

        var expected = new double[] { 16, 40, 73, 103, 127, 164 }
            .Select(d => (double)(int)(d * 1.4))
            .ToArray();
        Assert.Equal(expected, windows);
    }

    [Fact]
    public void ScoreV2_uses_its_own_window_table()
    {
        var replay = new Core.Replay.OsuReplay { Mods = 1 << 29 };

        var windows = Core.Replay.OsuReplayReader.GetJudgement(8.0, replay, BeatMap.TypeMania, isScoreV2: true);

        // mapDifficultyRange(od=8, min, mid, max): difficulty 8 > 5,
        // so result = mid + (max - mid) * (8 - 5) / 5, truncated to int.
        static double Range(double min, double mid, double max)
            => (int)(mid + (max - mid) * (8.0 - 5) / 5);

        var expected = new[]
        {
            Range(22.4, 19.4, 13.9),
            Range(64.0, 49.0, 34.0),
            Range(97.0, 82.0, 67.0),
            Range(127.0, 112.0, 97.0),
            Range(151.0, 136.0, 121.0),
            Range(188.0, 173.0, 158.0)
        };

        Assert.Equal(expected, windows);
    }

    [Fact]
    public void Taiko_uses_three_windows()
    {
        var replay = new Core.Replay.OsuReplay { Mods = 0 };

        var windows = Core.Replay.OsuReplayReader.GetJudgement(5.0, replay, BeatMap.TypeTaiko, isScoreV2: false);

        // difficulty == 5 -> the mid value exactly.
        Assert.Equal(new double[] { 35, 80, 95 }, windows);
    }

    [Fact]
    public void Long_note_users_two_judgements_per_note_under_scorev2()
    {
        var beatMap = new BeatMap
        {
            Key = 1,
            Notes = { new Note(1000, duration: 200) }
        };
        var replay = new ReplayModel(
            new List<Note> { new Note(1000, duration: 200) }, 1.0, false, Window, isScoreV2: true);

        var judger = new OsuManiaJudger(beatMap, replay);
        judger.Judge();

        // One row for the (empty after redistribution) miss bucket and one for judgementEnd,
        // because ScoreV2 counts the tail of every long note as well.
        Assert.Equal(2, judger.JudgementFromJudging.Count);
        Assert.True(replay.Acc > 0);
    }

    [Fact]
    public void Misjudged_notes_are_counted_as_misses()
    {
        var beatMap = new BeatMap { Key = 1, Notes = { new Note(1000) } };
        var replay = new ReplayModel(new List<Note> { new Note(5000) }, 1.0, false, Window);

        var judger = new OsuManiaJudger(beatMap, replay);
        judger.Judge();

        Assert.Equal(-1, beatMap.Notes[0].JudgementStart);
        Assert.Equal(0.0, replay.Acc);
        // count[-1] is always incremented by one as the "miss" row.
        Assert.Single(judger.JudgementFromJudging);
        Assert.Equal(2, judger.JudgementFromJudging[0]);
    }

    [Fact]
    public void Mirrored_beatmap_keeps_replay_columns_untouched()
    {
        var beatMap = new BeatMap { Key = 4, Notes = { new Note(1000, 0), new Note(1000, 3) } };
        var replay = new ReplayModel(
            new List<Note> { new Note(1000, 0), new Note(1000, 3) }, 1.0, mirror: true, Window);

        // adjust() is what performs the mirroring, and it mirrors beatmap notes only --
        // the replay's column mask is derived from the pre-mirror layout.
        Core.Utils.Adjust(beatMap, replay);

        Assert.Equal(3, beatMap.Notes[0].Column);
        Assert.Equal(0, beatMap.Notes[1].Column);

        new OsuManiaJudger(beatMap, replay).Judge();

        Assert.Equal(0, beatMap.Notes[0].JudgementStart);
        Assert.Equal(0, beatMap.Notes[1].JudgementStart);
    }

    [Fact]
    public void Rate_scaling_is_applied_to_both_sides()
    {
        var beatMap = new BeatMap { Key = 1, Notes = { new Note(1500) } };
        var replay = new ReplayModel(new List<Note> { new Note(1500) }, rate: 1.5, mirror: false, Window);

        Core.Utils.Adjust(beatMap, replay);

        // Kotlin's (x / rate).toLong() truncates toward zero.
        Assert.Equal(1000, beatMap.Notes[0].TimeStamp);
        Assert.Equal(1000, replay.ReplayData[0].TimeStamp);
    }
}

using ManiaReplayMaster.Core.Judger;
using ManiaReplayMaster.Core.Model;
using Xunit;

namespace ManiaReplayMaster.Tests;

/// <summary>Judgement boundaries and independent long-note settings.</summary>
public sealed class OsuManiaJudgementTests
{
    private static readonly double[] Window = { 16.0, 34.0, 67.0, 97.0, 121.0, 158.0 };

    [Fact]
    public void Long_note_adjustment_is_isolated_between_judgers()
    {
        var note = new Note(1000, duration: 400);
        var beatMap = new BeatMap { Key = 1, Notes = { note, new Note(1200) } };
        var action = new Note(1250);
        var replay = new ReplayModel(new List<Note> { action }, 1.0, false, Window, isScoreV2: true);
        var adjusted = new OsuManiaJudger(beatMap, replay, new Config { OmScoreV2Adjust = true });
        var unadjusted = new OsuManiaJudger(beatMap, replay, new Config { OmScoreV2Adjust = false });

        Assert.Equal(Judgements.TooLate, adjusted.CanHit(action, note));
        Assert.Equal(Judgements.HitAndCanHitAgain, unadjusted.CanHit(action, note));
        Assert.Equal(Judgements.TooLate, adjusted.CanHit(action, note));
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

}

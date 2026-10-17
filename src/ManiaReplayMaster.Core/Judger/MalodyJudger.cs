// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Core.Judger;

/// <summary>Port of <c>me.replaymaster.judger.MalodyJudger</c>.</summary>
public sealed class MalodyJudger : BaseJudger
{
    public MalodyJudger(BeatMap beatMap, ReplayModel replayModel) : base(beatMap, replayModel)
    {
    }

    public override int CanHit(Note action, Note note)
    {
        if (action.Column != note.Column)
        {
            return Judgements.Ignore;
        }

        var diff = action.TimeStamp - note.TimeStamp;
        if (diff < -JudgementWindow[^1])
        {
            return Judgements.TooEarly;
        }

        return diff > JudgementWindow[^1] ? Judgements.TooLate : Judgements.Hit;
    }

    /// <summary>Malody has no tail judgement.</summary>
    public override bool JudgeRelease(Note action, Note target)
    {
        target.JudgementEnd = target.JudgementStart;
        action.JudgementEnd = action.JudgementStart;
        return false;
    }
}

// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Core.Judger;

/// <summary>
/// Port of <c>me.replaymaster.judger.OsuManiaJudger</c>.
///
/// Every branch here is copied literally from the original, including the
/// <c>omScoreV2Adjust</c> "look at the next note in the same column" heuristic and the
/// "fxxking ppy" release-position fallback. Do not tidy these up: the goal is bit-identical
/// judgement output, verified by differential tests.
/// </summary>
public sealed class OsuManiaJudger : BaseJudger
{
    private const int JMax = 0;
    private const int J300 = 1;
    private const int J200 = 2;
    private const int J100 = 3;
    private const int J50 = 4;
    private const int JMiss = 5;

    private readonly HashSet<Note> _lnHasJudged = new(ReferenceEqualityComparer.Instance);

    public OsuManiaJudger(BeatMap beatMap, ReplayModel replayModel) : base(beatMap, replayModel)
    {
    }

    public override int CanHit(Note action, Note note)
    {
        if (action.Column != note.Column)
        {
            return Judgements.Ignore;
        }

        var diff = action.TimeStamp - note.TimeStamp;
        if (-diff > JudgementWindow[JMiss])
        {
            return Judgements.TooEarly;
        }

        bool isTooLate;
        if (note.Duration != 0L)
        {
            isTooLate = action.TimeStamp - note.EndTime
                > JudgementWindow[J100] * (ReplayModel.IsScoreV2 ? 1.5 : 1.0);

            if (Config.Instance.OmScoreV2Adjust)
            {
                Note? nextNote = null;
                var i = note.Index + 1;
                while (i < BeatMap.Notes.Count)
                {
                    if (BeatMap.Notes[i].Column == note.Column)
                    {
                        nextNote = BeatMap.Notes[i];
                        break;
                    }

                    i += 1;
                }

                if (nextNote is not null)
                {
                    var nextNoteDiff = nextNote.TimeStamp - note.TimeStamp;
                    var limit = nextNoteDiff < JudgementWindow[J50]
                        ? nextNote.TimeStamp - 1
                        : nextNoteDiff > JudgementWindow[J50] * 2
                            ? nextNote.TimeStamp - JudgementWindow[J50]
                            : note.TimeStamp + JudgementWindow[J50];
                    isTooLate = action.TimeStamp > (double)limit;
                }
            }
        }
        else
        {
            isTooLate = diff >= JudgementWindow[J100];
        }

        if (isTooLate)
        {
            return Judgements.TooLate;
        }

        if (note.Duration == 0L)
        {
            return Judgements.Hit;
        }

        var j = action.Index;
        while (j >= 1)
        {
            j -= 1;
            var anotherAction = ReplayModel.ReplayData[j];
            if (anotherAction.Column != action.Column)
            {
                continue;
            }

            var endTime = anotherAction.EndTime;
            if (endTime < note.EndTime - JudgementWindow[J50])
            {
                break;
            }

            var isHoldTooLong = anotherAction.EndTime
                > (anotherAction.RelatedActionOrNote?.EndTime ?? 0)
                  + JudgementWindow[J100] * (ReplayModel.IsScoreV2 ? 1.5 : 1.0);
            if (!isHoldTooLong && anotherAction.RelatedActionOrNote is not null)
            {
                continue;
            }

            if (endTime < note.EndTime + JudgementWindow[J100] * (ReplayModel.IsScoreV2 ? 1.5 : 1.0)
                && endTime > note.TimeStamp + JudgementWindow[J50])
            {
                return Judgements.TooLate;
            }
        }

        return Judgements.HitAndCanHitAgain;
    }

    public override int GetJudgement(double diff, Note? note = null, Note? action = null)
    {
        // The original had an intentionally empty ScoreV2 branch here
        // (the LNHasJudged short-circuit is commented out); kept empty for clarity.
        if (note is not null && ReplayModel.IsScoreV2)
        {
            // intentionally empty — see the commented-out block in the Kotlin source
        }

        return base.GetJudgement(diff, note, action);
    }

    public override double CalculateAccuracy(List<Note> notes)
    {
        if (!ReplayModel.IsScoreV2)
        {
            return notes
                .Select(it => it.JudgementStart switch
                {
                    JMax => 1.0,
                    J300 => 1.0,
                    J200 => 2.0 / 3.0,
                    J100 => 1.0 / 3.0,
                    J50 => 0.5 / 3.0,
                    _ => 0.0
                })
                .Average();
        }

        var accs = new List<double>(notes.Count * 2);

        static double JudgementToAcc(int judgement) => judgement switch
        {
            JMax => 305.0 / 305,
            J300 => 300.0 / 305,
            J200 => 200.0 / 305,
            J100 => 100.0 / 305,
            J50 => 50.0 / 305,
            _ => 0.0
        };

        foreach (var note in notes)
        {
            accs.Add(JudgementToAcc(note.JudgementStart));
            if (note.Duration != 0L)
            {
                accs.Add(JudgementToAcc(note.JudgementEnd));
            }
        }

        return accs.Average();
    }

    private bool IsLnJudgedWith(long startDiff, long totalDiff, int judgement, double rate)
    {
        var judgementTime = JudgementWindow[judgement] * rate;
        return startDiff <= judgementTime && totalDiff <= judgementTime * 2;
    }

    public override bool JudgeRelease(Note action, Note target)
    {
        var endDiff = Math.Abs(action.EndTime - target.EndTime);
        var signedEndDiff = action.EndTime - target.EndTime;

        // judge end (Score V2)
        action.JudgementEnd = signedEndDiff <= JudgementWindow[J100] * 1.5
            ? GetJudgement(endDiff / 1.5)
            : -1;

        if (_lnHasJudged.Contains(target) || target.JudgementStart == -1 || target.JudgementStart == JMiss)
        {
            if (action.JudgementEnd >= JMax && action.JudgementEnd <= J100)
            {
                action.JudgementEnd = J50;
            }
        }

        target.JudgementEnd = action.JudgementEnd;

        // adjust target's judgement
        var startDiff = Math.Abs(action.TimeStamp - target.TimeStamp);
        if (target.TimeStamp - JudgementWindow[J50] > action.TimeStamp)
        {
            // fxxking ppy
            var start = target.EndTime - 1;
            startDiff = Math.Abs(target.TimeStamp - start);
        }

        var totalDiff = startDiff + endDiff;

        int totalJudgement;
        bool canBeJudgedAgain;
        if (action.EndTime - target.EndTime < -JudgementWindow[J50])
        {
            totalJudgement = -1;
            canBeJudgedAgain = true;
        }
        else
        {
            canBeJudgedAgain = false;
            totalJudgement = IsLnJudgedWith(startDiff, totalDiff, JMax, 1.2)
                ? JMax
                : IsLnJudgedWith(startDiff, totalDiff, J300, 1.1)
                    ? J300
                    : IsLnJudgedWith(startDiff, totalDiff, J200, 1.0)
                        ? J200
                        : IsLnJudgedWith(startDiff, totalDiff, J100, 1.0)
                            ? J100
                            : J50;
        }

        if (_lnHasJudged.Contains(target) && (totalJudgement == JMax || totalJudgement == J300))
        {
            totalJudgement = J200;
        }

        _lnHasJudged.Add(target);
        if (!ReplayModel.IsScoreV2)
        {
            target.JudgementStart = totalJudgement;
        }

        return canBeJudgedAgain;
    }
}

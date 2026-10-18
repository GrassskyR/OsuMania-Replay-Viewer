// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Core.Judger;

public static class Judgements
{
    public const int TooEarly = -1;
    public const int Hit = 0;
    public const int HitAndCanHitAgain = 1;
    public const int TooLate = 2;
    public const int Ignore = 3;
}

public interface IJudger
{
    void Judge();
}

/// <summary>
/// Port of <c>me.replaymaster.judger.BaseJudger</c>.
///
/// The original iterates a <c>java.util.LinkedList</c> with an <c>Iterator</c> and calls
/// <c>remove()</c> during iteration. The C# port reproduces those exact semantics —
/// removal clears the "next" pointer only (the iterator falls back to <c>prev.Next</c>),
/// which is what makes the iteration order match the original byte for byte.
/// </summary>
public abstract class BaseJudger : IJudger
{
    protected readonly BeatMap BeatMap;
    protected readonly ReplayModel ReplayModel;
    protected readonly double[] JudgementWindow;

    private readonly LinkedList<Note> _notesToJudge;
    private readonly HashSet<Note> _cannotJudge = new(ReferenceEqualityComparer.Instance);

    /// <summary>Port of <c>Main.judgementFromJudging</c>.</summary>
    public List<int> JudgementFromJudging { get; private set; } = new();

    protected BaseJudger(BeatMap beatMap, ReplayModel replayModel)
    {
        BeatMap = beatMap;
        ReplayModel = replayModel;
        _notesToJudge = new LinkedList<Note>(beatMap.Notes);
        JudgementWindow = replayModel.Judgement;
    }

    /// <summary>Mirror of the constants returned by <see cref="CanHit"/>.</summary>
    public abstract int CanHit(Note action, Note note);

    public virtual bool JudgeRelease(Note action, Note target) => false;

    public virtual double CalculateAccuracy(List<Note> notes) => 0.0;

    public virtual void OnFindTarget(Note action, Note target)
    {
        action.OffSetStart = (int)(action.TimeStamp - target.TimeStamp);
        action.OffSetEnd = (int)(action.EndTime - target.EndTime);
    }

    public virtual int GetJudgement(double diff, Note? note = null, Note? action = null)
    {
        for (var i = 0; i < JudgementWindow.Length - 1; i++)
        {
            if (diff <= JudgementWindow[i])
            {
                return i;
            }
        }

        return -1;
    }

    private Note? FindTarget(Note action)
    {
        var listNode = _notesToJudge.First;
        Note? candidate = null;
        while (listNode is not null)
        {
            var note = listNode.Value;
            var shouldDelete = false;
            var canHit = CanHit(action, note);
            if (canHit == Judgements.Ignore)
            {
                listNode = listNode.Next;
                continue;
            }

            if (canHit == Judgements.TooEarly)
            {
                break;
            }

            if (canHit == Judgements.TooLate || _cannotJudge.Contains(note))
            {
                shouldDelete = true;
            }
            else
            {
                // hit! The original captures the first candidate and then jumps out of the
                // switch and out of the loop; only HIT_AND_CAN_HIT_AGAIN leaves the node in place.
                if (candidate is null)
                {
                    candidate = note;
                    if (canHit != Judgements.HitAndCanHitAgain)
                    {
                        _notesToJudge.Remove(listNode);
                    }

                    break;
                }
            }

            if (shouldDelete)
            {
                var next = listNode.Next;
                _notesToJudge.Remove(listNode);
                listNode = next;
            }
            else
            {
                listNode = listNode.Next;
            }
        }

        if (candidate is not null)
        {
            OnFindTarget(action, candidate);
        }

        return candidate;
    }

    public virtual void Judge()
    {
        for (var index = 0; index < ReplayModel.ReplayData.Count; index++)
        {
            ReplayModel.ReplayData[index].Index = index;
        }

        for (var index = 0; index < BeatMap.Notes.Count; index++)
        {
            BeatMap.Notes[index].Index = index;
        }

        foreach (var action in ReplayModel.ReplayData)
        {
            var target = FindTarget(action);
            if (target is null)
            {
                action.ShowAsLN = false;
                continue;
            }

            var judgement = GetJudgement(Math.Abs(action.TimeStamp - target.TimeStamp), target, action);
            if (action.JudgementStart == -1)
            {
                action.JudgementStart = judgement;
            }

            if (target.JudgementStart == -1)
            {
                target.JudgementStart = judgement;
            }

            action.RelatedActionOrNote = target;
            target.RelatedActionOrNote = action;
            if (target.Duration == 0L)
            {
                action.ShowAsLN = false;
            }
            else
            {
                var canBeJudgedAgain = JudgeRelease(action, target);
                if (!canBeJudgedAgain)
                {
                    _cannotJudge.Add(target);
                }
            }
        }

        ReplayModel.Acc = CalculateAccuracy(BeatMap.Notes);

        // Port of the counting block, which is not merely debug output: the sorted values
        // become Main.judgementFromJudging and feed the judgement-export/statistics pipeline.
        var count = new Dictionary<int, int>();
        void Increment(int key)
        {
            count.TryGetValue(key, out var current);
            count[key] = current + 1;
        }

        foreach (var note in BeatMap.Notes)
        {
            Increment(note.JudgementStart);
            if (ReplayModel.IsScoreV2 && note.Duration != 0L)
            {
                Increment(note.JudgementEnd);
            }
        }

        var missJudgement = JudgementWindow.Length - 1;
        count.TryGetValue(-1, out var minusOne);
        count.TryGetValue(missJudgement, out var missCount);
        count[-1] = minusOne + missCount + 1;
        count.Remove(missJudgement);

        JudgementFromJudging = count
            .OrderBy(kv => kv.Key == -1 ? int.MaxValue : kv.Key)
            .Select(kv => kv.Value)
            .ToList();
    }
}

using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Core.Judger;

/// <summary>Port of <c>me.replaymaster.judger.OsuTaikoJudger</c>.</summary>
public sealed class OsuTaikoJudger : BaseJudger
{
    private const int J300 = 0;
    private const int J100 = 1;
    private const int JMiss = 2;

    private readonly Dictionary<Note, Note> _firstHitNoteToAction = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Note, Note> _wrongHitNoteToAction = new(ReferenceEqualityComparer.Instance);

    public OsuTaikoJudger(BeatMap beatMap, ReplayModel replayModel) : base(beatMap, replayModel)
    {
    }

    public override int CanHit(Note action, Note note)
    {
        var diff = action.TimeStamp - note.TimeStamp;
        if (IsWrongAction(action, note))
        {
            if (note.DuelNote is not null && _firstHitNoteToAction.ContainsKey(note.DuelNote))
            {
                // second hit: ignore wrong action
                return Judgements.Ignore;
            }

            return Math.Abs(diff) <= JudgementWindow[JMiss]
                ? RecordWrongHit(note, action)
                : Judgements.Ignore;
        }

        // correct action
        if (diff < -JudgementWindow[JMiss])
        {
            return Judgements.TooEarly;
        }

        if (diff > JudgementWindow[JMiss])
        {
            return Judgements.TooLate;
        }

        // in the judgable interval
        if (_wrongHitNoteToAction.TryGetValue(note, out var previousWrongAction))
        {
            _wrongHitNoteToAction.Remove(note);
            if (previousWrongAction.TimeStamp != action.TimeStamp)
            {
                return Judgements.Ignore;
            }
        }

        if (note.DuelNote is null)
        {
            // normal note
            return Judgements.Hit;
        }

        // big note
        var duelNote = note.DuelNote;
        if (!_firstHitNoteToAction.ContainsKey(duelNote))
        {
            // first hit big note
            _firstHitNoteToAction[note] = action;
            return Judgements.Hit;
        }

        // second hit big note
        var firstAction = _firstHitNoteToAction[duelNote];
        if (action.TimeStamp > firstAction.EndTime)
        {
            // first action has released: too late
            return Judgements.TooLate;
        }

        if (Math.Abs(action.TimeStamp - firstAction.TimeStamp) < 30)
        {
            // second action time - first action time < 30: can hit
            return Judgements.Hit;
        }

        // second action time - first action time >= 30: too late
        return Judgements.TooLate;
    }

    private int RecordWrongHit(Note note, Note action)
    {
        _wrongHitNoteToAction[note] = action;
        return Judgements.HitAndCanHitAgain;
    }

    public override void OnFindTarget(Note action, Note target)
    {
        base.OnFindTarget(action, target);
        if (target.Column + action.Column == 3)
        {
            target.Column = action.Column;
            if (target.DuelNote is not null)
            {
                target.DuelNote.Column = 3 - action.Column;
            }
        }
    }

    public override int GetJudgement(double diff, Note? note = null, Note? action = null)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(action);
        if (IsWrongAction(note, action))
        {
            return -1;
        }

        if (diff >= JudgementWindow[J100])
        {
            return -1;
        }

        // big note: use the previous judgement
        if (note.DuelNote is not null
            && note.DuelNote.JudgementStart <= J100
            && note.DuelNote.JudgementStart >= J300)
        {
            return note.DuelNote.JudgementStart;
        }

        if (diff < JudgementWindow[J300])
        {
            return J300;
        }

        if (diff < JudgementWindow[J100])
        {
            return J100;
        }

        return -1;
    }

    private static bool IsWrongAction(Note action, Note note)
        => note.Column != action.Column && note.Column + action.Column != 3;

    public override void Judge()
    {
        base.Judge();

        if (Config.Instance.TaikoSingleColumn)
        {
            // taiko: single column
            BeatMap.Notes = BeatMap.Notes
                .Where(it => it.DuelNote is null || it.Column <= 1)
                .Select(note =>
                {
                    note.Column = 0;
                    if (note.DuelNote is not null && note.JudgementStart == -1)
                    {
                        note.JudgementStart = note.DuelNote.JudgementStart;
                    }

                    return note;
                })
                .ToList();

            ReplayModel.ReplayData = ReplayModel.ReplayData
                .Where(it => (it.RelatedActionOrNote ?? null) is null || it.RelatedActionOrNote!.JudgementStart != -1)
                .Select(note =>
                {
                    note.Column = 0;
                    return note;
                })
                .ToList();
        }
    }
}

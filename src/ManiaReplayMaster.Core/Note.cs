// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
namespace ManiaReplayMaster.Core.Model;

/// <summary>
/// Direct port of <c>me.replaymaster.model.Note</c> (Kotlin).
///
/// The original is a mutable class that is modified in place while judging
/// (<c>judgementStart</c>, <c>column</c> mirroring/taiko remapping,
/// <c>relatedActionOrNote</c>). It is therefore intentionally a mutable class here too --
/// do not "improve" this into an immutable record, the judgement code depends on identity
/// and in-place mutation.
/// </summary>
public sealed class Note : IComparable<Note>
{
    public long TimeStamp;
    public int Column;

    /// <summary>Length of a long note; 0 for a rice/plain note.</summary>
    public long Duration;

    /// <summary>action only, for monitor: action.TimeStamp - target.TimeStamp</summary>
    public int OffSetStart;

    /// <summary>action only, for monitor: action.EndTime - target.EndTime</summary>
    public int OffSetEnd;

    /// <summary>-1 == miss</summary>
    public int JudgementStart = -1;

    /// <summary>LN tail judgement</summary>
    public int JudgementEnd = -1;

    /// <summary>taiko big note, split into two duel notes (mutual references)</summary>
    public Note? DuelNote;

    /// <summary>taiko: red (don) vs blue (kat)</summary>
    public bool TaikoIsRed = true;

    public bool ShowAsLN = true;

    /// <summary>Bidirectional link created by the judger (note &lt;-&gt; action).</summary>
    public Note? RelatedActionOrNote;

    /// <summary>Index inside its source list; assigned by <c>BaseJudger.judge()</c>.</summary>
    public int Index;

    public Note()
    {
    }

    public Note(long timeStamp = 0, int column = 0, long duration = 0,
        int offSetStart = 0, int offSetEnd = 0, int judgementStart = -1, int judgementEnd = -1,
        Note? duelNote = null, bool taikoIsRed = true)
    {
        TimeStamp = timeStamp;
        Column = column;
        Duration = duration;
        OffSetStart = offSetStart;
        OffSetEnd = offSetEnd;
        JudgementStart = judgementStart;
        JudgementEnd = judgementEnd;
        DuelNote = duelNote;
        TaikoIsRed = taikoIsRed;
    }

    public long EndTime => Duration + TimeStamp;

    public int CompareTo(Note? other)
    {
        if (other is null)
        {
            return 1;
        }

        return TimeStamp.CompareTo(other.TimeStamp);
    }

    public override string ToString()
    {
        return $"{TimeStamp} {Column} {Duration} {JudgementStart} {JudgementEnd}\n";
    }

    /// <summary>
    /// Kotlin: <c>duration = (duration / rate).toLong(); timeStamp = (timeStamp / rate).toLong()</c>.
    /// Kotlin <c>Double.toLong()</c> truncates toward zero, which is checked-cast in C#.
    /// </summary>
    public void Scale(double rate)
    {
        Duration = (long)(Duration / rate);
        TimeStamp = (long)(TimeStamp / rate);
    }

    public SingleNote ToSingleNote()
    {
        return new SingleNote(TimeStamp, Column, Duration, OffSetStart, OffSetEnd, JudgementStart, JudgementEnd);
    }

    public sealed class SingleNote
    {
        public long TimeStamp { get; init; }
        public int Column { get; init; }
        public long Duration { get; init; }
        public int OffSetStart { get; init; }
        public int OffSetEnd { get; init; }
        public int JudgementStart { get; init; }
        public int JudgementEnd { get; init; }

        public SingleNote(long timeStamp = 0, int column = 0, long duration = 0,
            int offSetStart = 0, int offSetEnd = 0, int judgementStart = -1, int judgementEnd = -1)
        {
            TimeStamp = timeStamp;
            Column = column;
            Duration = duration;
            OffSetStart = offSetStart;
            OffSetEnd = offSetEnd;
            JudgementStart = judgementStart;
            JudgementEnd = judgementEnd;
        }
    }
}

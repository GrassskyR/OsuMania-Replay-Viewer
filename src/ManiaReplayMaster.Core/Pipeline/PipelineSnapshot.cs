// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using System.Text.Json.Serialization;
using ManiaReplayMaster.Core.Judger;
using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Core.Pipeline;

/// <summary>
/// Snapshot of the judgement stage, structured per appendix C of the rewrite plan so the C#
/// output can be diffed field-by-field against the reference implementation.
/// </summary>
public sealed class PipelineSnapshot
{
    [JsonPropertyName("fixture")] public string Fixture { get; set; } = string.Empty;
    [JsonPropertyName("beatmapMd5")] public string? BeatmapMd5 { get; set; }
    [JsonPropertyName("gameMode")] public string GameMode { get; set; } = string.Empty;
    [JsonPropertyName("type")] public int Mode { get; set; }
    [JsonPropertyName("key")] public int Key { get; set; }
    [JsonPropertyName("od")] public double Od { get; set; }
    [JsonPropertyName("rate")] public double Rate { get; set; }
    [JsonPropertyName("mirror")] public bool Mirror { get; set; }
    [JsonPropertyName("isScoreV2")] public bool IsScoreV2 { get; set; }
    [JsonPropertyName("judgementWindow")] public double[] JudgementWindow { get; set; } = Array.Empty<double>();
    [JsonPropertyName("delay")] public int Delay { get; set; }
    [JsonPropertyName("durationMs")] public long DurationMs { get; set; }
    [JsonPropertyName("acc")] public double Acc { get; set; }
    [JsonPropertyName("judgementCounts")] public List<int> JudgementCounts { get; set; } = new();
    [JsonPropertyName("notes")] public List<NoteSnapshot> Notes { get; set; } = new();
    [JsonPropertyName("replayData")] public List<NoteSnapshot> ReplayData { get; set; } = new();

    /// <summary>Builds the diffable snapshot (used by the CLI benchmark and the test suite).</summary>
    public static PipelineSnapshot Create(Pipeline.Result result)
    {
        var beatMap = result.BeatMap;
        var replayModel = result.ReplayModel;

        var noteIndex = new Dictionary<Note, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < beatMap.Notes.Count; i++)
        {
            noteIndex[beatMap.Notes[i]] = i;
        }

        var actionIndex = new Dictionary<Note, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < replayModel.ReplayData.Count; i++)
        {
            actionIndex[replayModel.ReplayData[i]] = i;
        }

        var snapshot = new PipelineSnapshot
        {
            Fixture = Path.GetFileName(result.ReplayPath),
            BeatmapMd5 = result.BeatmapMd5,
            GameMode = beatMap.GameMode,
            Mode = beatMap.Type,
            Key = beatMap.Key,
            Od = beatMap.Od,
            Rate = replayModel.Rate,
            Mirror = replayModel.Mirror,
            IsScoreV2 = replayModel.IsScoreV2,
            JudgementWindow = replayModel.Judgement,
            Delay = (int)result.Delay,
            DurationMs = beatMap.Duration,
            Acc = replayModel.Acc,
            JudgementCounts = result.Judger is BaseJudger baseJudger
                ? baseJudger.JudgementFromJudging
                : new List<int>()
        };

        foreach (var note in beatMap.Notes)
        {
            snapshot.Notes.Add(ToSnapshot(note, noteIndex, actionIndex));
        }

        foreach (var note in replayModel.ReplayData)
        {
            snapshot.ReplayData.Add(ToSnapshot(note, actionIndex, noteIndex));
        }

        return snapshot;
    }

    private static NoteSnapshot ToSnapshot(Note note, Dictionary<Note, int> ownIndex,
        Dictionary<Note, int> relatedIndex)
    {
        int? related = null;
        if (note.RelatedActionOrNote is not null && relatedIndex.TryGetValue(note.RelatedActionOrNote, out var r))
        {
            related = r;
        }

        int? duel = null;
        if (note.DuelNote is not null && ownIndex.TryGetValue(note.DuelNote, out var d))
        {
            duel = d;
        }

        return new NoteSnapshot
        {
            Index = note.Index,
            TimeStamp = note.TimeStamp,
            Column = note.Column,
            Duration = note.Duration,
            JudgementStart = note.JudgementStart,
            JudgementEnd = note.JudgementEnd,
            OffSetStart = note.OffSetStart,
            OffSetEnd = note.OffSetEnd,
            ShowAsLN = note.ShowAsLN,
            RelatedActionIndex = related,
            DuelNoteIndex = duel,
            TaikoIsRed = note.DuelNote is null && !note.TaikoIsRed ? false : null
        };
    }
}

/// <summary>
/// Per-note snapshot. The cross-references are flattened to indices because the original's
/// <c>relatedActionOrNote</c> is bidirectional and would recurse forever if serialized directly.
/// </summary>
public sealed class NoteSnapshot
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("timeStamp")] public long TimeStamp { get; set; }
    [JsonPropertyName("column")] public int Column { get; set; }
    [JsonPropertyName("duration")] public long Duration { get; set; }
    [JsonPropertyName("judgementStart")] public int JudgementStart { get; set; }
    [JsonPropertyName("judgementEnd")] public int JudgementEnd { get; set; }
    [JsonPropertyName("offSetStart")] public int OffSetStart { get; set; }
    [JsonPropertyName("offSetEnd")] public int OffSetEnd { get; set; }
    [JsonPropertyName("showAsLN")] public bool ShowAsLN { get; set; }
    [JsonPropertyName("relatedActionIndex")] public int? RelatedActionIndex { get; set; }
    [JsonPropertyName("taikoIsRed")] public bool? TaikoIsRed { get; set; }
    [JsonPropertyName("duelNoteIndex")] public int? DuelNoteIndex { get; set; }
}


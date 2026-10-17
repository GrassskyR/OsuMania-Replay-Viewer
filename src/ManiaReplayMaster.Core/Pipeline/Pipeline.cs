using System.Text.Json.Serialization;
using ManiaReplayMaster.Core.Beatmap;
using ManiaReplayMaster.Core.Judger;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Core.Replay;

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

/// <summary>
/// Port of the preparation half of <c>Main.prepareJudgements</c>: resolve the replay, locate the
/// beatmap (unpacking archives / scanning directories), read both, apply the 1000 ms pre-roll
/// delay, and run the judgement.
/// </summary>
public static class Pipeline
{
    public const long DefaultDelay = 1000L;

    public sealed class Result
    {
        public required BeatMap BeatMap { get; init; }
        public required ReplayModel ReplayModel { get; init; }
        public required IJudger Judger { get; init; }
        public required IReplayReader ReplayReader { get; init; }
        public required long Delay { get; init; }
        public required string BeatmapPath { get; init; }
        public required string ReplayPath { get; init; }
        public required string? BeatmapMd5 { get; init; }
    }

    public static Result Prepare(string beatMapPath, string replayPath, string? tempDirectory = null,
        Action<string, object?[]>? log = null)
    {
        var replayFile = replayPath;
        var beatMapFile = beatMapPath;

        var replayReader = ReplayReaderRegistry.Match(replayFile)
                           ?? throw new InvalidReplayException(replayFile, new NotSupportedException("No replay reader."));

        string md5;
        try
        {
            md5 = replayReader.ReadBeatMapMd5(replayFile).ToUpperInvariant();
        }
        catch (Exception t)
        {
            throw new InvalidReplayException(Path.GetFullPath(replayFile), t);
        }

        var tempDir = tempDirectory ?? Path.Combine(Path.GetTempPath(), "mrm-" + Guid.NewGuid().ToString("N"));

        if (!Directory.Exists(beatMapFile) && MapReaderRegistry.IsZipMap(beatMapFile))
        {
            var unzipPath = Path.Combine(tempDir, "temp_map");
            Directory.CreateDirectory(unzipPath);
            log?.Invoke("read.beatmap.iscompressed", new object?[] { unzipPath });
            try
            {
                Utils.Unzip(Path.GetFullPath(beatMapFile), unzipPath);
            }
            catch (Exception t)
            {
                throw new InvalidBeatmapException(Path.GetFullPath(beatMapFile), t);
            }

            beatMapFile = unzipPath;
        }

        if (Directory.Exists(beatMapFile))
        {
            log?.Invoke("read.beatmap.dir", new object?[] { Path.GetFullPath(beatMapFile) });
            var candidates = Directory.EnumerateFiles(beatMapFile, "*", SearchOption.AllDirectories)
                .Where(f => MapReaderRegistry.Match(f) is not null)
                .ToList();

            beatMapFile = candidates.Count == 1
                ? candidates[0]
                : candidates.FirstOrDefault(f => CheckMd5(f, md5))
                  ?? throw new BeatmapNotFoundException(md5);
        }

        var md5Matched = CheckMd5(beatMapFile, md5);
        if (!md5Matched)
        {
            log?.Invoke("warning.md5", new object?[] { md5 });
        }

        log?.Invoke("read.beatmap", new object?[] { beatMapFile });
        BeatMap beatMap;
        try
        {
            beatMap = (MapReaderRegistry.Match(beatMapFile) ?? throw new InvalidBeatmapException(beatMapFile, null))
                .ReadMap(beatMapFile);
        }
        catch (Exception t)
        {
            if (t is IBaseException)
            {
                throw;
            }

            throw new InvalidBeatmapException(Path.GetFullPath(beatMapFile), t);
        }

        beatMap.CheckValidation((key, a) => log?.Invoke(key, a));

        log?.Invoke("read.replay", new object?[] { replayFile });
        ReplayModel replayModel;
        try
        {
            replayModel = replayReader.ReadReplay(replayFile, beatMap);
        }
        catch (Exception t)
        {
            if (t is IBaseException)
            {
                throw;
            }

            throw new InvalidReplayException(Path.GetFullPath(replayFile), t);
        }

        const long delay = DefaultDelay;
        foreach (var note in beatMap.Notes)
        {
            note.TimeStamp += delay;
        }

        foreach (var note in replayModel.ReplayData)
        {
            note.TimeStamp += delay;
        }

        beatMap.Duration = Math.Max(replayModel.ReplayData[^1].EndTime, beatMap.Notes[^1].EndTime) + 2000L;

        if (Config.Instance.MaxEnabledDuration > 0 && beatMap.Duration > Config.Instance.MaxEnabledDuration)
        {
            throw new DurationTooLongException();
        }

        log?.Invoke("judgement.generate", Array.Empty<object?>());
        var judger = replayReader.GetJudger(beatMap, replayModel);
        judger.Judge();

        return new Result
        {
            BeatMap = beatMap,
            ReplayModel = replayModel,
            Judger = judger,
            ReplayReader = replayReader,
            Delay = delay,
            BeatmapPath = Path.GetFullPath(beatMapFile),
            ReplayPath = Path.GetFullPath(replayFile),
            BeatmapMd5 = md5Matched ? md5 : null
        };
    }

    public static bool CheckMd5(string file, string md5)
        => string.Equals(Utils.GetMd5(file)?.ToUpperInvariant(), md5, StringComparison.Ordinal);

    /// <summary>Builds the diffable snapshot (used by the CLI benchmark and the test suite).</summary>
    public static PipelineSnapshot Build(string beatMapPath, string replayPath, bool quiet = true)
    {
        var result = Prepare(beatMapPath, replayPath);
        var beatMap = result.BeatMap;
        var replayModel = result.ReplayModel;

        var noteIndex = new Dictionary<Note, int>(ReferenceComparer.Instance);
        for (var i = 0; i < beatMap.Notes.Count; i++)
        {
            noteIndex[beatMap.Notes[i]] = i;
        }

        var actionIndex = new Dictionary<Note, int>(ReferenceComparer.Instance);
        for (var i = 0; i < replayModel.ReplayData.Count; i++)
        {
            actionIndex[replayModel.ReplayData[i]] = i;
        }

        var snapshot = new PipelineSnapshot
        {
            Fixture = Path.GetFileName(replayPath),
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

        _ = quiet;
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

    private sealed class ReferenceComparer : IEqualityComparer<Note>
    {
        public static readonly ReferenceComparer Instance = new();

        public bool Equals(Note? x, Note? y) => ReferenceEquals(x, y);

        public int GetHashCode(Note obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

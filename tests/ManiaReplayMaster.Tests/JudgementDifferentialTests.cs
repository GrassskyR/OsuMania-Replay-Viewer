using System.Text.Json;
using System.Text.Json.Serialization;
using ManiaReplayMaster.Core.Pipeline;
using Xunit;

namespace ManiaReplayMaster.Tests;

/// <summary>
/// Differential tests: the C# pipeline must reproduce, field by field, the per-note snapshots
/// produced by the reference Kotlin/Java implementation (frozen under <c>tests/golden/</c>).
///
/// These are the tests that guarantee "the judgement colours do not change". Weak assertions
/// (e.g. "it did not crash") are deliberately avoided -- per appendix C of the rewrite plan, the
/// only reliable guard is a full-field comparison of both mutated note lists.
/// </summary>
public sealed class JudgementDifferentialTests
{
    private sealed class GoldenNote
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
    }

    private sealed class Golden
    {
        [JsonPropertyName("gameMode")] public string GameMode { get; set; } = string.Empty;
        [JsonPropertyName("type")] public int Type { get; set; }
        [JsonPropertyName("key")] public int Key { get; set; }
        [JsonPropertyName("od")] public double Od { get; set; }
        [JsonPropertyName("rate")] public double Rate { get; set; }
        [JsonPropertyName("mirror")] public bool Mirror { get; set; }
        [JsonPropertyName("isScoreV2")] public bool IsScoreV2 { get; set; }
        [JsonPropertyName("judgementWindow")] public double[] JudgementWindow { get; set; } = Array.Empty<double>();
        [JsonPropertyName("durationMs")] public long DurationMs { get; set; }
        [JsonPropertyName("acc")] public double Acc { get; set; }
        [JsonPropertyName("judgementCounts")] public List<int> JudgementCounts { get; set; } = new();
        [JsonPropertyName("notes")] public List<GoldenNote> Notes { get; set; } = new();
        [JsonPropertyName("replayData")] public List<GoldenNote> ReplayData { get; set; } = new();
    }

    /// <summary>Fixture name -> (beatmap, replay) inside <c>fixtures/</c>.</summary>
    public static TheoryData<string, string, string> Fixtures => new()
    {
        {
            "KuitDoinPineNut",
            "Doin - Pine nut (Night Bunny 7) [ATTang's Lv.27].osu",
            "Kuit - Doin - Pine nut [ATTang's Lv.27] (2020-10-03) OsuMania.osr"
        },
        {
            "CarnationLN",
            "ck - Carnation (ck remix) (Gekido-) [LN Master].osu",
            "- ck - Carnation (ck remix) [LN Master] (2021-12-06) OsuMania.osr"
        },
        {
            "RaijinTempest",
            "Sound Horizon - Raijin no Hidariude (-Kamikaze-) [Tempest w _underjoy].osu",
            "Kuiiiiteeee - Sound Horizon - Raijin no Hidariude [Tempest w _underjoy] (2018-09-23) OsuMania.osr"
        },
        {
            "YuugureMarathon",
            "Various Artists - 4K LN Dan Courses v2 - Extra Level - (_underjoy) [12th Dan - Yuugure (Marathon)].osu",
            "Kuit - Various Artists - 4K LN Dan Courses v2 - Extra Level - [12th Dan - Yuugure (Marathon)] (2022-01-08) OsuMania.osr"
        }
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Judgement_matches_reference_implementation(string goldenName, string beatmapFile, string replayFile)
    {
        var golden = RepoPaths.LoadJson<Golden>(RepoPaths.GoldenFile(goldenName));
        var actual = Pipeline.Build(RepoPaths.Fixture(beatmapFile), RepoPaths.Fixture(replayFile));

        Assert.Equal(golden.GameMode, actual.GameMode);
        Assert.Equal(golden.Type, actual.Mode);
        Assert.Equal(golden.Key, actual.Key);
        Assert.Equal(golden.Od, actual.Od);
        Assert.Equal(golden.Rate, actual.Rate);
        Assert.Equal(golden.Mirror, actual.Mirror);
        Assert.Equal(golden.IsScoreV2, actual.IsScoreV2);
        Assert.Equal(golden.JudgementWindow, actual.JudgementWindow);
        Assert.Equal(golden.DurationMs, actual.DurationMs);
        Assert.Equal(golden.Acc, actual.Acc, 12);
        Assert.Equal(golden.JudgementCounts, actual.JudgementCounts);

        AssertNotesEqual(golden.Notes, actual.Notes, "notes");
        AssertNotesEqual(golden.ReplayData, actual.ReplayData, "replayData");
    }

    private static void AssertNotesEqual(
        IReadOnlyList<GoldenNote> expected,
        IReadOnlyList<NoteSnapshot> actual,
        string label)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = actual[i];
            var where = $"{label}[{i}]";

            Assert.True(e.Index == a.Index, $"{where}.index: expected {e.Index}, got {a.Index}");
            Assert.True(e.TimeStamp == a.TimeStamp, $"{where}.timeStamp: expected {e.TimeStamp}, got {a.TimeStamp}");
            Assert.True(e.Column == a.Column, $"{where}.column: expected {e.Column}, got {a.Column}");
            Assert.True(e.Duration == a.Duration, $"{where}.duration: expected {e.Duration}, got {a.Duration}");
            Assert.True(e.JudgementStart == a.JudgementStart,
                $"{where}.judgementStart: expected {e.JudgementStart}, got {a.JudgementStart}");
            Assert.True(e.JudgementEnd == a.JudgementEnd,
                $"{where}.judgementEnd: expected {e.JudgementEnd}, got {a.JudgementEnd}");
            Assert.True(e.OffSetStart == a.OffSetStart,
                $"{where}.offSetStart: expected {e.OffSetStart}, got {a.OffSetStart}");
            Assert.True(e.OffSetEnd == a.OffSetEnd,
                $"{where}.offSetEnd: expected {e.OffSetEnd}, got {a.OffSetEnd}");
            Assert.True(e.ShowAsLN == a.ShowAsLN, $"{where}.showAsLN: expected {e.ShowAsLN}, got {a.ShowAsLN}");
            Assert.True(e.RelatedActionIndex == a.RelatedActionIndex,
                $"{where}.relatedActionIndex: expected {e.RelatedActionIndex?.ToString() ?? "null"}, " +
                $"got {a.RelatedActionIndex?.ToString() ?? "null"}");
        }
    }
}

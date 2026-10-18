using System.Text;
using ManiaReplayMaster.Core.IO;
using ManiaReplayMaster.Game;
using PipelineRunner = ManiaReplayMaster.Core.Pipeline.Pipeline;

namespace ManiaReplayMaster.RenderEncode.Tests;

public sealed class ReplayResolverTests : IDisposable
{
    private readonly string _directory = RepoPaths.CreateTempDirectory();
    private const string MapFile = "Doin - Pine nut (Night Bunny 7) [ATTang's Lv.27].osu";
    private const string ReplayFile = "Kuit - Doin - Pine nut [ATTang's Lv.27] (2020-10-03) OsuMania.osr";

    [Theory]
    [InlineData("", true, true)]
    [InlineData("", false, true)]
    public void MatchingScore_ResolvesReplay_WithOrWithoutPlayerName(string player, bool useDatabase, bool nullMarker)
    {
        var (resolver, path, header) = CreateReplay(player, useDatabase, nullMarker);
        var result = resolver.Resolve(Snapshot(header, player));

        Assert.True(result.IsConfident, result.Best?.Explanation);
        Assert.Equal(path, result.Best?.ReplayPath);
        Assert.True(result.Best?.FileExists);
        Assert.DoesNotContain("player=DIFF", result.Best!.Explanation!);
        // The same blank-name replay must remain usable by the actual judgement/render pipeline.
        var prepared = PipelineRunner.Prepare(RepoPaths.Fixture(MapFile), path);
        Assert.NotEmpty(prepared.BeatMap.Notes);
    }

    [Theory]
    [InlineData("Player", "")]
    public void DifferentPlayer_DoesNotBecomeAnExactMatch(string observedPlayer, string replayPlayer)
    {
        var (resolver, _, header) = CreateReplay(replayPlayer, true);
        var result = resolver.Resolve(Snapshot(header, observedPlayer), allowScan: false);
        Assert.False(result.IsConfident);
    }

    [Fact]
    public void EmptyNameWithoutScoreIdentity_IsNotSufficientForAutoFill()
    {
        var (resolver, _, header) = CreateReplay("", true);
        var result = resolver.Resolve(new GameSnapshot
        {
            BeatmapMd5 = header.BeatmapHash, PlayerName = "", Mods = header.Mods
        }, allowScan: false);
        Assert.False(result.IsConfident);
        result = resolver.Resolve(new GameSnapshot
        {
            BeatmapMd5 = header.BeatmapHash, PlayerName = null, Mods = header.Mods,
            Score = header.Score // one matching score alone cannot compensate for missing identity
        }, allowScan: false);
        Assert.False(result.IsConfident);
    }

    private (ReplayResolver Resolver, string Path, OsuReplayHeader Header) CreateReplay(string player, bool useDatabase, bool nullMarker = false)
    {
        var original = RepoPaths.Fixture(ReplayFile);
        long start, end;
        using (var reader = OsuBinaryReader.OpenFile(original))
        {
            reader.ReadByte(); reader.ReadInt32(); reader.ReadLine();
            start = reader.Position;
            reader.ReadLine();
            end = reader.Position;
        }
        var replayDirectory = Path.Combine(_directory, "Data", "r");
        Directory.CreateDirectory(replayDirectory);
        var originalHeader = OsuReplayHeaderReader.Parse(original);
        var path = Path.Combine(replayDirectory,
            OsuReplayHeaderReader.ComposeDataRFileName(originalHeader.BeatmapHash!, originalHeader.TimestampTicks));
        var bytes = File.ReadAllBytes(original);
        using (var writer = new BinaryWriter(File.Create(path), Encoding.UTF8))
        {
            writer.Write(bytes, 0, (int)start);
            WriteString(writer, player, nullMarker);
            writer.Write(bytes, (int)end, bytes.Length - (int)end);
        }
        var header = OsuReplayHeaderReader.Parse(path);
        File.SetLastWriteTimeUtc(path, header.TimestampUtc.UtcDateTime);
        if (useDatabase)
        {
            using var writer = new BinaryWriter(File.Create(Path.Combine(_directory, "scores.db")), Encoding.UTF8);
            writer.Write(20260924); writer.Write(1);
            WriteString(writer, header.BeatmapHash!); writer.Write(1);
            writer.Write((byte)header.Mode); writer.Write(header.FileVersion);
            WriteString(writer, header.BeatmapHash!); WriteString(writer, player, nullMarker);
            WriteString(writer, header.ReplayHash!);
            foreach (var count in header.HitCounts) writer.Write((ushort)count);
            writer.Write(header.Score); writer.Write((ushort)header.MaxCombo); writer.Write(header.Perfect);
            writer.Write(header.Mods); writer.Write((byte)0);
            writer.Write(header.TimestampTicks); writer.Write(-1); writer.Write(0L);
        }
        return (new ReplayResolver(GameLocator.FromRoot(_directory), options: new ReplayResolverOptions
        {
            UseCache = false, CacheDirectory = Path.Combine(_directory, "cache")
        }), path, header);
    }

    private static GameSnapshot Snapshot(OsuReplayHeader header, string? player) => new()
    {
        State = OsuGameState.Results, Status = MemoryReadStatus.Ok,
        BeatmapMd5 = header.BeatmapHash, PlayerName = player, Mode = header.Mode,
        Mods = header.Mods, Score = header.Score, MaxCombo = header.MaxCombo,
        HitCounts = header.HitCounts, ScoreDate = header.TimestampUtc
    };

    private static void WriteString(BinaryWriter writer, string value, bool emptyAsNull = false)
    {
        if (value.Length == 0 && emptyAsNull)
        {
            writer.Write((byte)0);
            return;
        }
        writer.Write((byte)0x0b);
        writer.Write(value);
    }

    public void Dispose() => RepoPaths.DeleteDirectory(_directory);
}

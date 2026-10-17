using System.Text;
using ManiaReplayMaster.Render;

namespace ManiaReplayMaster.RenderEncode.Tests;

public sealed class ReplayVideoFileNameTests : IDisposable
{
    private readonly string _directory = RepoPaths.CreateTempDirectory();

    [Theory]
    [InlineData(0, "Osu")]
    [InlineData(1, "Taiko")]
    [InlineData(2, "CatchTheBeat")]
    [InlineData(3, "OsuMania")]
    public void HashedReplay_UsesUtf8HeaderAndMetadataWithReplayMode(byte mode, string modeName)
    {
        var (map, replay) = WriteInputs("俊达萌", mode);
        Assert.Equal($"俊达萌 - Artist - Song [Difficulty] (2026-10-13) {modeName}.mp4",
            ReplayVideoFileName.Create(map, replay));
    }

    [Fact]
    public void RealReplay_RenamedToHash_StillProducesExportName()
    {
        const string stem = "Kuit - Doin - Pine nut [ATTang's Lv.27] (2020-10-03) OsuMania";
        var replay = Path.Combine(_directory, "c6487dd157b63778054999463c44e163-134363722214384367.osr");
        File.Copy(RepoPaths.Fixture(stem + ".osr"), replay);
        Assert.Equal(stem + ".mp4", ReplayVideoFileName.Create(
            RepoPaths.Fixture("Doin - Pine nut (Night Bunny 7) [ATTang's Lv.27].osu"), replay));
    }

    [Fact]
    public void InvalidFilenameCharacters_AreReplaced_AndMetadataValuesKeepColons()
    {
        var (map, replay) = WriteInputs("玩家/一", title: "Song: Part 2?", difficulty: "Hard/Extra*");
        Assert.Equal("玩家_一 - Artist - Song_ Part 2_ [Hard_Extra_] (2026-10-13) OsuMania.mp4",
            ReplayVideoFileName.Create(map, replay));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingPlayer_FallsBackToSourceName(string player)
    {
        var (map, replay) = WriteInputs(player);
        Assert.Equal("hash-123.mp4", ReplayVideoFileName.Create(map, replay));
    }

    [Fact]
    public void MissingMetadataOrTruncatedHeader_FallsBackToSourceName()
    {
        var (map, replay) = WriteInputs("Player");
        File.WriteAllText(map, "[General]\nTitle: Not metadata\n");
        Assert.Equal("hash-123.mp4", ReplayVideoFileName.Create(map, replay));
        File.WriteAllBytes(replay, new byte[] { 3, 0 });
        Assert.Equal("hash-123.mp4", ReplayVideoFileName.Create(map, replay));
        // A corrupt string byte length is rejected as InvalidDataException by the core reader.
        File.WriteAllBytes(replay, new byte[] { 3, 0, 0, 0, 0, 0x0b, 0xff, 0xff, 0xff, 0x7f });
        Assert.Equal("hash-123.mp4", ReplayVideoFileName.Create(map, replay));
    }

    [Fact]
    public void Malody_KeepsSourceName_AndProtectsWindowsDeviceNames()
    {
        Assert.Equal("Sewing.mp4", ReplayVideoFileName.Create("map.mc", "Sewing.mr"));
        Assert.Equal("_CON.mp4", ReplayVideoFileName.Create("map.mc", "CON.mr"));
    }

    [Fact]
    public void LongNames_AreBoundedAndDistinct_WithNoSplitSurrogates()
    {
        var title = string.Concat(Enumerable.Repeat("🎵", 150));
        var (map, replay) = WriteInputs(new string('玩', 60), title: title);
        var first = ReplayVideoFileName.Create(map, replay);
        var (otherMap, _) = WriteInputs(new string('玩', 60), title: title + "2");
        var second = ReplayVideoFileName.Create(otherMap, replay);
        Assert.InRange(first.Length, 1, 240);
        Assert.NotEqual(first, second);
        Assert.Equal(first, new UTF8Encoding(false, true).GetString(new UTF8Encoding(false, true).GetBytes(first)));
    }

    private (string Map, string Replay) WriteInputs(string player, byte mode = 3,
        string title = "Song", string difficulty = "Difficulty")
    {
        var map = Path.Combine(_directory, "map.osu");
        File.WriteAllText(map, $"osu file format v14\n[General]\nMode:3\nTitle: Ignore\n" +
            $"[Metadata]\nArtist:Artist\nTitle:{title}\nVersion:{difficulty}\n" +
            "[Events]\nTitle: Ignore this too\n");
        var replay = Path.Combine(_directory, "hash-123.osr");
        using var writer = new BinaryWriter(File.Create(replay), Encoding.UTF8);
        writer.Write(mode);
        writer.Write(20261013);
        WriteString(writer, new string('a', 32));
        WriteString(writer, player);
        WriteString(writer, new string('b', 32));
        for (var i = 0; i < 6; i++) writer.Write((ushort)0);
        writer.Write(0); // score
        writer.Write((ushort)0); // combo
        writer.Write(false);
        writer.Write(0); // mods
        writer.Write((byte)0); // life graph
        writer.Write(new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks);
        // No payload: naming must only read the header.
        return (map, replay);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        writer.Write((byte)0x0b);
        writer.Write(value);
    }

    public void Dispose() => RepoPaths.DeleteDirectory(_directory);
}

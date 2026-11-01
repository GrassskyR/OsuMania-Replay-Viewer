using ManiaReplayMaster.Game;
using ManiaReplayMaster.Game.Memory;

namespace ManiaReplayMaster.GameCheck;

internal static class StableLayoutChecks
{
    public static void Run(ProcessMemory memory, ulong arena, bool results)
    {
        var scanner = new SignatureScanner(memory);
        var addresses = new Dictionary<string, ulong>();
        foreach (var entry in SignatureTables.Stable.Entries.Where(e => e.Name is
                     SignatureTables.BaseAddr or SignatureTables.StatusPtr or SignatureTables.RulesetsAddr or
                     SignatureTables.SettingsClassAddr or SignatureTables.MenuModsPtr or
                     SignatureTables.GameTimePtr or SignatureTables.CanRunSlowlyAddr))
        {
            var pattern = MemoryPattern.Parse(entry.Pattern);
            var hits = scanner.ScanRange(arena, arena + 0x780, pattern);
            if (hits.Count != 1) throw new Exception($"Cannot locate {entry.Name} in score fixture.");
            if (scanner.TryResolveSignature(entry, pattern, hits[0], out var address, out _))
                addresses[entry.Name] = address;
        }
        var reader = new OsuStableReader(memory, default,
            new SignatureResolution(addresses, new Dictionary<string, string>()));
        var snapshot = reader.ReadSnapshot();
        Equal(snapshot.State, results ? OsuGameState.Results : OsuGameState.Playing, "correct screen");
        Equal(snapshot.Status, MemoryReadStatus.Ok, "snapshot usable");
        Equal(snapshot.BeatmapMd5, "d41d8cd98f00b204e9800998ecf8427e", "double indirection to beatmap");
        Equal(snapshot.BeatmapFolder, "123 测试曲目", "CLR UTF-16 folder");
        Equal(snapshot.BeatmapOsuFile, "测试 [4K].osu", "CLR UTF-16 filename");
        Equal(snapshot.PlayerName, "玩家🚀", "CLR UTF-16 player including surrogate pair");
        Equal(snapshot.Score, (int?)(results ? 739977 : 123456), "score uses screen-specific object");
        Equal(snapshot.MaxCombo, (int?)326, "combo reads ushort, not adjacent data");
        Equal(snapshot.Mode, GameMode.OsuMania, "actual mode");
        Equal(snapshot.Mods, results ? 8 : 64, "mods from score XOR, not menu mods");
        Check(snapshot.HitCounts?.SequenceEqual(new[] { 3260, 214, 9, 4315, 920, 168 }) == true,
            "300 and 100 use correct offsets", string.Join('/', snapshot.HitCounts ?? Array.Empty<int>()));
        Equal(snapshot.ScoreDate, (DateTimeOffset?)new DateTimeOffset(2026, 10, 17, 1, 2, 3, TimeSpan.Zero),
            "DateTime binary kind bits decoded");
        Equal(snapshot.IsWatchingReplay, !results, "replay flag instruction operand dereferenced");
        reader.TryReadField(OsuStableStructure.SongsDirectory, out var songs, out _);
        Equal(songs.Text, @"D:\osu\Songs", "settings string");
        Console.WriteLine("PASS read " + (results ? "results screen score" : "gameplay score"));
    }

    private static void Equal<T>(T actual, T expected, string field)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new Exception($"{field}: expected {expected}, got {actual}");
    }

    private static void Check(bool success, string field, string actual)
    {
        if (!success) throw new Exception($"{field}: got {actual}");
    }
}

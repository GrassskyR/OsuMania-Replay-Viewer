using ManiaReplayMaster.Game;
using ManiaReplayMaster.Game.Memory;

namespace ManiaReplayMaster.GameCheck;

internal static class StableLayoutChecks
{
    public static void Run(CheckRunner runner, ProcessMemory memory, ulong arena, bool results)
    {
        runner.Section("Stable layout regression: " + (results ? "results screen" : "replay gameplay"));
        var scanner = new SignatureScanner(memory);
        var addresses = new Dictionary<string, ulong>();
        foreach (var entry in SignatureTables.Stable.Entries.Where(e => e.Name is
                     SignatureTables.BaseAddr or SignatureTables.StatusPtr or SignatureTables.RulesetsAddr or
                     SignatureTables.SettingsClassAddr or SignatureTables.MenuModsPtr or
                     SignatureTables.GameTimePtr or SignatureTables.CanRunSlowlyAddr))
        {
            var pattern = MemoryPattern.Parse(entry.Pattern);
            var hits = scanner.ScanRange(arena, arena + 0x780, pattern);
            runner.Check(hits.Count == 1, entry.Name + " has one fixture match", hits.Count.ToString());
            if (hits.Count == 1 && scanner.TryResolveSignature(entry, pattern, hits[0], out var address, out _))
                addresses[entry.Name] = address;
        }
        var reader = new OsuStableReader(memory, default,
            new SignatureResolution(addresses, new Dictionary<string, string>()));
        var snapshot = reader.ReadSnapshot();
        runner.CheckEqual(snapshot.State, results ? OsuGameState.Results : OsuGameState.Playing, "correct screen");
        runner.CheckEqual(snapshot.Status, MemoryReadStatus.Ok, "snapshot usable");
        runner.CheckEqual(snapshot.BeatmapMd5, "d41d8cd98f00b204e9800998ecf8427e", "double indirection to beatmap");
        runner.CheckEqual(snapshot.BeatmapFolder, "123 测试曲目", "CLR UTF-16 folder");
        runner.CheckEqual(snapshot.BeatmapOsuFile, "测试 [4K].osu", "CLR UTF-16 filename");
        runner.CheckEqual(snapshot.PlayerName, "玩家🚀", "CLR UTF-16 player including surrogate pair");
        runner.CheckEqual(snapshot.Score, (int?)(results ? 739977 : 123456), "score uses screen-specific object");
        runner.CheckEqual(snapshot.MaxCombo, (int?)326, "combo reads ushort, not adjacent data");
        runner.CheckEqual(snapshot.Mode, GameMode.OsuMania, "actual mode");
        runner.CheckEqual(snapshot.Mods, results ? 8 : 64, "mods from score XOR, not menu mods");
        runner.Check(snapshot.HitCounts?.SequenceEqual(new[] { 3260, 214, 9, 4315, 920, 168 }) == true,
            "300 and 100 use correct offsets", string.Join('/', snapshot.HitCounts ?? Array.Empty<int>()));
        runner.CheckEqual(snapshot.ScoreDate, (DateTimeOffset?)new DateTimeOffset(2026, 10, 17, 1, 2, 3, TimeSpan.Zero),
            "DateTime binary kind bits decoded");
        runner.CheckEqual(snapshot.IsWatchingReplay, !results, "replay flag instruction operand dereferenced");
        reader.TryReadField(OsuStableStructure.SongsDirectory, out var songs, out _);
        runner.CheckEqual(songs.Text, @"D:\osu\Songs", "settings string");
    }
}

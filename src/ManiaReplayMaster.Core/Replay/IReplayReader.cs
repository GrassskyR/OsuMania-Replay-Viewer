using ManiaReplayMaster.Core.Judger;
using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Core.Replay;

/// <summary>Port of <c>me.replaymaster.replay.IReplayReader</c>.</summary>
public interface IReplayReader
{
    /// <summary>File extension without the dot, e.g. <c>osr</c>.</summary>
    string Extension { get; }

    string ReadBeatMapMd5(string replayPath);

    ReplayModel ReadReplay(string path, BeatMap beatMap);

    /// <summary>Creates the judger appropriate for this replay format + beatmap type.</summary>
    IJudger GetJudger(BeatMap beatMap, ReplayModel replayModel);
}

public static class ReplayReaderRegistry
{
    private static readonly IReplayReader[] Readers =
    {
        new OsuReplayReader(),
        new MalodyReplayReader()
    };

    public static IReplayReader? Match(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        return Readers.FirstOrDefault(r => string.Equals(r.Extension, extension, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<IReplayReader> All => Readers;
}

using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Core.Beatmap;

/// <summary>Port of <c>me.replaymaster.beatmap.IMapReader</c>.</summary>
public interface IMapReader
{
    string MapExtension { get; }

    string ZipExtension { get; }

    BeatMap ReadMap(string path);
}

public static class MapReaderRegistry
{
    private static readonly IMapReader[] Readers =
    {
        new OsuMapReader(),
        new MalodyMapReader()
    };

    public static bool IsZipMap(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        return string.Equals(extension, "zip", StringComparison.OrdinalIgnoreCase)
            || Readers.Any(r => string.Equals(r.ZipExtension, extension, StringComparison.OrdinalIgnoreCase));
    }

    public static IMapReader? Match(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        return Readers.FirstOrDefault(r => string.Equals(r.MapExtension, extension, StringComparison.OrdinalIgnoreCase));
    }
}

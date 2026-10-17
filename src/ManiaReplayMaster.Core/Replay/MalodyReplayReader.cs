using ManiaReplayMaster.Core.IO;
using ManiaReplayMaster.Core.Judger;
using ManiaReplayMaster.Core.Model;

namespace ManiaReplayMaster.Core.Replay;

/// <summary>Port of <c>me.replaymaster.replay.MalodyReplayReader</c> and its nested types.</summary>
public sealed class MalodyReplayReader : IReplayReader
{
    public string Extension => "mr";

    /// <summary>Judge A–E × (PE adds 9 ms).</summary>
    public static readonly double[][] JudgementTable =
    {
        new[] { 52.0, 92.0, 126.0, 150.0 }, // Judge A
        new[] { 44.0, 84.0, 118.0, 150.0 }, // Judge B
        new[] { 36.0, 76.0, 110.0, 150.0 }, // Judge C
        new[] { 28.0, 68.0, 102.0, 150.0 }, // Judge D
        new[] { 20.0, 60.0, 94.0, 150.0 } // Judge E
    };

    [Flags]
    public enum MalodyMod
    {
        NoMod = 0,
        Luck = 1 << 1,
        Flip = 1 << 2,
        Const = 1 << 3,
        Dash = 1 << 4,
        Rash = 1 << 5,
        Hide = 1 << 6,
        Slow = 1 << 8,
        Death = 1 << 9
    }

    public static bool BelongsTo(int value, MalodyMod mod) => (value & (int)mod) > 0;

    /// <summary>Port of <c>MalodyMod.toString(value)</c>.</summary>
    public static string ModToString(int value)
    {
        var names = new List<string>();
        foreach (var mod in Enum.GetValues<MalodyMod>())
        {
            if (mod != MalodyMod.NoMod && BelongsTo(value, mod))
            {
                names.Add(mod.ToString());
            }
        }

        return string.Join(", ", names);
    }

    private static double[] GetJudgement(int judgeLevel, double rate)
    {
        return JudgementTable[judgeLevel]
            .Select(it => (it + (Config.Instance.IsMalodyPE ? 9 : 0)) / rate)
            .ToArray();
    }

    public IJudger GetJudger(BeatMap beatMap, Model.ReplayModel replayModel) => new MalodyJudger(beatMap, replayModel);

    public string ReadBeatMapMd5(string replayPath)
    {
        using var reader = OsuBinaryReader.OpenFile(replayPath);
        CheckFormatValid(replayPath, reader.ReadMalodyString() == "mr format head");
        _ = reader.ReadByteAsHexString(4); // version
        return reader.ReadMalodyString() ?? string.Empty;
    }

    public Model.ReplayModel ReadReplay(string path, BeatMap beatMap)
    {
        using var reader = OsuBinaryReader.OpenFile(path);

        CheckFormatValid(path, reader.ReadMalodyString() == "mr format head");
        _ = reader.ReadByteAsHexString(4); // version
        _ = reader.ReadMalodyString(); // md5
        _ = reader.ReadMalodyString(); // difficulty
        _ = reader.ReadMalodyString(); // map name
        _ = reader.ReadMalodyString(); // author

        _ = reader.ReadInt32(); // score
        _ = reader.ReadInt32(); // combo
        var best = reader.ReadInt32();
        var cool = reader.ReadInt32();
        var good = reader.ReadInt32();
        var miss = reader.ReadInt32();
        _ = reader.ReadInt32(); // miss2

        JudgementFromReplay = new List<int> { best, cool, good, miss };

        var mod = reader.ReadInt32();
        var rate = BelongsTo(mod, MalodyMod.Dash) ? 1.2
            : BelongsTo(mod, MalodyMod.Rash) ? 1.5
            : BelongsTo(mod, MalodyMod.Slow) ? 0.8
            : 1.0;

        var judge = reader.ReadInt32(); // 0-4: A-E
        var judgement = GetJudgement(judge, rate);

        CheckFormatValid(path, reader.ReadMalodyString() == "mr data");
        _ = reader.ReadByteAsHexString(4); // version

        var eventCount = reader.ReadInt32();
        _ = reader.ReadByteAsHexString(1); // unknown5
        _ = reader.ReadInt32(); // playTime
        _ = reader.ReadByteAsHexString(4); // unknown6

        var currentHold = new Dictionary<int, Note>();
        var notes = new List<Note>();
        var rawEvents = new List<MalodyRawHitEvent>(eventCount);

        for (var i = 0; i < eventCount; i++)
        {
            var timeStamp = reader.ReadInt32();
            var pressOrRelease = reader.ReadByte();
            var column = reader.ReadByte();
            rawEvents.Add(new MalodyRawHitEvent(timeStamp, pressOrRelease, column));
        }

        rawEvents.Sort();

        foreach (var rawEvent in rawEvents)
        {
            var timeStamp = rawEvent.TimeStamp;
            var pressOrRelease = rawEvent.PressOrRelease;
            var column = rawEvent.Column;

            if (timeStamp < 0)
            {
                continue;
            }

            CheckFormatValid(path, pressOrRelease is 2 or 1);
            CheckFormatValid(path, column < beatMap.Key);

            if (pressOrRelease == 1)
            {
                // press
                if (currentHold.TryGetValue(column, out var open))
                {
                    open.Duration = timeStamp - open.TimeStamp;
                    currentHold.Remove(column);
                }

                var newNote = new Note(timeStamp, column, 10000);
                currentHold[column] = newNote;
                notes.Add(newNote);
            }
            else
            {
                // release
                if (currentHold.TryGetValue(column, out var open))
                {
                    open.Duration = timeStamp - open.TimeStamp;
                    currentHold.Remove(column);
                }
            }
        }

        var replayModel = new Model.ReplayModel(notes, rate, BelongsTo(mod, MalodyMod.Flip), judgement);
        Utils.Adjust(beatMap, replayModel);
        return replayModel;
    }

    /// <summary>Port of <c>Main.judgementFromReplay</c>: Malody readers write it as a side effect.</summary>
    public static List<int> JudgementFromReplay { get; set; } = new();

    private static void CheckFormatValid(string path, bool valid)
    {
        if (!valid)
        {
            throw new InvalidReplayException(path, new InvalidDataException("Malody replay format check failed."));
        }
    }
}

/// <summary>Port of <c>MalodyRawHitEvent</c> including its unusual tie-breaking comparison.</summary>
public readonly struct MalodyRawHitEvent : IComparable<MalodyRawHitEvent>
{
    public readonly int TimeStamp;
    public readonly int PressOrRelease;
    public readonly int Column;

    public MalodyRawHitEvent(int timeStamp, int pressOrRelease, int column)
    {
        TimeStamp = timeStamp;
        PressOrRelease = pressOrRelease;
        Column = column;
    }

    /// <summary>
    /// Kotlin: <c>-pressOrRelease.compareTo(other.pressOrRelease)</c> — i.e. releases (2) sort
    /// before presses (1) at equal timestamps.
    /// </summary>
    public int CompareTo(MalodyRawHitEvent other)
    {
        var timeCompare = TimeStamp.CompareTo(other.TimeStamp);
        return timeCompare != 0 ? timeCompare : -PressOrRelease.CompareTo(other.PressOrRelease);
    }
}

/// <summary>Port of the <c>readMalodyString()</c> / <c>readByteAsHexString()</c> extensions.</summary>
public static class MalodyBinaryExtensions
{
    public static string? ReadMalodyString(this OsuBinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0)
        {
            return null;
        }

        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
        {
            bytes[i] = reader.ReadByte();
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public static string ReadByteAsHexString(this OsuBinaryReader reader, int count)
    {
        var parts = new string[count];
        for (var i = 0; i < count; i++)
        {
            parts[i] = reader.ReadByte().ToString("X2");
        }

        return string.Join(", ", parts);
    }
}

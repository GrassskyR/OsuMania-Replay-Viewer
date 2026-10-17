using ManiaReplayMaster.Core.IO;

namespace ManiaReplayMaster.Cli;

/// <summary>Diagnostic: dumps raw .osr header field boundaries so the reader can be audited.</summary>
internal static class OsrTrace
{
    public static int Run(string path, int hexCount = 64)
    {
        var bytes = File.ReadAllBytes(path);
        Console.WriteLine($"file={Path.GetFileName(path)} length={bytes.Length}");
        Console.WriteLine("raw hex (verified via array index, not hand-counted):");
        for (var i = 0; i < Math.Min(hexCount, bytes.Length); i++)
        {
            var c = bytes[i] >= 32 && bytes[i] < 127 ? (char)bytes[i] : '.';
            Console.WriteLine($"  [{i,3}] 0x{bytes[i]:X2} '{c}'");
        }

        using var reader = OsuBinaryReader.OpenFile(path);
        Console.WriteLine($"file={Path.GetFileName(path)} length={reader.Length}");

        var mode = reader.ReadByte();
        Console.WriteLine($"pos={reader.Position,4}  mode={mode}");
        var version = reader.ReadInt32();
        Console.WriteLine($"pos={reader.Position,4}  version={version}");

        for (var i = 0; i < 3; i++)
        {
            var start = reader.Position;
            var value = reader.ReadLine();
            Console.WriteLine($"pos={reader.Position,4}  string[{i}] start={start} len={value?.Length ?? -1} value=[{value}]");
        }

        var counts = new int[6];
        for (var i = 0; i < 6; i++)
        {
            counts[i] = reader.ReadUInt16();
        }

        Console.WriteLine($"pos={reader.Position,4}  counts=[{string.Join(",", counts)}]");
        var points = reader.ReadInt32();
        Console.WriteLine($"pos={reader.Position,4}  points={points}");
        var combo = reader.ReadUInt16();
        Console.WriteLine($"pos={reader.Position,4}  maxCombo={combo}");
        var perfect = reader.ReadBoolean();
        Console.WriteLine($"pos={reader.Position,4}  perfect={perfect}");
        var mods = reader.ReadInt32();
        Console.WriteLine($"pos={reader.Position,4}  mods={mods} (0x{mods:X})");

        var diagramStart = reader.Position;
        var diagram = reader.ReadLine();
        Console.WriteLine($"pos={reader.Position,4}  diagram start={diagramStart} len={diagram?.Length ?? -1}");
        var ticks = reader.ReadInt64();
        Console.WriteLine($"pos={reader.Position,4}  ticks={ticks}");
        var compressedLength = reader.ReadInt32();
        Console.WriteLine($"pos={reader.Position,4}  compressedLength={compressedLength} remaining={reader.Length - reader.Position}");

        // Direct decoder check, bypassing the .osr parser entirely.
        try
        {
            var replay = ManiaReplayMaster.Core.Replay.OsuReplayParser.Parse(path);
            var decoded = ManiaReplayMaster.Core.Replay.OsuReplayParser.Decompress(replay);
            Console.WriteLine($"decoded={decoded.Length} head={System.Text.Encoding.ASCII.GetString(decoded, 0, Math.Min(80, decoded.Length))}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"decoder FAILED: {ex.Message}");
        }

        return 0;
    }
}

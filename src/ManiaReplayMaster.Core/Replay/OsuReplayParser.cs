// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using ManiaReplayMaster.Core.IO;

namespace ManiaReplayMaster.Core.Replay;

/// <summary>Port of <c>me.replays.Mode</c>.</summary>
public enum OsuMode
{
    Osu = 0,
    Taiko = 1,
    CatchTheBeat = 2,
    OsuMania = 3
}

/// <summary>
/// Port of <c>me.replays.Replay</c>: the <c>.osr</c> header.
/// </summary>
public sealed class OsuReplay
{
    public OsuMode Mode;
    public int FileVersion;
    public int Mods;
    public long OnlineScoreId;
    public int Hit300;
    public int Hit100;
    public int Hit50;
    public int Beat300;
    public int Beat100;
    public int Misses;
    public int MaxCombo;
    public int Points;
    public string? BeatmapHash;
    public string? Username;
    public string? ReplayHash;
    public string? Diagram;
    public long TimestampTicks;
    public byte[]? CompressedData;
    public bool Perfect;

    /// <summary>True when bit 29 is set (ScoreV2).</summary>
    public bool HasScoreV2 => (Mods & (1 << 29)) != 0;

    /// <summary>True when bit 30 is set (Mirror).</summary>
    public bool HasMirror => (Mods & (1 << 30)) != 0;
}

/// <summary>One entry of the decoded replay event stream (<c>w|x|y|z</c>).</summary>
public readonly struct OsuReplayAction
{
    /// <summary>Time delta in ms relative to the previous action.</summary>
    public readonly long W;

    /// <summary>For mania: bitmask of pressed columns. For osu!std: cursor x.</summary>
    public readonly float X;

    public readonly float Y;

    /// <summary>For taiko: the hit drum.</summary>
    public readonly int Z;

    public OsuReplayAction(long w, float x, float y, int z)
    {
        W = w;
        X = x;
        Y = y;
        Z = z;
    }
}

/// <summary>
/// Port of <c>me.replays.parse.ReplayReader</c> plus the event-stream decoding that the
/// original delegated to <c>me.replays.ReplayData.parse()</c>.
/// </summary>
public static class OsuReplayParser
{
    public static OsuReplay Parse(string path)
    {
        using var reader = OsuBinaryReader.OpenFile(path);
        return Parse(reader);
    }

    public static OsuReplay Parse(OsuBinaryReader reader)
    {
        var replay = ParseHeader(reader);
        replay.CompressedData = reader.ReadBytes();
        replay.OnlineScoreId = reader.ReadInt64();
        return replay;
    }

    /// <summary>Reads through the timestamp, leaving the compressed payload unread.</summary>
    public static OsuReplay ParseHeader(OsuBinaryReader reader)
    {
        var replay = new OsuReplay
        {
            Mode = (OsuMode)reader.ReadByte(),
            FileVersion = reader.ReadInt32(),
            BeatmapHash = reader.ReadLine(),
            Username = reader.ReadLine(),
            ReplayHash = reader.ReadLine(),
            Hit300 = reader.ReadUInt16(),
            Hit100 = reader.ReadUInt16(),
            Hit50 = reader.ReadUInt16(),
            Beat300 = reader.ReadUInt16(),
            Beat100 = reader.ReadUInt16(),
            Misses = reader.ReadUInt16(),
            Points = reader.ReadInt32(),
            MaxCombo = reader.ReadUInt16(),
            Perfect = reader.ReadBoolean(),
            Mods = reader.ReadInt32(),
            Diagram = reader.ReadLine(),
            TimestampTicks = reader.ReadInt64()
        };

        return replay;
    }

    /// <summary>
    /// Decompresses the replay payload.
    ///
    /// The payload is an LZMA <c>FORMAT_ALONE</c> stream: 5 properties bytes (lc/lp/pb packed),
    /// then an 8-byte little-endian uncompressed size, then the range-coded data. This is exactly
    /// what <c>org.tukaani.xz.LZMAInputStream</c> consumed in the reference implementation, and
    /// what <see cref="SharpCompress.Compressors.LZMA.LzmaStream"/>'s legacy constructor expects.
    /// </summary>
    public static byte[] Decompress(OsuReplay replay)
    {
        var data = replay.CompressedData;
        if (data is null || data.Length == 0)
        {
            return Array.Empty<byte>();
        }

        if (data.Length < 13)
        {
            throw new InvalidDataException($"Replay payload too short for LZMA ({data.Length} bytes).");
        }

        var uncompressedSize = BitConverter.ToInt64(data, 5);
        var properties = new byte[5];
        Array.Copy(data, 0, properties, 0, 5);

        using var payload = new MemoryStream(data, 13, data.Length - 13, writable: false);
        using var stream = new SharpCompress.Compressors.LZMA.LzmaStream(
            properties, payload, payload.Length, uncompressedSize);

        using var output = new MemoryStream(uncompressedSize > 0 && uncompressedSize < int.MaxValue
            ? (int)uncompressedSize
            : 0);
        stream.CopyTo(output);

        if (uncompressedSize > 0 && output.Length != uncompressedSize)
        {
            throw new InvalidDataException(
                $"LZMA stream produced {output.Length} bytes, expected {uncompressedSize}.");
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decodes the raw event stream. Format:
    /// <c>w|x|y|z,w|x|y|z,...</c> where <c>w</c> is a relative time delta.
    /// A trailing entry of <c>-12345|0|0|...</c> is the end marker.
    /// </summary>
    public static List<OsuReplayAction> ParseActions(byte[] raw)
    {
        var actions = new List<OsuReplayAction>(raw.Length / 12 + 4);
        var span = System.Text.Encoding.UTF8.GetString(raw).AsSpan();
        var start = 0;
        while (start < span.Length)
        {
            var end = span.Slice(start).IndexOf(',');
            var chunk = end < 0 ? span.Slice(start) : span.Slice(start, end);
            start = end < 0 ? span.Length : start + end + 1;

            if (chunk.Length == 0)
            {
                continue;
            }

            // w|x|y|z
            var p1 = chunk.IndexOf('|');
            if (p1 < 0)
            {
                continue;
            }

            var p2 = chunk.Slice(p1 + 1).IndexOf('|');
            if (p2 < 0)
            {
                continue;
            }

            p2 += p1 + 1;
            var p3 = chunk.Slice(p2 + 1).IndexOf('|');
            if (p3 < 0)
            {
                continue;
            }

            p3 += p2 + 1;

            if (!long.TryParse(chunk.Slice(0, p1), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var w))
            {
                continue;
            }

            if (!float.TryParse(chunk.Slice(p1 + 1, p2 - p1 - 1), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x))
            {
                continue;
            }

            if (!float.TryParse(chunk.Slice(p2 + 1, p3 - p2 - 1), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y))
            {
                continue;
            }

            if (!int.TryParse(chunk.Slice(p3 + 1), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var z))
            {
                continue;
            }

            actions.Add(new OsuReplayAction(w, x, y, z));
        }

        return actions;
    }
}

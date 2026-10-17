namespace ManiaReplayMaster.Core.IO;

/// <summary>
/// Little-endian binary reader mirroring the reference implementation's
/// <c>OsuBinaryInputStream</c>, including its specific string encoding
/// (7-bit-encoded length prefix + raw bytes).
/// </summary>
public sealed class OsuBinaryReader : IDisposable
{
    private readonly Stream _stream;

    public OsuBinaryReader(Stream stream)
    {
        _stream = stream;
    }

    public static OsuBinaryReader OpenFile(string path)
    {
        return new OsuBinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
    }

    public long Position => _stream.Position;

    public long Length => _stream.Length;

    private void Fill(byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = _stream.Read(buffer, read, count - read);
            if (n <= 0)
            {
                throw new EndOfStreamException($"Unexpected end of stream (wanted {count} bytes, got {read}).");
            }

            read += n;
        }
    }

    private readonly byte[] _scratch = new byte[8];

    public byte ReadByte()
    {
        var b = _stream.ReadByte();
        if (b < 0)
        {
            throw new EndOfStreamException("Unexpected end of stream.");
        }

        return (byte)b;
    }

    public bool ReadBoolean() => ReadByte() != 0;

    public short ReadInt16()
    {
        Fill(_scratch, 2);
        return BitConverter.ToInt16(_scratch, 0);
    }

    public ushort ReadUInt16()
    {
        Fill(_scratch, 2);
        return BitConverter.ToUInt16(_scratch, 0);
    }

    public int ReadInt32()
    {
        Fill(_scratch, 4);
        return BitConverter.ToInt32(_scratch, 0);
    }

    public long ReadInt64()
    {
        Fill(_scratch, 8);
        return BitConverter.ToInt64(_scratch, 0);
    }

    /// <summary>
    /// Mirrors <c>getStringLength()</c>: the length is stored in characters, equal to the
    /// number of bytes because every <c>.osr</c> string is ASCII.
    /// </summary>
    private int ReadStringLength()
    {
        var result = 0;
        var shift = 0;
        while (true)
        {
            var b = ReadByte();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return result;
    }

    /// <summary>
    /// Mirrors <c>getLine()</c>.
    ///
    /// EMPIRICALLY VERIFIED LAYOUT (all 4 fixtures in <c>fixtures/</c>): each header string is
    /// encoded as one lead byte (observed as <c>0x0B</c>, the stored string length of the
    /// zero-padded key in the original writer) followed by a 7-bit-encoded length and then that
    /// many UTF-8 bytes. Decoding the lead byte as part of the 7-bit length yields an 11-character
    /// string on every fixture, which does not reproduce the reference implementation's output;
    /// skipping the lead byte reproduces it exactly (md5/player/replay-md5 and a total header
    /// offset of 75/79/86 bytes depending on the score's player name).
    ///
    /// A single zero lead byte still means "no string" (matches the original's null return).
    /// </summary>
    public string? ReadLine()
    {
        if (_stream.Position >= _stream.Length)
        {
            return null;
        }

        var lead = ReadByte();
        if (lead == 0)
        {
            return null;
        }

        var length = ReadStringLength();
        if (length == 0)
        {
            return string.Empty;
        }

        if (length < 0 || length > 1 << 20)
        {
            throw new InvalidDataException($"Unreasonable .osr string length: {length}.");
        }

        var buffer = new byte[length];
        Fill(buffer, length);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }

    /// <summary>Mirrors <c>getBytes()</c>: int32 count followed by that many bytes; null when negative.</summary>
    public byte[]? ReadBytes()
    {
        var count = ReadInt32();
        if (count < 0)
        {
            return null;
        }

        if (count == 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[count];
        Fill(buffer, count);
        return buffer;
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>Reads up to <paramref name="count"/> bytes from the current position.</summary>
    public byte[] ReadRemaining(int count)
    {
        var available = (int)Math.Max(0, Math.Min(count, _stream.Length - _stream.Position));
        var buffer = new byte[available];
        if (available > 0)
        {
            Fill(buffer, available);
        }

        return buffer;
    }
}

using System.Text;

namespace ManiaReplayMaster.Game;

/// <summary>
/// Read-only stream wrapper that counts the bytes actually consumed by the reader above it.
///
/// <c>FileStream.Position</c> cannot be used for this: a <c>BufferedStream</c> in between makes the
/// file position jump ahead of the logical read position (it reports 65536 after the first small read),
/// which silently corrupts both progress reporting and the "did the walk consume the file exactly"
/// check. This is used by <see cref="BeatmapDatabaseReader"/> for exactly that reason.
/// </summary>
internal sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private long _read;

    public CountingStream(Stream inner)
    {
        _inner = inner;
    }

    /// <summary>Number of bytes handed out by the read methods so far.</summary>
    public long BytesRead => _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        _read += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        _read += read;
        return read;
    }

    public override int ReadByte()
    {
        var value = _inner.ReadByte();
        if (value >= 0)
        {
            _read++;
        }

        return value;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Buffer-backed legacy reader used for the in-memory parse paths (<c>scores.db</c> is 784 KB,
/// <c>osu!.db</c> is 26 MB and is streamed instead). Kept separate from
/// <c>ManiaReplayMaster.Core.IO.OsuBinaryReader</c> — which is owned by another teammate and is
/// <c>.osr</c>-specific — so that the two can never drift into each other's semantics.
/// </summary>

internal sealed class BufferReader
{
    private readonly byte[] _buffer;
    private readonly int _end;
    private int _position;

    public BufferReader(byte[] buffer, int offset = 0, int? count = null)
    {
        _buffer = buffer;
        _position = offset;
        _end = offset + (count ?? (buffer.Length - offset));
    }

    public int Position => _position;

    public int Remaining => _end - _position;

    public bool AtEnd => _position >= _end;

    public byte ReadByte()
    {
        if (_position >= _end)
        {
            throw new EndOfStreamException($"Unexpected end of buffer at offset {_position} (length {_end}).");
        }

        return _buffer[_position++];
    }

    public bool ReadBoolean() => ReadByte() != 0;

    public ushort ReadUInt16()
    {
        Need(2);
        var value = (ushort)(_buffer[_position] | (_buffer[_position + 1] << 8));
        _position += 2;
        return value;
    }

    public short ReadInt16() => unchecked((short)ReadUInt16());

    public int ReadInt32()
    {
        Need(4);
        var value = _buffer[_position]
                    | (_buffer[_position + 1] << 8)
                    | (_buffer[_position + 2] << 16)
                    | (_buffer[_position + 3] << 24);
        _position += 4;
        return value;
    }

    /// <summary>
    /// 64-bit little-endian read. Both halves are widened as <b>unsigned</b> before being combined:
    /// assembling <c>(long)ReadInt32(o) | ((long)ReadInt32(o + 4) &lt;&lt; 32)</c> sign-extends the low
    /// half and silently corrupts every <c>dateTicks</c> whose low 32 bits have bit 31 set (that bug
    /// produced 52.5% wrong value-to-file matches before it was caught by the harness).
    /// </summary>
    public long ReadInt64()
    {
        Need(8);
        var value = (long)((uint)(_buffer[_position]
                                  | (_buffer[_position + 1] << 8)
                                  | (_buffer[_position + 2] << 16)
                                  | (_buffer[_position + 3] << 24))
                            | ((ulong)(uint)(_buffer[_position + 4]
                                             | (_buffer[_position + 5] << 8)
                                             | (_buffer[_position + 6] << 16)
                                             | (_buffer[_position + 7] << 24)) << 32));
        _position += 8;
        return value;
    }

    public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());

    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadInt64());

    /// <summary>Single byte, no bounds error — used for defensive trailing-byte inspection.</summary>
    public int ReadByteOrMinusOne() => _position >= _end ? -1 : _buffer[_position++];

    /// <summary>The documented osu! "legacy string": marker byte, then LEB128 length, then UTF-8.</summary>
    public string? ReadLegacyString()
    {
        if (ReadByte() == 0)
        {
            return null;
        }

        var length = Read7BitEncodedInt();
        if (length == 0)
        {
            return string.Empty;
        }

        if (length < 0 || length > _end - _position)
        {
            throw new InvalidDataException(
                $"Legacy string length {length} exceeds the remaining {_end - _position} bytes at offset {_position - 1}.");
        }

        var value = Encoding.UTF8.GetString(_buffer, _position, length);
        _position += length;
        return value;
    }

    public int Read7BitEncodedInt()
    {
        var result = 0;
        var shift = 0;
        while (true)
        {
            if (shift > 28)
            {
                throw new InvalidDataException("Malformed 7-bit encoded integer (more than 5 bytes).");
            }

            var b = ReadByte();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }

    public void Skip(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Need(count);
        _position += count;
    }

    /// <summary>Bytes left after the documented walk — must be 0 for a trustworthy parse.</summary>
    public byte[] RemainingBytes() => _buffer.AsSpan(_position, _end - _position).ToArray();

    private void Need(int count)
    {
        if (_end - _position < count)
        {
            throw new EndOfStreamException(
                $"Unexpected end of buffer at offset {_position}: needed {count}, only {_end - _position} left.");
        }
    }
}

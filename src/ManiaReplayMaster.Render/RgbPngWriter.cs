using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ManiaReplayMaster.Render;

/// <summary>
/// Lossless PNG writer for the renderer's opaque 24-bit canvas. A fixed Up filter and fast zlib
/// compression favour temporary slice throughput over GDI+'s general-purpose PNG encoding.
/// Row and compressed buffers are reused across slices; instances are not thread-safe.
/// </summary>
internal sealed class RgbPngWriter(int width) : IDisposable
{
    private readonly byte[] _bgr = new byte[checked(width * 3)];
    private byte[] _current = new byte[checked(width * 3)];
    private byte[] _previous = new byte[checked(width * 3)];
    private readonly byte[] _filtered = new byte[checked(width * 3 + 1)];
    private readonly MemoryStream _compressed = new();
    private static readonly uint[] CrcTable = CreateCrcTable();

    public void Write(Bitmap bitmap, string path)
    {
        var pixels = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            _compressed.SetLength(0);
            Array.Clear(_previous);
            _filtered[0] = 2; // PNG Up: each byte is the difference from the row above.
            using var zlib = new ZLibStream(_compressed, CompressionLevel.Fastest, leaveOpen: true);
            for (var y = 0; y < bitmap.Height; y++)
            {
                // GDI stores BGR with aligned rows; PNG needs RGB without stride padding.
                Marshal.Copy(pixels.Scan0 + y * pixels.Stride, _bgr, 0, _bgr.Length);
                for (var x = 0; x < _bgr.Length; x += 3)
                {
                    _current[x] = _bgr[x + 2];
                    _current[x + 1] = _bgr[x + 1];
                    _current[x + 2] = _bgr[x];
                }

                var offset = 0;
                for (; offset <= _current.Length - Vector<byte>.Count; offset += Vector<byte>.Count)
                    (new Vector<byte>(_current, offset) - new Vector<byte>(_previous, offset))
                        .CopyTo(_filtered, offset + 1);
                for (; offset < _current.Length; offset++)
                    _filtered[offset + 1] = unchecked((byte)(_current[offset] - _previous[offset]));

                zlib.Write(_filtered);
                (_current, _previous) = (_previous, _current);
            }
        }
        finally
        {
            bitmap.UnlockBits(pixels);
        }

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, bitmap.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], bitmap.Height);
        header[8] = 8; // 8-bit channels
        header[9] = 2; // Truecolour RGB, no alpha
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, _compressed.GetBuffer().AsSpan(0, checked((int)_compressed.Length)));
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(integer, data.Length);
        output.Write(integer);
        output.Write(type);
        output.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in type) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
        output.Write(integer);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (var i = 0; i < table.Length; i++)
        {
            var crc = (uint)i;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    public void Dispose() => _compressed.Dispose();
}

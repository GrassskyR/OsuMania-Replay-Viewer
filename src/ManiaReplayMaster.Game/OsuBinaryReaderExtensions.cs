using ManiaReplayMaster.Core.IO;

namespace ManiaReplayMaster.Game;

/// <summary>
/// Adds the two primitives the game-link layer needs on top of
/// <c>ManiaReplayMaster.Core.IO.OsuBinaryReader</c> (owned by another teammate — never modified here):
/// a settable position so array sections can be skipped without materialising them, and a raw
/// UTF-8 string reader for the LEB128-prefixed <c>.osr</c> header strings.
///
/// The class exists so that <see cref="OsuReplayHeaderReader"/> can share the reference byte
/// semantics (including the <c>0x0B</c> lead byte / 7-bit length convention) instead of
/// reimplementing them — a second implementation would be a second place to get it wrong.
/// </summary>
internal static class OsuBinaryReaderExtensions
{
    /// <summary>Position inside the underlying stream (the header offset proofs in Core rely on this).</summary>
    public static long GetPosition(this OsuBinaryReader reader) => reader.Position;

    /// <summary>
    /// Skips <paramref name="count"/> bytes while keeping the stream position exact. The data is
    /// consumed (not seeked) because the underlying file is opened with <c>FileShare.ReadWrite</c>
    /// and re-seeking a buffered stream mid-file buys nothing here.
    /// </summary>
    public static void Skip(this OsuBinaryReader reader, long count)
    {
        var remaining = count;
        while (remaining > 0)
        {
            var take = (int)Math.Min(int.MaxValue, Math.Min(remaining, 1 << 20));
            var read = reader.ReadRemaining(take);
            if (read.Length == 0)
            {
                throw new EndOfStreamException($"Unexpected end of stream while skipping {count} bytes.");
            }

            remaining -= read.Length;
        }
    }

    /// <summary>
    /// Reads a <c>.osr</c> header string. Core's <c>ReadLine()</c> is documented as "lead byte, then
    /// 7-bit length, then that many UTF-8 bytes" and is empirically verified against all four
    /// fixtures, so it is used verbatim; this alias just names the intent at the call site.
    /// </summary>
    public static string? ReadHeaderString(this OsuBinaryReader reader) => reader.ReadLine();
}

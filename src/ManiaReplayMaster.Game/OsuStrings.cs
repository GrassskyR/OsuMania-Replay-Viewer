namespace ManiaReplayMaster.Game;

/// <summary>
/// The text primitives shared by every osu!stable binary format (<c>.osr</c>, <c>osu!.db</c>,
/// <c>scores.db</c>, <c>collection.db</c>), reproduced one-to-one from
/// <c>docs/SCORESDB_FORMAT.md</c> §1 and <c>docs/BEATMAPDB_FORMAT.md</c> §0.1.
///
/// Encoding: a single marker byte (<c>0x00</c> = null, anything else = string follows), then a
/// 7-bit-encoded (LEB128) byte length, then that many UTF-8 bytes. <c>Encoding.UTF8</c> uses
/// replacement fallback, which matches osu!'s own behaviour for the handful of names in
/// <c>osu!.db</c> that contain invalid UTF-8; a throwing decoder must never be used here.
/// </summary>
public static class OsuStrings
{
    /// <summary>Reads one legacy string. Returns <c>null</c> only for the <c>0x00</c> marker.</summary>
    public static string? Read(BinaryReader reader)
    {
        if (reader.ReadByte() == 0)
        {
            return null;
        }

        return reader.ReadString();
    }

    /// <summary>Reads one legacy string, substituting <paramref name="whenNull"/> for a null marker.</summary>
    public static string Read(BinaryReader reader, string whenNull)
        => Read(reader) ?? whenNull;

    /// <summary>True for the 32 lowercase hex characters every osu! MD5 field uses.</summary>
    public static bool LooksLikeMd5(string? value)
    {
        if (value is null || value.Length != 32)
        {
            return false;
        }

        foreach (var c in value)
        {
            var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex)
            {
                return false;
            }
        }

        return true;
    }
}

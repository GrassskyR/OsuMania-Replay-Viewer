using System.Diagnostics;

namespace ManiaReplayMaster.Game;

/// <summary>
/// Reader for <c>scores.db</c> — the local "score → replay file" index that the whole automatic
/// resolution feature rests on (plan §3.3 level 1).
///
/// The format is versioned and officially undocumented; the layout implemented here is the one
/// measured and validated in <c>docs/SCORESDB_FORMAT.md</c> (5,420/5,420 records consumed the file
/// exactly and matched the corresponding <c>.osr</c> header on all 18 fields). Because a silently
/// wrong parse is worse than a visible failure, the reader:
///
/// * verifies that the documented walk consumes the file to its last byte
///   (<see cref="ScoreDatabaseOptions.RequireExactByteConsumption"/>),
/// * counts records whose embedded beatmap MD5 disagrees with the enclosing group,
/// * returns a result object carrying the counters plus a <c>Warning</c>/<c>Error</c> instead of
///   throwing into the UI path.
///
/// The file is opened read-only with <see cref="FileShare.ReadWrite"/>: osu! rewrites
/// <c>scores.db</c> after every play, so a torn read is possible and must never lock the game out.
/// </summary>
public static class ScoreDatabaseReader
{
    /// <summary>
    /// Minimum client build whose records carry the trailing <c>int64</c> online id
    /// (<c>LegacyScoreDecoder</c> gate <c>20140721</c>). Older records use <c>int32</c> or nothing;
    /// no such record exists on the reference machine, so that branch is defensive only.
    /// </summary>
    public const int OnlineIdInt64Version = 20140721;

    /// <summary>Minimum client build that has an online id at all (<c>int32</c> form).</summary>
    public const int OnlineIdInt32Version = 20121008;

    /// <summary>Builds whose file could not be parsed; never throws (a torn file is a normal condition).</summary>
    public static bool TryRead(
        string path,
        out ScoreDatabaseResult result,
        ScoreDatabaseOptions? options = null)
    {
        try
        {
            result = Read(path, options);
            return result.Success;
        }
        catch (Exception ex)
        {
            result = new ScoreDatabaseResult
            {
                Path = path,
                FileLength = SafeLength(path),
                Success = false,
                Error = $"{ex.GetType().Name}: {ex.Message}"
            };
            return false;
        }
    }

    /// <summary>Throws <see cref="ScoreDatabaseFormatException"/> when the file does not match the spec.</summary>
    public static ScoreDatabaseResult Read(string path, ScoreDatabaseOptions? options = null)
    {
        options ??= ScoreDatabaseOptions.Default;
        var stopwatch = Stopwatch.StartNew();

        byte[] bytes;
        long length;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            length = stream.Length;
            if (length <= 0)
            {
                throw new ScoreDatabaseFormatException($"'{path}' is empty.");
            }

            if (length > int.MaxValue)
            {
                throw new ScoreDatabaseFormatException($"'{path}' is implausibly large ({length} bytes).");
            }

            bytes = new byte[length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = stream.Read(bytes, read, bytes.Length - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            if (read != bytes.Length)
            {
                throw new ScoreDatabaseFormatException(
                    $"'{path}' was truncated while reading: got {read} of {length} bytes (the game may be rewriting it).");
            }
        }

        var reader = new BufferReader(bytes);
        var warnings = new List<string>();
        var groups = new Dictionary<string, List<ScoreRecord>>(StringComparer.OrdinalIgnoreCase);
        var version = reader.ReadInt32();
        var declaredGroups = reader.ReadInt32();

        if (declaredGroups < 0 || declaredGroups > 10_000_000)
        {
            throw new ScoreDatabaseFormatException(
                $"Group count {declaredGroups} at offset 4 is implausible; this does not look like scores.db (version {version}).");
        }

        var parsedGroups = 0;
        var records = 0;
        var emptyGroups = 0;
        var duplicates = 0;
        var malformedGroups = 0;
        var recordMd5Mismatch = 0;
        var oldVersionRecords = 0;
        var minVersion = int.MaxValue;
        var maxVersion = int.MinValue;

        try
        {
            for (var g = 0; g < declaredGroups; g++)
            {
                var md5 = reader.ReadLegacyString();
                var scoreCount = reader.ReadInt32();
                if (scoreCount < 0 || scoreCount > 100_000)
                {
                    throw new ScoreDatabaseFormatException(
                        $"Group {g} declares {scoreCount} records at offset {reader.Position - 4}; the file is not in the documented layout.");
                }

                var key = md5 ?? string.Empty;
                if (md5 is null)
                {
                    warnings.Add($"Group {g} has a null beatmap MD5.");
                }
                else if (!OsuStrings.LooksLikeMd5(md5))
                {
                    malformedGroups++;
                    warnings.Add($"Group {g} has a non-MD5 key '{Truncate(md5)}'.");
                }

                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<ScoreRecord>(scoreCount);
                    groups[key] = list;
                }
                else
                {
                    duplicates++;
                    warnings.Add($"Group MD5 '{key}' appears more than once (group {g}).");
                }

                if (scoreCount == 0)
                {
                    emptyGroups++;
                }

                for (var i = 0; i < scoreCount; i++)
                {
                    var offset = reader.Position;
                    var record = ReadRecord(reader, key);
                    list.Add(record with { FileOffset = offset });
                    records++;

                    if (record.Version < minVersion)
                    {
                        minVersion = record.Version;
                    }

                    if (record.Version > maxVersion)
                    {
                        maxVersion = record.Version;
                    }

                    if (record.Version < OnlineIdInt64Version)
                    {
                        oldVersionRecords++;
                    }

                    if (options.DetectRecordAnomalies
                        && !string.Equals(record.BeatmapMd5, key, StringComparison.OrdinalIgnoreCase))
                    {
                        recordMd5Mismatch++;
                        if (recordMd5Mismatch <= 5)
                        {
                            warnings.Add(
                                $"Record {i} of group {g} has beatmap MD5 '{Truncate(record.BeatmapMd5)}' but the group key is '{key}'.");
                        }
                    }
                }

                parsedGroups++;
            }
        }
        catch (ScoreDatabaseFormatException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ScoreDatabaseFormatException(
                $"scores.db parse aborted after {parsedGroups}/{declaredGroups} groups and {records} records " +
                $"at offset {reader.Position}: {ex.GetType().Name}: {ex.Message}",
                ex);
        }

        var consumed = reader.Position;
        var trailing = bytes.Length - consumed;
        var exact = trailing == 0;

        if (!exact && trailing <= options.AllowedTrailingBytes && options.AllowedTrailingBytes > 0)
        {
            warnings.Add($"{trailing} trailing byte(s) ignored (allowed by options).");
            exact = true;
        }

        if (!exact)
        {
            var hex = Convert.ToHexString(bytes.AsSpan(consumed, Math.Min(16, trailing)));
            var message =
                $"scores.db byte consumption mismatch: parsed {consumed} of {bytes.Length} bytes " +
                $"({trailing} bytes left after {parsedGroups} groups / {records} records; first trailing bytes {hex}). " +
                "The layout assumptions in docs/SCORESDB_FORMAT.md do not hold for this file " +
                "(newer osu! build?); callers must fall back to the level-3 header scan instead of trusting this list.";
            if (options.RequireExactByteConsumption)
            {
                throw new ScoreDatabaseFormatException(message);
            }

            warnings.Add(message);
        }

        stopwatch.Stop();

        return new ScoreDatabaseResult
        {
            Path = path,
            FileLength = length,
            Version = version,
            DeclaredGroupCount = declaredGroups,
            ParsedGroupCount = parsedGroups,
            ScoreRecordCount = records,
            EmptyGroupCount = emptyGroups,
            BytesConsumed = consumed,
            TrailingByteCount = Math.Max(0, trailing),
            DuplicateGroupMd5Count = duplicates,
            MalformedGroupMd5Count = malformedGroups,
            RecordMd5MismatchCount = recordMd5Mismatch,
            RecordVersionBelowOnlineIdGateCount = oldVersionRecords,
            MinimumRecordVersion = minVersion == int.MaxValue ? 0 : minVersion,
            MaximumRecordVersion = maxVersion == int.MinValue ? 0 : maxVersion,
            ParseDuration = stopwatch.Elapsed,
            Groups = groups,
            Warnings = warnings,
            Success = exact
        };
    }

    /// <summary>
    /// Parses a single score record. The field order is fixed and unpadded; see
    /// <c>docs/SCORESDB_FORMAT.md</c> §3.
    /// </summary>
    private static ScoreRecord ReadRecord(BufferReader reader, string groupMd5)
    {
        var mode = (GameMode)reader.ReadByte();
        var version = reader.ReadInt32();
        var beatmapMd5 = reader.ReadLegacyString();
        var player = reader.ReadLegacyString();
        var replayMd5 = reader.ReadLegacyString();
        var count300 = reader.ReadUInt16();
        var count100 = reader.ReadUInt16();
        var count50 = reader.ReadUInt16();
        var countGeki = reader.ReadUInt16();
        var countKatu = reader.ReadUInt16();
        var countMiss = reader.ReadUInt16();
        var totalScore = reader.ReadInt32();
        var maxCombo = reader.ReadUInt16();
        var perfect = reader.ReadBoolean();
        var mods = reader.ReadInt32();
        var hpGraph = reader.ReadLegacyString();
        var dateTicks = reader.ReadInt64();
        var replayLength = reader.ReadInt32();

        // Never observed non-negative on the reference file, but the format allows an embedded
        // payload; skipping it keeps the record boundary correct if a build ever writes one.
        if (replayLength > 0)
        {
            reader.Skip(replayLength);
        }

        long onlineId = 0;
        if (version >= OnlineIdInt64Version)
        {
            onlineId = reader.ReadInt64();
        }
        else if (version >= OnlineIdInt32Version)
        {
            onlineId = reader.ReadInt32();
        }

        return new ScoreRecord
        {
            Mode = mode,
            Version = version,
            BeatmapMd5 = beatmapMd5 ?? groupMd5,
            PlayerName = player,
            ReplayMd5 = replayMd5,
            Count300 = count300,
            Count100 = count100,
            Count50 = count50,
            CountGeki = countGeki,
            CountKatu = countKatu,
            CountMiss = countMiss,
            TotalScore = totalScore,
            MaxCombo = maxCombo,
            Perfect = perfect,
            Mods = mods,
            HpGraph = hpGraph,
            DateTicks = dateTicks,
            ReplayLength = replayLength,
            OnlineId = onlineId
        };
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string Truncate(string? value)
        => value is null ? "(null)" : value.Length <= 40 ? value : value[..40] + "…";
}

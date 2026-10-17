using System.Diagnostics;
using System.Text;

namespace ManiaReplayMaster.Game;

/// <summary>
/// Streams <c>osu!.db</c> into an MD5 鈫?(folder, <c>.osu</c> file, ids, mode) index.
///
/// Layout and the three version-gated details come from <c>docs/FORMATS.md</c>, which was
/// measured on the real 26 MB file (22,123 records, exact byte consumption, single trailing
/// <c>int32</c>). The walk here covers every field of every record 鈥?including the timing-point and
/// star-rating arrays, which together are about half the file 鈥?because the record length is
/// variable and skipping by assumption is exactly the kind of shortcut that silently desynchronises
/// on the next osu! build.
///
/// The file is opened read-only with <see cref="FileShare.ReadWrite"/>; osu! rewrites <c>osu!.db</c>
/// on exit, so the game must never be blocked out. A truncated file surfaces as a clear
/// <see cref="BeatmapDatabaseFormatException"/> rather than a partial index.
/// </summary>
public static class BeatmapDatabaseReader
{
    /// <summary>Build that switched AR/CS/HP/OD from 4 bytes to 4 floats (defensive branch, inferred).</summary>
    public const int FloatDifficultyVersion = 20140609;

    private const int ProgressInterval = 1000;

    public static bool TryRead(
        string path,
        string? songsRoot,
        out BeatmapDatabaseResult result,
        BeatmapDatabaseOptions? options = null)
    {
        try
        {
            result = Read(path, songsRoot, options);
            return result.Success;
        }
        catch (Exception ex)
        {
            result = new BeatmapDatabaseResult
            {
                Path = path,
                FileLength = SafeLength(path),
                Success = false,
                Error = $"{ex.GetType().Name}: {ex.Message}"
            };
            return false;
        }
    }

    /// <summary>Throws <see cref="BeatmapDatabaseFormatException"/> on any deviation from the spec.</summary>
    public static BeatmapDatabaseResult Read(
        string path,
        string? songsRoot,
        BeatmapDatabaseOptions? options = null)
    {
        options ??= BeatmapDatabaseOptions.Default;
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();

        using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1 << 16, FileOptions.SequentialScan);
        var length = file.Length;
        // The counting stream is what makes `consumed` trustworthy: FileStream.Position reports the
        // buffer's position, not the reader's (it jumps to 65536 after the first record).
        using var counting = new CountingStream(new BufferedStream(file, 1 << 16));
        var reader = new BinaryReader(counting, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var version = reader.ReadInt32();
        var folderCount = reader.ReadInt32();
        var accountUnlocked = reader.ReadByte() != 0;
        var unlockTicks = reader.ReadInt64();
        var player = OsuStrings.Read(reader);
        var beatmapCount = reader.ReadInt32();

        if (folderCount < 0 || folderCount > 10_000_000)
        {
            throw new BeatmapDatabaseFormatException(
                $"Folder count {folderCount} at offset 4 is implausible; this does not look like osu!.db.");
        }

        if (beatmapCount < 0 || beatmapCount > 2_000_000)
        {
            throw new BeatmapDatabaseFormatException(
                $"Beatmap count {beatmapCount} is implausible (header offset 17+); this does not look like osu!.db.");
        }

        var byMd5 = new Dictionary<string, BeatmapIndexEntry>(Math.Min(beatmapCount, 100_000), StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<int, BeatmapIndexEntry>();
        var bySet = new Dictionary<int, List<BeatmapIndexEntry>>();
        var histogram = new Dictionary<GameMode, int>();
        var parsed = 0;
        long starPairs = 0;
        long timingPoints = 0;
        var malformedMd5 = 0;
        var missingFile = 0;
        var duplicateMd5 = 0;

        // Differential-testing hook: dumping each record's start/length lets the harness diff this walk
        // against the Python reference record by record. Off unless the environment variable is set.
        // The length is accumulated from the stream position immediately before and after each record.
        var lengthDumpPath = Environment.GetEnvironmentVariable("MRM_BEATMAPDB_LENGTH_DUMP");
        List<string>? lengthDump = lengthDumpPath is null ? null : new List<string>(beatmapCount + 4);

        try
        {
            for (var index = 0; index < beatmapCount; index++)
            {
                var recordStart = counting.BytesRead;
                var entry = ReadRecord(reader, version, songsRoot, ref starPairs, ref timingPoints, out var md5Valid);
                lengthDump?.Add($"{index} {recordStart} {counting.BytesRead - recordStart}");

                if (!md5Valid)
                {
                    malformedMd5++;
                    if (malformedMd5 <= 5)
                    {
                        warnings.Add($"Record {index} has a non-32-hex MD5 '{entry.Md5}'.");
                    }
                }

                if (options.Modes.Count > 0 && !options.Modes.Contains(entry.Mode))
                {
                    parsed++;
                    continue;
                }

                if (entry.AbsolutePath is not null && !File.Exists(entry.AbsolutePath))
                {
                    missingFile++;
                    if (options.ExistingFilesOnly)
                    {
                        parsed++;
                        continue;
                    }
                }

                if (byMd5.TryGetValue(entry.Md5, out var previous))
                {
                    duplicateMd5++;
                    if (duplicateMd5 <= 5)
                    {
                        warnings.Add(
                            $"Duplicate MD5 {entry.Md5}: keeping '{previous.FolderName}\\{previous.OsuFileName}', " +
                            $"dropping '{entry.FolderName}\\{entry.OsuFileName}'.");
                    }
                }
                else
                {
                    byMd5[entry.Md5] = entry;

                    if (entry.BeatmapId > 0)
                    {
                        byId.TryAdd(entry.BeatmapId, entry);
                    }

                    if (entry.BeatmapSetId > 0)
                    {
                        if (!bySet.TryGetValue(entry.BeatmapSetId, out var setList))
                        {
                            setList = new List<BeatmapIndexEntry>();
                            bySet[entry.BeatmapSetId] = setList;
                        }

                        setList.Add(entry);
                    }
                }

                histogram[entry.Mode] = histogram.GetValueOrDefault(entry.Mode) + 1;
                parsed++;

                if (options.Progress is not null && index % ProgressInterval == 0)
                {
                    options.Progress(index);
                }
            }
        }
        catch (BeatmapDatabaseFormatException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new BeatmapDatabaseFormatException(
                $"osu!.db walk aborted after {parsed}/{beatmapCount} records at position {counting.BytesRead}: " +
                $"{ex.GetType().Name}: {ex.Message}",
                ex);
        }

        // The documented trailer is a single int32 that sits *before* end-of-file. Measure the bytes
        // left by the record walk first: asking `consumed + 4 <= length` after the walk would silently
        // accept a walk that had already eaten the trailer, which is exactly the check that needs to
        // stay honest.
        var consumed = counting.BytesRead;
        var remainingAfterRecords = length - consumed;
        long trailer = 0;
        var hasTrailer = false;
        if (remainingAfterRecords >= 4)
        {
            trailer = reader.ReadInt32();
            hasTrailer = true;
            consumed = counting.BytesRead;
        }
        else
        {
            warnings.Add($"No trailing int32 found at {consumed}; the file may be truncated.");
        }

        if (lengthDump is not null && lengthDumpPath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lengthDumpPath)!);
                File.WriteAllLines(lengthDumpPath, lengthDump);
            }
            catch
            {
                // Diagnostics only.
            }
        }

        var trailing = length - consumed;
        var exact = trailing == 0;

        if (!exact)
        {
            var message =
                $"osu!.db byte consumption mismatch: parsed {consumed} of {length} bytes ({trailing} bytes left " +
                $"after {parsed} records, version {version}). The layout in docs/FORMATS.md does not hold " +
                "for this file; the MD5 index would be unreliable and the Songs scan is used instead.";
            if (options.RequireExactByteConsumption)
            {
                throw new BeatmapDatabaseFormatException(message);
            }

            warnings.Add(message);
        }

        if (missingFile > 0)
        {
            warnings.Add(
                $"{missingFile} record(s) point at a Songs path that does not exist on disk " +
                "(stale database or maps deleted by the user).");
        }

        stopwatch.Stop();

        return new BeatmapDatabaseResult
        {
            Path = path,
            FileLength = length,
            Version = version,
            DeclaredFolderCount = folderCount,
            AccountUnlocked = accountUnlocked,
            UnlockTimeTicks = unlockTicks,
            PlayerName = player,
            DeclaredBeatmapCount = beatmapCount,
            ParsedBeatmapCount = parsed,
            BytesConsumed = consumed,
            TrailerValue = (int)trailer,
            HasTrailer = hasTrailer,
            TrailingByteCount = (int)Math.Max(0, trailing),
            StarRatingPairTotal = starPairs,
            TimingPointTotal = timingPoints,
            ByMd5 = byMd5,
            ByBeatmapId = byId,
            ByBeatmapSetId = bySet.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<BeatmapIndexEntry>)kv.Value),
            ParseDuration = stopwatch.Elapsed,
            Warnings = warnings,
            ModeHistogram = histogram,
            Success = exact
        };
    }

    /// <summary>
    /// Walks one record. Field order is the verified table in <c>docs/FORMATS.md</c> 搂3;
    /// the last <c>int32</c> + <c>byte</c> are opaque and deliberately unused.
    /// </summary>
    private static BeatmapIndexEntry ReadRecord(
        BinaryReader r,
        int version,
        string? songsRoot,
        ref long starPairs,
        ref long timingPoints,
        out bool md5Valid)
    {
        var artist = OsuStrings.Read(r);
        var artistUnicode = OsuStrings.Read(r);
        var title = OsuStrings.Read(r);
        var titleUnicode = OsuStrings.Read(r);
        var creator = OsuStrings.Read(r);
        var difficulty = OsuStrings.Read(r);
        var audio = OsuStrings.Read(r);
        var md5 = OsuStrings.Read(r) ?? string.Empty;
        var osuFile = OsuStrings.Read(r) ?? string.Empty;
        r.ReadByte(); // rankedStatus
        r.ReadUInt16(); // hit circles
        r.ReadUInt16(); // sliders / mania hold notes
        r.ReadUInt16(); // spinners
        r.ReadInt64(); // lastModifiedTicks (UTC)

        float ar = 0, cs = 0, hp = 0, od = 0;
        if (version < FloatDifficultyVersion)
        {
            // [INFERRED, not exercised by the reference file] older builds store 4 single bytes here.
            ar = r.ReadByte();
            cs = r.ReadByte();
            hp = r.ReadByte();
            od = r.ReadByte();
        }
        else
        {
            ar = r.ReadSingle();
            cs = r.ReadSingle();
            hp = r.ReadSingle();
            od = r.ReadSingle();
        }

        var sliderVelocity = r.ReadDouble();

        var pairs = 0;
        if (version >= FloatDifficultyVersion)
        {
            for (var slot = 0; slot < 4; slot++)
            {
                var count = r.ReadInt32();
                if (count < 0 || count > 1_000_000)
                {
                    throw new BeatmapDatabaseFormatException(
                        $"Implausible star-rating pair count {count} in ruleset slot {slot}.");
                }

                for (var k = 0; k < count; k++)
                {
                    var keyTag = r.ReadByte();
                    if (keyTag != 0x08)
                    {
                        // The game itself dispatches on the tag; do the same instead of assuming int32.
                        throw new BeatmapDatabaseFormatException(
                            $"Unexpected star-rating key tag 0x{keyTag:X2} (expected 0x08 Int32) in slot {slot}.");
                    }

                    r.ReadInt32(); // mods

                    var valueTag = r.ReadByte();
                    switch (valueTag)
                    {
                        case 0x0C: // Single
                            r.ReadSingle();
                            break;
                        case 0x0D: // Double
                            r.ReadDouble();
                            break;
                        default:
                            throw new BeatmapDatabaseFormatException(
                                $"Unexpected star-rating value tag 0x{valueTag:X2} (expected 0x0C Single or 0x0D Double) in slot {slot}.");
                    }

                    pairs++;
                }
            }
        }

        starPairs += pairs;

        var drain = r.ReadInt32();
        var total = r.ReadInt32();
        var preview = r.ReadInt32();
        var timingCount = r.ReadInt32();
        if (timingCount < 0 || timingCount > 10_000_000)
        {
            throw new BeatmapDatabaseFormatException($"Implausible timing point count {timingCount}.");
        }

        for (var t = 0; t < timingCount; t++)
        {
            r.ReadDouble(); // beatLength / msPerBeat
            r.ReadDouble(); // offset
            r.ReadByte();   // regular
        }

        timingPoints += timingCount;

        var beatmapId = r.ReadInt32();
        var setId = r.ReadInt32();
        r.ReadInt32(); // threadId
        r.ReadByte();   // gradeOsu
        r.ReadByte();   // gradeTaiko
        r.ReadByte();   // gradeCatch
        r.ReadByte();   // gradeMania
        r.ReadUInt16(); // localBeatmapOffset
        r.ReadSingle(); // stackLeniency
        var mode = (GameMode)r.ReadByte();
        OsuStrings.Read(r); // source
        OsuStrings.Read(r); // tags
        r.ReadUInt16();     // onlineOffset
        OsuStrings.Read(r); // titleFont
        r.ReadByte();       // unplayed
        r.ReadInt64();      // lastPlayedTicks
        r.ReadByte();       // osz2
        var folder = OsuStrings.Read(r) ?? string.Empty;
        r.ReadInt64();      // lastCheckedTicks
        r.ReadByte();       // ignoreSound
        r.ReadByte();       // ignoreSkin
        r.ReadByte();       // disableStoryboard
        r.ReadByte();       // disableVideo
        r.ReadByte();       // visualOverride
        if (version < FloatDifficultyVersion)
        {
            r.ReadUInt16(); // [INFERRED] legacy extra field
        }

        r.ReadInt32(); // unknownTail 鈥?opaque, values unverified
        r.ReadByte();  // unknownByte 鈥?opaque, 0 in every record of the reference file

        md5Valid = OsuStrings.LooksLikeMd5(md5);

        string? absolute = null;
        if (!string.IsNullOrEmpty(songsRoot) && folder.Length > 0 && osuFile.Length > 0)
        {
            // folderName may contain '\', so this must stay a relative path combination.
            absolute = Path.GetFullPath(Path.Combine(songsRoot, folder, osuFile));
        }

        return new BeatmapIndexEntry
        {
            Md5 = md5,
            FolderName = folder,
            OsuFileName = osuFile,
            BeatmapId = beatmapId,
            BeatmapSetId = setId,
            Mode = mode,
            Artist = artist ?? artistUnicode,
            Title = title ?? titleUnicode,
            DifficultyName = difficulty,
            Creator = creator,
            TotalTimeMs = total,
            PreviewTimeMs = preview,
            DrainTimeSeconds = drain,
            ApproachRate = ar,
            CircleSize = cs,
            HpDrainRate = hp,
            OverallDifficulty = od,
            SliderVelocity = sliderVelocity,
            TimingPointCount = timingCount,
            StarRatingPairCount = pairs,
            AbsolutePath = absolute
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
}

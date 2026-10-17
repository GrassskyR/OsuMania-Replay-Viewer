# `osu!.db` — binary format specification (osu!stable)

Reference implementation: [`tools/osudb/beatmapdb.py`](../tools/osudb/beatmapdb.py)
Raw validation log: [`tools/osudb/out/beatmapdb_validation_full.log`](../tools/osudb/out/beatmapdb_validation_full.log)
Target file: `D:\Game\osu\osu!.db` (26,227,647 bytes, mtime 2026-10-16 18:11)

> Measured on the real file. **[VERIFIED]** = backed by a numeric check in the log;
> **[INFERRED]** = reasoned, not independently proven. Third-party references were
> used only as a layout hypothesis and were then re-checked field by field against
> this file (see §9).

---

## 0. TL;DR for the C# implementer

1. Little-endian throughout; every string is an osu! **legacy string** (marker byte
   `0x00` = null / otherwise `0x0B`, then LEB128 length, then UTF-8) — identical to
   `scores.db`, see [`SCORESDB_FORMAT.md`](SCORESDB_FORMAT.md) §1.
2. Header: `int32 version`, `int32 folderCount`, `bool accountUnlocked`,
   `int64 unlockTicks`, `string playerName`, `int32 beatmapCount`.
3. Then `beatmapCount` records (§3), then a single trailing `int32`.
4. The record contains `md5`, `folderName` and `osuFileName` directly, so the
   beatmap file is `Path.Combine(osuRoot, "Songs", folderName, osuFileName)`
   — **no folder scanning or hashing needed**.
5. **Do not assume the Songs folder prefix is the beatmap-set id**: 410 of 22,123
   records contradict that heuristic - 397 are locally created maps with
   `beatmapsetId == -1` (folder names like `1 - 2` or
   `202310 - 6K Group Collection#A1`), 5 have other negative ids, and 8 have a
   positive `beatmapsetId` that differs from the folder prefix (a stale folder
   name). Always take `folderName` from the record.
   Also, `folderName` may itself contain `\` (28 records), so treat it as a
   relative path, not a single directory name.

---

## 1. Header **[VERIFIED]**

| offset | type | field | value in this file |
|---|---|---|---|
| 0 | `int32` | `version` | 20260924 |
| 4 | `int32` | `folderCount` | 5724 |
| 8 | `bool` (1 byte) | `accountUnlocked` | 1 |
| 9 | `int64` | `unlockTimeTicks` | 0 |
| 17 | legacy string | `playerName` | 3 CJK chars, raw UTF-8 `e4bf8ae8bebee8908c` = `俊达萌` |
| 28 | `int32` | `beatmapCount` | 22123 |

First record starts at offset **32**. `folderCount` is the number of *beatmap sets*
known to the client; 5,723 folders exist on disk (1 folder was removed since the DB
was written) — close enough to confirm the field's meaning.

## 2. Trailer **[VERIFIED]**

After the last record there is exactly one `int32` (value `5` here, file ends
immediately after it). Semantics unknown; skip it. Total consumption check:
**26,227,647 / 26,227,647 bytes, 0 remaining.**

---

## 3. Beatmap record

```csharp
// version-gated parts are marked; this build has version = 20260924
string  artist;              // 1
string  artistUnicode;       // 2
string  title;               // 3
string  titleUnicode;        // 4
string  creator;             // 5
string  difficultyName;      // 6
string  audioFileName;       // 7   may be null
string  md5;                 // 8   32 lowercase hex == MD5 of the .osu file bytes
string  osuFileName;         // 9   file name inside folderName
byte    rankedStatus;        // 10
ushort  countHitCircles;     // 11
ushort  countSliders;        // 12  mania: hold notes
ushort  countSpinners;       // 13
long    lastModifiedTicks;   // 14  .NET DateTime ticks (UTC)
float   approachRate;        // 15  ┐
float   circleSize;          // 16  │ version >= 20140609: 4x float32
float   hpDrainRate;         // 17  │ older: 4x byte
float   overallDifficulty;   // 18  ┘
double  sliderVelocity;      // 19
[star rating blocks x4]      // 20  version >= 20140609 (see §3.1)
int32   drainTimeSeconds;    // 21
int32   totalTimeMs;         // 22
int32   previewTimeMs;       // 23
int32   timingPointCount;    // 24
timingPoint[timingPointCount]; // 25 see §3.2
int32   beatmapId;           // 26  -1 or 0 when not submitted
int32   beatmapSetId;        // 27  -1 when not submitted
int32   threadId;            // 28  always 0 here
byte    gradeOsu;            // 29  last achieved rank per ruleset
byte    gradeTaiko;          // 30
byte    gradeCatch;          // 31
byte    gradeMania;          // 32
ushort  localBeatmapOffset;  // 33
float   stackLeniency;       // 34  e.g. 0.7
byte    mode;                // 35  0=osu 1=taiko 2=catch 3=mania
string  source;              // 36  e.g. "SOUND VOLTEX VIVID WAVE"
string  tags;                // 37  may be null
ushort  onlineOffset;        // 38
string  titleFont;           // 39  may be null
bool    unplayed;            // 40
long    lastPlayedTicks;     // 41  0 == never played
bool    osz2;                // 42
string  folderName;          // 43  Songs subfolder (may contain '\')
long    lastCheckedTicks;    // 44
bool    ignoreSound;         // 45
bool    ignoreSkin;          // 46
bool    disableStoryboard;   // 47
bool    disableVideo;        // 48
bool    visualOverride;      // 49
// (version < 20140609: ushort — absent in this build)
int32   unknownTail;         // 50  semantics NOT verified (§3.3)
byte    unknownByte;         // 51  always 0 here (§3.3)
```

All 51 fields above are what my reader walks; the walk consumes the file exactly,
so the **sizes** are proven even where a name is not.

### 3.1 Star-rating blocks **[VERIFIED]**

Four consecutive blocks, one per ruleset slot (osu, taiko, catch, mania):

```
int32 count
count × {
    byte  tagKey    == 0x08 (Int32)      -> int32 mods
    byte  tagValue  == 0x0C (Single)     -> float32 rating
                    or 0x0D (Double)     -> float64 rating
}
```

* The tag bytes are `.NET SerializationWriter.WriteObject` type tags
  (`osu.Game/IO/Legacy/SerializationReader.cs`, `ObjType`): `0x08` = Int32, `0x0C` =
  Single, `0x0D` = Double.
* **Measured**: `0x08` × 1,187,694 and `0x0C` × 1,187,694 — i.e. 1,187,694
  (mods, float32 rating) pairs, and **no `0x0D` value at all** in this file.
  Older/other community parsers hard-code `0x0D` + `double`; that is wrong for this
  build. **Dispatch on the tag byte** — that is what the game itself does.
* **The block index IS the ruleset id** (`0=osu, 1=taiko, 2=catch, 3=mania`) —
  **[VERIFIED]** by the occupancy patterns over all 22,123 records
  ([probe_20](../tools/osudb/probe_beatmapdb_20.py)):

  | beatmap mode | non-empty block index patterns (count) |
  |---|---|
  | 0 osu | `{0,2,3}` ×8,726 · `{0,3}` ×389 · `{0,1,2,3}` ×194 · `{0,1,3}` ×11 · `{3}` ×2 |
  | 1 taiko | `{1}` ×391 (always exactly its own block) |
  | 2 catch | `{2}` ×87 (always exactly its own block) |
  | 3 mania | `{3}` ×12,322 · `{0,3}` ×1 |

  So a beatmap's own ruleset block is always populated, and osu!std maps usually also
  carry the taiko/catch/mania *conversion* ratings (e.g. block 2 for an osu!std map).
* Example: record 0 (a 7K mania map) has block counts `0, 0, 0, 9`; its 9 mania pairs
  are `(0, 7.1218) (64, 10.0078) (256, 5.6274) (2, 7.1218) (66, 10.0078) (258, 5.6274)
  (16, 7.1218) (80, 10.0078) (272, 5.6274)` — the classic NM/DT/HT/EZ/DT+EZ/HT+EZ/HR/
  DT+HR/HT+HR mod-combination set.
* **[INFERRED]** the rating scale (the numbers are consistent with star ratings, but
  the exact scale/versioning is not needed for the MD5 index).

### 3.2 Timing point **[VERIFIED]**

```
double bpmOrMsPerBeat   // record 0: 329.67032967033 (== the .osu's beatLength)
double offsetMs         // record 0: 1790.0        (== the .osu's timing point time)
bool   regular          // record 0: 1
```

Note the order: the **beat length comes first, then the offset**. This was confirmed
against the real `.osu` file (`[TimingPoints]` line `1790,329.67032967033,4,2,1,60,1,0`).

### 3.3 The last two fields of the record **[PARTIALLY VERIFIED]**

* Sizes are proven (the whole-file walk consumes exactly 0 remaining bytes for all
  22,123 records), so they are 4 bytes + 1 byte.
* `unknownTail` (int32): 21,074 records are `0`; 1,049 records are non-zero with
  1,005 distinct values (e.g. `15` ×34, `20` ×9, `30` ×3, `163614` ×2). It is **not**
  the .osu file size, not a CRC32 of the .osu, not a truncation of `beatmapId`,
  `beatmapSetId`, `lastModified` or `lastChecked` (all tested:
  [probe_15](../tools/osudb/probe_beatmapdb_15.py)). Older layouts document a
  `read_int` here followed by `maniaScrollSpeed`; the field may be new in this build
  or the int/byte order may be swapped. **Treat as opaque; do not use.**
* `unknownByte`: `0` for **all** 22,123 records, so it cannot be validated as
  "mania scroll speed"; treat as opaque (0).
* A defensive reader should read `int32` then `byte` (as above) and **assert nothing**
  about the values.

---

## 4. Verified numerics

### 4.1 Structural

| quantity | value |
|---|---|
| file size | 26,227,647 bytes |
| `version` | 20260924 |
| declared `beatmapCount` | 22,123 |
| records parsed | 22,123 |
| bytes consumed | 26,227,647 / 26,227,647 (0 remaining) |
| trailing `int32` | 5 |
| star-rating pairs | 1,187,694 (all `Single`) |
| `unknownTail == 0` | 21,074 / 22,123 |
| `unknownByte == 0` | 22,123 / 22,123 |

### 4.2 MD5 → file validation (**full population, not a sample**)

| check | result |
|---|---|
| records whose `folderName` + `osuFileName` exist on disk | **22,002 / 22,123** |
| … folder missing (stale records, maps deleted by the user) | 120 |
| … file missing inside an existing folder | 1 |
| `.osu` files hashed and compared byte-MD5 against the record's `md5` | **22,002** |
| **byte-MD5 equal** | **22,001 / 22,002 = 99.9955 %** |
| byte-MD5 mismatch | 1 |
| parsed `audio_file` / `mode` / `ar` / `cs` / `hp` / `od` / `slider_velocity` / `preview_time_ms` mismatches vs the `.osu` text file | **0** |

The single MD5 mismatch is a **user-edited map**: the file is
`…\V.A. - 6K Galgame Project 01 (Arkman) [(marathon)[DELETE UPON DOWNLOAD!]].osu`
whose on-disk MD5 (`ea179530…`) no longer matches the DB (`677f5655…`). The folder
name literally says "DELETE UPON DOWNLOAD", i.e. the user edited it after import.
This is a real failure mode to design for (see §6) and not a parser defect.

### 4.3 Index self-consistency

| quantity | value |
|---|---|
| Songs folders on disk | 5,723 |
| records whose `folderName` is not on disk | 120 |
| records where `beatmapSetId` ≠ leading integer of `folderName` | 410 → 397 with `beatmapSetId == -1`, 5 with other negative ids, 8 with a positive id (stale folder name) — **do not derive the set id from the folder name** |
| records with a malformed (non-32-hex) `md5` | 0 |
| Songs folders not referenced by any record | 694 (maps installed after the DB was written) |
| mode histogram | mania 12,323 / osu 9,322 / taiko 391 / catch 87 |
| ranked-status histogram | 4 (ranked) 9,080 / 2 (pending) 4,824 / 7 (loved) 3,754 / 5 (approved) 2,359 / 1 (unsubmitted) 2,095 / 6 (qualified) 11 |

---

## 5. The MD5 → (folder, .osu file, ids, mode) index for C#

```csharp
sealed record BeatmapIndexEntry(
    string Md5, string FolderName, string OsuFileName,
    int BeatmapId, int BeatmapSetId, byte Mode, int TotalTimeMs, string Title);

static IEnumerable<BeatmapIndexEntry> ReadIndex(string osuDbPath)
{
    using var f = new FileStream(osuDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    var r = new BinaryReader(f, Encoding.UTF8);           // replaces malformed bytes with U+FFFD
    int version      = r.ReadInt32();
    int folderCount  = r.ReadInt32();
    bool unlocked    = r.ReadByte() != 0;
    long unlockTicks = r.ReadInt64();
    string player    = ReadLegacyString(r);
    int count        = r.ReadInt32();
    for (int i = 0; i < count; i++)
    {
        string artist = ReadLegacyString(r), artistU = ReadLegacyString(r);
        string title  = ReadLegacyString(r), titleU  = ReadLegacyString(r);
        string creator = ReadLegacyString(r), diff   = ReadLegacyString(r);
        string audio  = ReadLegacyString(r);
        string md5    = ReadLegacyString(r);
        string osuFile = ReadLegacyString(r);
        byte status = r.ReadByte();
        ushort circles = r.ReadUInt16(), sliders = r.ReadUInt16(), spinners = r.ReadUInt16();
        long lastMod = r.ReadInt64();
        float ar, cs, hp, od;                                   // version >= 20140609
        if (version < 20140609) { r.ReadByte(); r.ReadByte(); r.ReadByte(); r.ReadByte(); ar=cs=hp=od=0; }
        else { ar = r.ReadSingle(); cs = r.ReadSingle(); hp = r.ReadSingle(); od = r.ReadSingle(); }
        double sv = r.ReadDouble();
        if (version >= 20140609)
            for (int slot = 0; slot < 4; slot++)                // 4 star-rating blocks
            {
                int n = r.ReadInt32();
                for (int k = 0; k < n; k++)
                {
                    if (r.ReadByte() != 0x08) throw new InvalidDataException("sr key tag");
                    int mods = r.ReadInt32();
                    byte tag = r.ReadByte();
                    if (tag == 0x0C) r.ReadSingle();
                    else if (tag == 0x0D) r.ReadDouble();
                    else throw new InvalidDataException("sr value tag");
                }
            }
        int drain = r.ReadInt32(), total = r.ReadInt32(), preview = r.ReadInt32();
        int ntp = r.ReadInt32();
        for (int t = 0; t < ntp; t++) { r.ReadDouble(); r.ReadDouble(); r.ReadByte(); }
        int beatmapId = r.ReadInt32(), setId = r.ReadInt32(), threadId = r.ReadInt32();
        r.ReadByte(); r.ReadByte(); r.ReadByte(); r.ReadByte();  // grades
        r.ReadUInt16();                                          // local offset
        r.ReadSingle();                                          // stack leniency
        byte mode = r.ReadByte();
        string source = ReadLegacyString(r), tags = ReadLegacyString(r);
        r.ReadUInt16();                                          // online offset
        string font = ReadLegacyString(r);
        r.ReadByte(); r.ReadInt64(); r.ReadByte();                // unplayed, last played, osz2
        string folder = ReadLegacyString(r);
        r.ReadInt64();                                            // last checked
        r.ReadByte(); r.ReadByte(); r.ReadByte(); r.ReadByte(); r.ReadByte(); // 5 flags
        if (version < 20140609) r.ReadUInt16();
        r.ReadInt32(); r.ReadByte();                              // unknown tail (opaque)

        yield return new BeatmapIndexEntry(md5, folder, osuFile, beatmapId, setId, mode, total, title);
    }
    r.ReadInt32();                                                // file trailer (opaque; see note)
}

> **The trailer value is not a format constant.** osu!stable wrote `5` in one save of
> `osu!.db` on the reference install and `0` in the next (both measured 2026-10-16, same
> game build, both parsed to exact byte consumption with 22123/22123 records). Treat it as
> an opaque int32 whose *presence* is the contract — do not assert a specific value.

static string ReadLegacyString(BinaryReader r)
    => r.ReadByte() == 0 ? null : r.ReadString();   // ReadString() == LEB128 + UTF-8
```

Lookup for the replay pipeline:

```csharp
string OsuFilePath(string osuRoot, BeatmapIndexEntry e)
    => Path.Combine(osuRoot, "Songs", e.FolderName, e.OsuFileName);   // FolderName may contain '\'
```

### 5.1 Small JSON sample emitted for diffing against C#

* [`tools/osudb/index_sample.json`](../tools/osudb/index_sample.json) — **committed**
  reference output: 200 beatmaps (spread across the file) with every index-relevant
  field plus the timing-point and star-rating *counts*, and 10 `scores.db` groups
  (~150 KB, generated by [`emit_samples.py`](../tools/osudb/emit_samples.py)).
  Purpose: let the C# reader be diffed field-by-field against this Python reference.
  It is **not** a shipped index and it deliberately omits the two bulk arrays.
* [`tools/osudb/out/beatmapdb_sample.json`](../tools/osudb/out/beatmapdb_sample.json)
  — the first 100 records with *every* field including both bulk arrays (git-ignored
  local artifact, ~690 KB).
* [`tools/osudb/out/scoresdb_sample.json`](../tools/osudb/out/scoresdb_sample.json)
  — the first 25 `scores.db` groups (git-ignored).
* The full index is **not** committed (26 MB of JSON); it must be rebuilt from
  `osu!.db` at startup. If a cache is wanted, cache only
  `md5 → (folderName, osuFileName, beatmapId, beatmapSetId, mode)` in a compact
  binary/JSON form and key it on `osu!.db`'s size + last-write time.
* The **complete** walk (including timing points and star-rating pairs) is covered
  exhaustively by the differential test below, which is stronger evidence than a
  JSON sample.

---

## 6. Known failure modes and real-world caveats

| condition | observed here | handling |
|---|---|---|
| **`osu!.db` is stale** | 120 records point at folders that no longer exist; 694 on-disk folders are not in the DB | always `File.Exists` the composed path; fall back to hashing `Songs\**\*.osu`, or to the `scores.db` beatmap MD5 → scan path from the plan §3.2 |
| **`.osu` edited after import** | 1 map (MD5 in DB ≠ MD5 on disk) | treat the DB MD5 as "MD5 at import time"; for exact matching, hash the file or trust the `.osr` header's beatmap MD5 |
| **`folderName` contains `\`** | 28 records (locally created sub-collections) | use `Path.Combine(root, "Songs", folderName, osuFile)`, never `Path.Combine(root, "Songs", folderName.Replace('\\','_'), …)` |
| **null strings** | 332 null strings in the first 3,000 records (`audioFileName`, `tags`, `titleFont` are commonly null) | `ReadLegacyString` must return `null`; never call `.Length` unguarded |
| **invalid UTF-8 bytes** | 4 of ~265,000 strings (`artistUnicode` ×2, `titleUnicode` ×1, `tags` ×1) decode to U+FFFD | use `Encoding.UTF8` (replacement fallback). `Encoding.UTF8` with `throwOnInvalidBytes` would crash |
| **unknown `version`** | none observed | parse the header, then decide: `version < 20140609` changes 3 things (AR/CS/HP/OD are `byte`, star-rating blocks absent, an extra `ushort` before the tail int32). Everything else has been stable in the community layout for years. |
| **truncated / mid-write file** | not observed | wrap in try/catch → fall back to the `Songs` scan; osu! rewrites this file on exit |
| **very large file** | 26 MB, 22,123 records; entry lengths vary (strings + timing points) | stream it; do not `File.ReadAllBytes` if memory matters. The Python reference parses it in ~1 s. |

---

## 7. Exact commands used to validate

```
python tools\osudb\beatmapdb.py --validate --sample 25000 `
       --summary-json tools\osudb\out\beatmapdb_validation_full.json
python tools\osudb\probe_encoding_18.py          REM marker/length statistics
python tools\osudb\probe_beatmapdb_19.py         REM audio_file mismatch analysis
```

Full log: [`tools/osudb/out/beatmapdb_validation_full.log`](../tools/osudb/out/beatmapdb_validation_full.log).
A sampled log (500 records) and two JSON samples are in the same directory.
All probes open files read-only (`FileShare`/`open(..., 'rb')`) and never write to
`D:\Game\osu`.

### 7.1 Spec-implementability test

The §3 field table was transcribed into a **separate table-driven parser** in
[`tools/osudb/speccheck.py`](../tools/osudb/speccheck.py) (it does not call the
reference reader's record parser) and it reproduces the reference parse exactly:

```
osu!.db    spec-walk: records=22123 trailing=5 remaining=0  -> IDENTICAL to reference
```

It also asserts the two version-gated details (`version >= 20140609` float
difficulties, and the `0x0C`/`0x0D` star-rating value tags), so a C# implementation
written from this document is covered by the same check.

### 7.2 Encoding statistics **[VERIFIED]**

[`probe_encoding_18.py`](../tools/osudb/probe_encoding_18.py) re-walks both files
counting marker bytes and LEB128 lengths:

* `osu!.db`, first 3,000 records: 38,669 strings with marker `0x0B`, 332 with marker
  `0x00` (null), **no other marker byte**; 1,065 lengths needed **more than one
  LEB128 byte** (max string length 2,271) → the multi-byte LEB128 path is exercised,
  so a naive "single length byte" reader would break.
* `scores.db`: 18,950 × `0x0B`, 5,420 × `0x00`, no other marker; 0 multi-byte
  lengths; max length 32.

---

## 8. Verified vs assumed

**[VERIFIED]**
* header fields and offsets (record 0 starts at 32)
* the complete field walk: 22,123 records, exact byte consumption, single trailing int32
* legacy-string encoding incl. nulls, `0x0B` marker, multi-byte LEB128
* AR/CS/HP/OD (float32), slider velocity (double), mode, audio file name and preview
  time agree with the real `.osu` for all 22,002 reachable records (0 mismatches)
* star-rating blocks: 4 blocks, block index == ruleset id, `0x08` int32 mods +
  `0x0C` float32 rating, 1,187,694 pairs, occupancy pattern matches the ruleset
* timing points: `double msPerBeat`, `double offsetMs`, `bool regular`
* `md5` == byte MD5 of the on-disk `.osu` for 22,001/22,002 reachable records
* `folderName` + `osuFileName` compose the real path (22,002/22,123; the rest are
  stale records) and `folderName` may contain `\`

**[INFERRED / not proven]**
* semantic names for `gradeOsu/Taiko/Catch/Mania`, `localBeatmapOffset`,
  `onlineOffset`, `threadId`, `osz2`, `visualOverride` (positions/sizes proven;
  names come from the long-standing community layout)
* the star-rating **scale** (the numbers are consistent with star ratings; the
  block↔ruleset mapping itself is verified)
* **the last `int32` (`unknownTail`) and the last byte — opaque, values unverified**
* version-gated branches for `version < 20140609` (byte AR/CS/HP/OD, no star-rating
  blocks, an extra `ushort` between the 5 flag bytes and `unknownTail`) —
  **[INFERRED from other parsers; this file is 20260924]**
* whether the per-record leading `int32 osuFileSize` (present in older layouts)
  is genuinely gone in this build; the observed record 0 starts directly with the
  `artist` string and the whole-file walk is exact, so **it is gone here**

## 9. Third-party references used

* `osu.Game/IO/Legacy/SerializationReader.cs` and
  `osu.Game/Scoring/Legacy/LegacyScoreDecoder.cs` (ppy/osu, MIT) — the string
  convention, `ObjType` tags and the `.osr` layout. These were used as the model
  and then verified against the files.
* `silane/osudb-parser` (Python, community) — used **only** as the initial field-order
  hypothesis. Two of its assumptions are wrong for this build and were corrected from
  measurement: the per-record leading `int32 osu file size` (absent here) and the
  star-rating value type (`0x0C`/`float32` here, `0x0D`/`double` in that parser).
  A third (the trailing `int32` + `byte`) parses but its semantics remain unverified.

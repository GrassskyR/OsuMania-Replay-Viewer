# `scores.db` — binary format specification (osu!stable)

Reference implementation: [`tools/osudb/scoresdb.py`](../tools/osudb/scoresdb.py)
Raw validation log: [`tools/osudb/out/scoresdb_validation_full.log`](../tools/osudb/out/scoresdb_validation_full.log)
Target file: `D:\Game\osu\scores.db` (784,295 bytes, mtime 2026-10-16 18:07)

> **Everything below was measured on the real file on this machine.** Sections marked
> **[VERIFIED]** are backed by a numeric check in the validation log; sections marked
> **[INFERRED]** are reasoned but not independently proven. Nothing in this document
> was taken on faith from a third party.

---

## 0. TL;DR for the C# implementer

1. All integers/floats are **little-endian**.
2. Strings are **osu! "legacy strings"**: one marker byte (`0x00` = null, any other
   value = string follows) followed by the .NET `BinaryReader.ReadString()` payload
   (**7-bit-encoded length** (LEB128) + UTF-8 bytes).
3. The file is `int32 version`, `int32 groupCount`, then `groupCount` groups of
   `string beatmapMd5` + `int32 scoreCount` + `scoreCount` score records.
4. **The replay file for a score is NOT named after the record's `replayMd5`.**
   It is `Data\r\<beatmapMd5>-<FILETIME>.osr` where
   `FILETIME = dateTicks - 504911232000000000`. See §4 — this is the single most
   important correction to the assumption in the plan document.

---

## 1. Encoding primitives

### 1.1 Legacy string

Used for every string in this file (and in `osu!.db`, `.osr`, `collection.db`).

```
byte    marker        0x00 -> null; any other value -> string follows
                    (osu! always writes 0x0B for a non-null string)
[LEB128 byteLength]   only present when marker != 0
[byteLength bytes]    UTF-8
```

- **[VERIFIED]** the marker byte is `0x0B` for every non-empty/empty-but-non-null
  string in this file, and the following length is a plain 7-bit encoded int
  (values 0x04, 0x07, 0x09, 0x0A, 0x0C, 0x0D, 0x17, 0x20, 0x29, … were all observed
  and each exactly covered the following UTF-8 run).
- **[VERIFIED]** `hpGraph` is written as a single `0x00` byte (null) in all 5,420
  records.
- Authoritative reference for this convention:
  `osu.Game/IO/Legacy/SerializationReader.cs` (MIT) —
  `ReadString()` returns `null` when the first byte is `0`, otherwise delegates to
  `BinaryReader.ReadString()`.
- **[VERIFIED]** Lengths above 127 would need a two-byte LEB128; no string in
  `scores.db` is long enough to exercise that path (max observed length 32,
  [probe_encoding_18.py](../tools/osudb/probe_encoding_18.py)). The reader must still
  loop, because `osu!.db` does exercise it (1,065 multi-byte lengths).

### 1.2 LEB128 (7-bit encoded int)

```
result = 0; shift = 0
loop: b = readByte(); result |= (b & 0x7F) << shift; if ((b & 0x80) == 0) break; shift += 7
```

---

## 2. File layout

```
offset  type            field
------  --------------  --------------------------------------------------
0       int32           version            = 20260924
4       int32           beatmapGroupCount  = 2690
8       ...             beatmapGroup[0..2690)

beatmapGroup:
    legacy string   beatmapMd5     32 lowercase hex chars
    int32           scoreCount     number of score records that follow
    scoreRecord[scoreCount]

scoreRecord:                       (see §3)
```

### 2.1 **[VERIFIED]** version field

* `version = 20260924`.
* Interpretation **[INFERRED]**: the osu!stable build date in `YYYYMMDD` form
  (`2026-09-24`), i.e. the same convention the `.osr` replay header uses. The value
  is consistent with the file's mtime (2026-10-16) and with the per-record
  `version` values (range `20230326` … `20260924`, all of which look like build
  dates). The file also has a top-level version identical to `osu!.db`'s, which
  supports the "same osu! build wrote both" reading.
* **[INFERRED]** no version-dependent branching is known for `scores.db`. The
  per-record version does gate the trailing `int64 onlineId` in the analogous
  `.osr` format (`>= 20140721` uses `int64`, `>= 20121008` uses `int32`, older has
  none — see `LegacyScoreDecoder.cs`). **All 5,420 records in this file have
  `version >= 20230326`, so the `int64` branch is the only one that can be
  exercised here; the older branches are untested on this machine.** Practically it
  does not matter for this deployment (no record can exist with an older version in
  a 2026 build's database), but a defensive reader should branch anyway.

### 2.2 **[VERIFIED]** group / record counts

| quantity | value |
|---|---|
| declared `beatmapGroupCount` | 2690 |
| groups actually parsed | 2690 |
| groups with `scoreCount == 0` | 4 |
| total score records | 5420 |
| bytes consumed after the last record | **784,295 / 784,295 (0 remaining)** |
| duplicate group MD5s | 0 |

The declared count **is** trustworthy here: parsing exactly `beatmapGroupCount`
groups consumes the file to the last byte with no padding, and an independent
structural scan of the file for record-start signatures found **5,420** candidates —
the same number the grammar produces. ([source](../tools/osudb/probe_scoresdb_03.py))

---

## 3. Score record

Fixed order, **no length prefix, no padding**:

| # | type | field | notes |
|---|---|---|---|
| 1 | `byte` | `mode` | 0=osu, 1=taiko, 2=catch, 3=mania |
| 2 | `int32` | `version` | client build version (YYYYMMDD) that wrote the score |
| 3 | legacy string | `beatmapMd5` | **equals** the enclosing group's MD5 |
| 4 | legacy string | `playerName` | may be a 0-length string (258 records here) |
| 5 | legacy string | `replayMd5` | replay data hash — see §4 |
| 6 | `uint16` | `count300` | |
| 7 | `uint16` | `count100` | mania: "100" (200s) |
| 8 | `uint16` | `count50` | |
| 9 | `uint16` | `countGeki` | mania: MAX / 320s |
| 10 | `uint16` | `countKatu` | mania: 200s |
| 11 | `uint16` | `countMiss` | |
| 12 | `int32` | `totalScore` | |
| 13 | `uint16` | `maxCombo` | |
| 14 | `bool` (1 byte) | `perfect` | 0/1 only, observed |
| 15 | `int32` | `mods` | legacy mod bitmask |
| 16 | legacy string | `hpGraph` | `0x00` (null) in every record here |
| 17 | `int64` | `dateTicks` | .NET `DateTime` ticks, UTC |
| 18 | `int32` | `replayLength` | byte-array length; **`-1` in all 5,420 records** |
| 19 | `int64` | `onlineId` | the score id after upload; `0` in 4,012 records |

Record size = `119 + len(playerName)` bytes, assuming a null `hpGraph`
(the 1-byte `0x00`). Every record in this file satisfies exactly that: e.g. the
first three records (player `"GrassMe"`, length 7) are 126 bytes each at offsets
46 → 172 → 298, and the structural scan's dominant record deltas are 126
(×2,439) and 164 (×2,400, player length 45).

### 3.0 C# sketch

```csharp
static string ReadLegacyString(BinaryReader r)          // osu! "legacy string"
    => r.ReadByte() == 0 ? null : r.ReadString();       // ReadString() == LEB128 len + UTF-8

static IEnumerable<(string Md5, List<ScoreRecord> Scores)> ReadScoresDb(string path)
{
    using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    var r = new BinaryReader(f, Encoding.UTF8);
    int version     = r.ReadInt32();     // 20260924 here
    int groupCount  = r.ReadInt32();     // 2690
    for (int g = 0; g < groupCount; g++)
    {
        string beatmapMd5 = ReadLegacyString(r);
        int scoreCount    = r.ReadInt32();
        var list = new List<ScoreRecord>(scoreCount);
        for (int i = 0; i < scoreCount; i++)
        {
            var s = new ScoreRecord();
            s.Mode         = r.ReadByte();          // 1
            s.Version      = r.ReadInt32();         // 2  (client build, YYYYMMDD)
            s.BeatmapMd5   = ReadLegacyString(r);   // 3  == group key
            s.Player       = ReadLegacyString(r);   // 4
            s.ReplayMd5    = ReadLegacyString(r);   // 5  replay data hash
            s.Count300     = r.ReadUInt16();        // 6
            s.Count100     = r.ReadUInt16();        // 7
            s.Count50      = r.ReadUInt16();        // 8
            s.CountGeki    = r.ReadUInt16();        // 9
            s.CountKatu    = r.ReadUInt16();        // 10
            s.CountMiss    = r.ReadUInt16();        // 11
            s.TotalScore   = r.ReadInt32();         // 12
            s.MaxCombo     = r.ReadUInt16();        // 13
            s.Perfect      = r.ReadByte() != 0;     // 14
            s.Mods         = r.ReadInt32();         // 15
            s.HpGraph      = ReadLegacyString(r);   // 16 (null here)
            s.DateTicks    = r.ReadInt64();         // 17 UTC .NET ticks
            s.ReplayLength = r.ReadInt32();         // 18 -1 here
            if (s.ReplayLength > 0) r.ReadBytes(s.ReplayLength); // never observed
            s.OnlineId     = r.ReadInt64();         // 19
            list.Add(s);
        }
        yield return (beatmapMd5, list);
    }
    // 0 bytes must remain here; anything left means the layout assumptions are wrong
}

// score -> replay file
static string ReplayPath(string osuRoot, ScoreRecord s) =>
    Path.Combine(osuRoot, "Data", "r",
                 $"{s.BeatmapMd5}-{s.DateTicks - 504911232000000000L}.osr");
```

### 3.1 **[VERIFIED]** evidence per field

* **Alignment / sizes** — a structural scan that only knows "record starts with
  `mode ∈ 0..3`, `int32 version ∈ 1990xxxx..2999xxxx`, `0x0B` marker, length 32,
  32 hex chars" finds 5,420 starts, whose consecutive deltas are all
  `119 + playerNameLength`. Independently, the sequential grammar above consumes the
  file to exactly 0 remaining bytes. Two independent methods agree.
  ([probe_03](../tools/osudb/probe_scoresdb_03.py))
* **`mode`, `version`, `beatmapMd5`, `playerName`, `replayMd5`, 6 counts, `totalScore`,
  `maxCombo`, `perfect`, `mods`, `dateTicks`, `onlineId`** — all 18 fields were
  compared against the **matching `.osr` replay header** for the full population:
  **5,420 compared, 0 mismatching records, 0 mismatching fields**
  ([log](../tools/osudb/out/scoresdb_validation_full.log)). The one difference is
  by design (`replayLength`: `-1` in `scores.db` vs the real payload length in the
  `.osr`).
* **`mods` position** — the observed values are valid legacy bitmasks with a
  plausible distribution: `0` (×3964), `0x20000001` (V2|NF, ×537), `0x20000009`
  (V2|HD, ×172), `0x20000011` (V2|HR, ×114), `0x20000041` (V2|DT, ×93), `0x08`
  (HD, ×91), `0x20000000` (V2, ×72), `0x40` (DT, ×53), `0x48` (DT|HD, ×49),
  `0x02` (EZ, ×44), `0x40000000` (Mirror, ×43), `0x100` (HT, ×41), `0x01` (NF, ×21),
  `0x10` (HR, ×20), `0x101` (NF|HT, ×20) …
  Reading `mods` at any other candidate offset produces values that are not valid
  mod masks and would not match the `.osr` headers, so this is not just a
  "looks plausible" claim.
* **`dateTicks`** — all 5,420 values decode to a UTC timestamp in `[2000, 2100]`;
  e.g. record 0 = `638784763305386930` → `2025-03-25T05:12:10.538693Z`. Choosing any
  neighbouring 8-byte window instead yields a negative `int64` (which .NET's
  `DateTime` would reject) — see [probe_02](../tools/osudb/probe_scoresdb_02.py).
* **`replayLength == -1`** — observed value histogram over all records:
  `{-1: 5420}`.
* **`onlineId`** — see §3.2.

### 3.2 **[VERIFIED]** `onlineId` (the trailing `int64`)

* Position and size are fixed (see the alignment evidence above).
* **Identity proof**: for each of the 25 sampled records with `onlineId != 0` the
  value is **byte-identical** to the `int64` that follows the LZMA payload in the
  matching `.osr` file (`file_size == headerLength + replayLength + 8` holds
  exactly). In the `.osr` format that trailing `int64` is `LegacyOnlineID`
  (`osu.Game/Scoring/Legacy/LegacyScoreDecoder.cs`, read for `version >= 20140721`
  right after the replay byte array). The same field therefore appears in both
  files. ([probe_07](../tools/osudb/probe_scoresdb_07.py)) The full-population
  cross-check in §6 additionally compares `onlineId` for **all 5,420** records
  against that same trailing `.osr` field: **0 mismatches**.
* **[INFERRED]** semantic name: "online score id". 4,012 records have `0`
  (never uploaded), 1,408 have a non-zero id. Observed non-zero values are unique
  and roughly increasing with date (e.g. 28,311,422 in 2021 → 24,413,274,828 in
  2026), which is what an auto-increment server id looks like. Values above `2^32`
  occur, so it is genuinely an `int64`.

---

## 4. Score → replay file mapping (**the critical correction**)

> The plan document (§3.3) assumes the record's replay hash *is* the encrypted file
> name in `Data\r`. **That is false on this machine.** Measured facts:

1. The record's `replayMd5` field (field #5) matches the **3rd string of the
   `.osr` header** for 100 % of compared records — so it *is* the replay hash, it is
   just not used as a file name.
2. `Data\r` file names have the form

   ```
   <32 hex chars>-<18 decimal digits>.osr   (and a sibling .osg with the same base name)
   ```

   e.g. `2fca73901ad68ab297b5987bd65a5404-133873531305386930.osr`.
3. The leading 32 hex chars are the **beatmap MD5**, not the replay hash:
   **all 2,690** group MD5s appear as `Data\r` name prefixes, while **0 of 5,420**
   record replay hashes do. ([probe_04](../tools/osudb/probe_scoresdb_04.py))
4. The 18-digit suffix is a **Windows FILETIME** (100-ns ticks since 1601-01-01) and
   equals the record's own date:

   ```
   suffix = dateTicks - 504911232000000000
   ```

   where `504911232000000000` is the standard .NET-ticks value of 1601-01-01.
   **5,420 / 5,420 (100.0000 %)** of records resolve to an existing file with exactly
   this name, and the resolved file's `.osr` header agrees with the record on all 18
   fields. `133873531305386930` ↔ `2025-03-25T05:12:10.538693` is the first record.

5. Consequently the correct lookup is:

   ```csharp
   static string ReplayPathFor(ScoreRecord s) =>
       Path.Combine(osuRoot, "Data", "r",
                    $"{s.BeatmapMd5}-{s.DateTicks - 504911232000000000L}.osr");
   ```

### 4.1 Other `Data\r` statistics **[VERIFIED]**

| quantity | value |
|---|---|
| files in `Data\r` | 10,215 (5,639 `.osr` + 4,576 `.osg`) |
| distinct name prefixes | 2,783 |
| prefixes that are a `scores.db` group MD5 | 2,690 / 2,690 groups |
| prefixes not referenced by any `scores.db` group | 93 |
| records resolved to an existing `.osr` | 5,420 / 5,420 |

The 93 orphan prefixes are replays whose scores are no longer in `scores.db`
(deleted/reset scores) — a normal condition, not a parse failure.

`.osg` files share the base name with their `.osr`. Purpose untested/not needed
(presumably the in-game "watch replay" graph data); a resolver should look for
`.osr` first and may fall back to `.osg` only if the `.osr` is missing.

---

## 5. Known failure modes

| condition | symptom | recommended handling |
|---|---|---|
| Truncated file | `EOFError` while reading a group/record | report "scores.db is incomplete"; do not silently return partial data. `scores.db` is rewritten by osu! on every score, so a torn read is possible if the game is writing while you read. |
| Unknown/newer `version` | none observed; format has been stable across the observed range | read the fields anyway but keep the whole parse inside a try/catch and fall back to the plan's §3.3 level-3 "scan all `Data\r` headers" strategy. |
| Very old `version` (< 20140721) | the trailing `onlineId` would be `int32` or absent, shifting every subsequent record | branch on the per-record `version` (from `LegacyScoreDecoder.cs`) — **[INFERRED, untested here]** because this machine has no such records. |
| Empty group (`scoreCount == 0`) | none; 4 such groups exist | legal, just skip. |
| Non-null `hpGraph` | record grows by `1 + LEB128 + len` bytes | read it as a legacy string (never assume it is one byte). Not observed here. |
| `replayLength > 0` | a replay payload is embedded in the DB | read and skip `replayLength` bytes (implemented), then read `onlineId`. Not observed here (`-1` everywhere). |
| Empty `playerName` | 258 records | treat as a valid, empty player name; the corresponding `.osr` also has an empty name (verified on 15/15 spot checks). Do not use "player is non-empty" as a validity test. |
| Missing replay file | record resolves to a non-existent path | score was deleted from `Data\r`, or the score predates the file; surface "no replay file for this score". |
| `Data\r` locked/renamed | path missing | fall back to the plan's "scan all `.osr` headers and score-match" strategy. |

### 5.1 Byte-order / API gotchas for the C# port

* `BinaryReader.ReadString()` does the LEB128+UTF-8 part — but **it does not read
  the marker byte**, so you need `if (reader.ReadByte() == 0) return null;`
  before calling it. Using `BinaryReader.ReadString()` directly on the file will
  produce garbage.
* `Encoding.UTF8` (the default for `BinaryReader`) uses replacement fallback, which
  matches osu!'s own behaviour when a name contains non-UTF-8 bytes. In `osu!.db`,
  4 of ~265,000 strings contain such bytes (see `BEATMAPDB_FORMAT.md` §6) and decode
  to `U+FFFD`; they must not throw.
* Read the file with `FileShare.ReadWrite` (osu! may hold it open while playing) and
  never with write access.

---

## 6. Exact commands used to validate

Run from `D:\Projects\CSharp\Replay-Viewer` (Python 3.11 at `C:\Python311\python.exe`):

```
python tools\osudb\scoresdb.py --validate --osr-sample 5420 `
       --json tools\osudb\out\scoresdb_validation_full.json
```

Result summary (full log: `tools\osudb\out\scoresdb_validation_full.log`):

```
parsed groups                 : 2690 / 2690 declared
total score records           : 5420
bytes consumed                : 784295 / 784295  (remaining 0)
empty groups                  : 4
duplicate group md5           : 0
record-version range          : 20230326 .. 20260924
records failing inner_md5     : 0
records failing replay_md5    : 0
records failing mode          : 0
records failing version       : 0
records failing date          : 0
records failing hp_graph      : 0
records failing replay_len    : 0
records failing player        : 258   (legitimately empty names)
records with onlineId != 0    : 1408
records with the exact expected .osr : 5420 / 5420 (100.0000%)
group md5 present as name prefix     : 2690 / 2690
.osr files compared           : 5420 (missing 0)
records with any field mismatch      : 0
mismatch count per field             : {}
by-design differences                : replay_length x 5420
```

Supporting probes (each re-runnable, all read-only):

| script | what it proves |
|---|---|
| [`probe_scoresdb_01.py`](../tools/osudb/probe_scoresdb_01.py) | first-byte offsets, field-order hypotheses |
| [`probe_scoresdb_02.py`](../tools/osudb/probe_scoresdb_02.py) | `dateTicks` window is the only plausible `int64` |
| [`probe_scoresdb_03.py`](../tools/osudb/probe_scoresdb_03.py) | 5,420 structural record starts; record-size = 119 + name length |
| [`probe_scoresdb_04.py`](../tools/osudb/probe_scoresdb_04.py) | `Data\r` key is the beatmap MD5, not the replay hash |
| [`probe_scoresdb_05.py`](../tools/osudb/probe_scoresdb_05.py) | `<beatmapMd5>-<FILETIME>` mapping, 5420/5420 |
| [`probe_scoresdb_07.py`](../tools/osudb/probe_scoresdb_07.py) | `onlineId` == the int64 after the `.osr` LZMA payload |
| [`probe_encoding_18.py`](../tools/osudb/probe_encoding_18.py) | every string marker is `0x00`/`0x0B`; LEB128 length statistics |

### 6.1 Spec-implementability test

The field table in §3 was transcribed into a **separate table-driven parser** in
[`tools/osudb/speccheck.py`](../tools/osudb/speccheck.py) — it does not call the
reference reader's record parser — and it reproduces the reference parse exactly:

```
scores.db  spec-walk: groups=2690 records=5420 remaining=0  -> IDENTICAL to reference
osu!.db    spec-walk: records=22123 trailing=5 remaining=0  -> IDENTICAL to reference
DIFFERENTIAL TEST PASSED: both documented field tables reproduce the reference parse.
```

For C# diffing there is also a compact committed sample:
[`tools/osudb/index_sample.json`](../tools/osudb/index_sample.json)
(200 beatmaps + 10 score groups, ~150 KB; generated by
[`emit_samples.py`](../tools/osudb/emit_samples.py)) — it is reference output, not a
shipped index.

## 7. Verified vs assumed

**[VERIFIED]**
* little-endian; legacy-string encoding incl. the `0x0B` marker and LEB128 lengths
* `version = 20260924`, `groupCount = 2690`, 5,420 records, 4 empty groups
* exact byte consumption (0 remaining) and 5,420 independently-detected record starts
* full field list, order, types and sizes of the score record
* `replayLength == -1` for all records; `hpGraph == null` for all records
* `dateTicks` is a plausible UTC .NET timestamp for all records
* `Data\r` file name = `<beatmapMd5>-<dateTicks - 504911232000000000>.osr`, 100 % hit rate
* every field of the record equals the matching `.osr` header field (5,420/5,420)

**[INFERRED / not proven on this machine]**
* the semantic names `version`, `perfect`, `mods`, `onlineId` (positions and sizes are
  proven; the names come from the `.osr` format they are byte-identical to)
* version-dependent `int32` vs `int64` `onlineId` branching for pre-2014 records
* the meaning of `.osg` files
* that `scores.db` has no version-dependent layout changes other than those above

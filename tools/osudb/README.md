# tools/osudb — osu!stable database format reverse-engineering kit

Read-only tooling that parses `D:\Game\osu\scores.db` and `D:\Game\osu\osu!.db` and
validates the result against the real files on disk. **Nothing here ever writes to
`D:\Game\osu`** — every file is opened `rb` / `FileShare`-style read-only, and all
output goes to this directory.

The point of this kit is the two specifications:

* [`docs/SCORESDB_FORMAT.md`](../../docs/SCORESDB_FORMAT.md)
* [`docs/BEATMAPDB_FORMAT.md`](../../docs/BEATMAPDB_FORMAT.md)

They are precise enough to implement the C# readers from scratch; both contain a
copy-pasteable C# walk and the exact validation numbers behind every claim.

## Quick start

```powershell
# everything (regenerates all logs + samples); takes ~1 minute
cmd /c tools\osudb\run_validation.cmd

# individual pieces
C:\Python311\python.exe tools\osudb\scoresdb.py --validate --osr-sample 5420
C:\Python311\python.exe tools\osudb\beatmapdb.py --validate --sample 25000
C:\Python311\python.exe tools\osudb\speccheck.py        # differential test
C:\Python311\python.exe tools\osudb\emit_samples.py     # index_sample.json
```

## Files

| file | role |
|---|---|
| `osu_ser.py` | .NET/osu! binary primitives: `Reader` (buffer), `StreamReader` (file+seek), legacy strings, LEB128, ticks→ISO |
| `scoresdb.py` | **reference reader for `scores.db`** + validation CLI |
| `beatmapdb.py` | **reference reader for `osu!.db`** + validation CLI |
| `songs_index.py` | helper: index `Songs\**\*.osu` by file name, byte-MD5 files |
| `speccheck.py` | differential test: parses both files with a table-driven walk written straight from the docs and asserts it is identical to the reference readers |
| `emit_samples.py` | writes the compact `index_sample.json` used for C# diffing |
| `run_validation.cmd` | runs every validation and writes all logs into `out/` |
| `index_sample.json` | committed sample: 200 beatmaps + 10 score groups (reference output for diffing; ~150 KB) |
| `probe_*.py` | the individual reverse-engineering probes, kept because each one is cited as evidence in the docs |
| `ref/` | third-party reference sources that were consulted, for provenance (not used at runtime) |
| `out/` | generated logs + JSON validation summaries (git-ignored) |

## Key findings (short version)

1. `scores.db` v20260924: 2,690 groups, 5,420 records, exact byte consumption,
   every record cross-checked field-by-field against its `.osr` (5,420/5,420 match).
2. **The replay file is `Data\r\<beatmapMd5>-<FILETIME>.osr`**, not
   `<replayMd5>.osr` — 100 % of records resolve with
   `FILETIME = dateTicks - 504911232000000000`.
3. `osu!.db` v20260924: 22,123 records, exact byte consumption, 22,001/22,002
   reachable records have a byte-MD5 match with the `.osu` on disk, and 0 mismatches
   on the fields that also live in the `.osu` text file.
4. This `osu!.db` build has **no per-record leading `.osu` size int32** and stores
   star-rating values as `Single` (`0x0C` tag + float32), not `Double`.

## Console encoding warning

This console is GBK. Never `print()` raw bytes or decoded CJK strings directly —
use `repr()`, `.hex()`, or write to a UTF-8 file (see `probe_encoding_17.py`).

# Handoff — Mania Replay Viewer (Windows-native rewrite)

**Written for:** the next agent picking this up.
**Date context:** supersedes the previous handoff; its "22 synthetic checks failing" section was
already stale when it was written (the previous owner fixed most of it a few minutes later, and this
session fixed the real load-bearing bug it missed). This session also: proved the signature table cannot
be validated statically and fixed the scan scope that followed from it (§5.4), fixed two genuine
memory-layer bugs (§6.1), and smoke-tested the WPF app for the first time (§6.3).
**Repo:** `D:\Projects\CSharp\Replay-Viewer` (git repo initialised but **nothing committed yet** — the
working tree is the source of truth; `git status` shows everything as untracked)
**Source being ported:** `D:\Projects\Java\Mania-Replay-Master` (Kotlin, v2.4.0, read-only reference)
**Original design doc:** `WINDOWS_NATIVE_REWRITE_PLAN.md` (read §0 decision table first; **§3.3 and
§3.4.2 both carry measured corrections — see §5 below**)

---

## 1. What this is

A Windows-native (.NET 8 + WPF) rewrite of *Mania Replay Master*: an osu!mania (and taiko/Malody)
replay renderer that turns a `.osr`/`.mr` replay plus its `.osu`/`.mc` beatmap into an mp4 with
falling-note judgement visuals.

The headline new feature (vs. the Java original) is **osu! process integration**: while the user is
looking at *any* score screen in osu!stable, the app should automatically resolve that score's replay
file and beatmap file and pre-fill the UI — not just for the map that was just played.

**Non-negotiable constraint from the user:** never write anything under `D:\Game\osu` (the real osu!
installation). Read-only access at most. All test copies of files live in `fixtures/`.

---

## 2. Current status — measured, not asserted

| Component | State | Evidence (command behind it) |
|---|---|---|
| `src/ManiaReplayMaster.Core` | **Done, differentially verified** | 14,672 notes + 15,149 replay actions field-equal to the reference jar (§4) |
| `src/ManiaReplayMaster.Cli` | **Done** | `dump-map`, `dump-replay`, `dump-judge`, `benchmark`, `trace-osr` |
| `src/ManiaReplayMaster.Render` | **Done, verified** | 47 PNG slices on a real fixture |
| `src/ManiaReplayMaster.Encode` | **Done, verified** | real ffmpeg export, ffprobe-checked |
| `src/ManiaReplayMaster.Game` — file-system layer | **Done, verified with numbers** | §6.2 |
| `src/ManiaReplayMaster.Game` — memory layer | **Machinery verified; scan scope fixed; offsets still need a live game** | §6.1 |
| `src/ManiaReplayMaster.App` | **Smoke-tested through the real UI**; one blocking wiring bug found and fixed | `.\build.ps1 appcheck` (§6.3) |
| `tests/ManiaReplayMaster.Tests` | **19/19 pass** | 4 of them full differential tests |
| `tests/ManiaReplayMaster.RenderEncode.Tests` | **73/73 pass** | exact ffmpeg argv pinning + E2E |
| `tools/gamecheck` harness | **128 checks pass, 0 fail** (`gamecheck all`) | log in `tools/gamecheck/harness/out/gamecheck-all.log` |
| Solution | `ManiaReplayMaster.sln` builds, 0 errors | 9 projects |

Reproduce everything:

```powershell
$env:DOTNET_ROOT="D:\Tools\dotnet8\sdk"
$d = "D:\Tools\dotnet8\sdk\dotnet.exe"
& $d build "D:\Projects\CSharp\Replay-Viewer\ManiaReplayMaster.sln" -v q --nologo
& $d test  "D:\Projects\CSharp\Replay-Viewer\tests\ManiaReplayMaster.Tests\ManiaReplayMaster.Tests.csproj" --nologo
& $d test  "D:\Projects\CSharp\Replay-Viewer\tests\ManiaReplayMaster.RenderEncode.Tests\ManiaReplayMaster.RenderEncode.Tests.csproj" --nologo
& $d run --project "D:\Projects\CSharp\Replay-Viewer\tools\gamecheck\harness\ManiaReplayMaster.GameCheck.csproj" -- all
```

---

## 3. How to build and run (environment specifics — read this or you will be stuck)

This machine has **no usable system-wide .NET 8 SDK**. `dotnet` on PATH is **x86 SDK 5.0.100**, which
cannot build `net8.0-windows`. A portable .NET 8 SDK was extracted to:

```
D:\Tools\dotnet8\sdk\dotnet.exe        (SDK 8.0.404)
```

`global.json` pins `8.0.404`. **Always set `DOTNET_ROOT` before invoking it** (the helper
`build.ps1` does this for you: `.\build.ps1 build | test | cli <args> | app | e2e | gamecheck`).

### NuGet is unreachable — packages come from a committed local feed

The .NET HTTP stack cannot reach `api.nuget.org` (offline CRL check →
`CRYPT_E_REVOCATION_OFFLINE`, shows up as `NU1301`). `NuGet.config` therefore clears all sources and
uses only the committed `packages/` directory (flat `.nupkg` feed).

If you need a new package:
```powershell
curl.exe -sSL --ssl-no-revoke -o "packages\<id>.<version>.nupkg" `
  "https://api.nuget.org/v3-flatcontainer/<id>/<version>/<id>.<version>.nupkg"
```
Plain `curl.exe` works; the .NET stack does not. Then reference it with an exact pinned version.

### Other environment facts

| Thing | Value |
|---|---|
| ffmpeg / ffprobe | `C:\ffmpeg\ffmpeg.exe`, `C:\ffmpeg\ffprobe.exe` — **not on PATH** |
| Java (to run the reference jar for golden data) | `C:\Program Files\JRE-1.8` runs the shipped jar fine |
| Reference jar (golden source) | `D:\Tools\ManiaReplayMaster v2.4.0\ManiaReplayMaster v2.4.0\ManiaReplayMaster.jar` |
| Python | `C:\Python311\python.exe` |
| Console encoding | **GBK** — never `print()` raw bytes in Python; write UTF-8 files or use hex/repr. Also: **console output of `§`/`—` looks like mojibake (`搂`/`鈥?`) even when the file is correct UTF-8** — check with the file reader, not the console |
| Display scaling | **150%** (Windows reports a 1080 DIP window as 1620 physical px). `GetWindowRect`/`PrintWindow` from a DPI-**unaware** process get virtualised coordinates, which makes a correct layout look cropped. Call `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` before capturing — `tools/appcheck/app-smoke.ps1` does it for you |
| osu! install | `D:\Game\osu` (read-only!). `scores.db`, `osu!.db`, `Songs/` (24,833 `.osu` files), `Data\r/` (10,215 files) |

---

## 4. The verification that matters most (and how to re-run it)

The Core judgement layer is **not** "it runs" — it is **byte-for-byte equal to the reference
implementation**. Golden JSON is committed at `tests/golden/*.json`, produced by the shipped v2.4.0 jar.

| Fixture | notes | actions | accuracy | counts |
|---|---|---|---|---|
| `KuitDoinPineNut` | 2,711 | 2,515 | 0.758883560801677 | 805,776,585,251,16,279 |
| `CarnationLN` | 1,798 | 1,814 | 0.9714844136022894 | 1782,1046,132,9,3,15 |
| `RaijinTempest` | 1,461 | 1,523 | 0.8966461327857637 | 458,652,271,47,22,12 |
| `YuugureMarathon` (ScoreV2) | 8,702 | 9,297 | 0.9703545229089618 | 9857,5009,703,61,46,64 |

Total: **14,672 notes + 15,149 replay actions** compared field-by-field, plus all scalar fields (mode,
key, od, rate, mirror, isScoreV2, judgementWindow, durationMs, acc, judgementCounts). `acc` compared at
12-decimal tolerance.

### Regenerating the goldens from the reference (if you ever change Core)

A Java harness `Bench.java` was used to dump the reference's post-judgement state. It is **not
committed** (it lived in `$env:TEMP\javabench`), so recreate it if needed:

1. Compile a class against the reference jar: `javac -cp "<jar>" -d <out> Bench.java`
2. Replicate `Main.prepareJudgements` exactly: read beatmap → read replay → add **+1000 ms** to every
   beatmap note and every replay action timestamp → `duration = max(lastReplayEnd, lastBeatmapEnd) + 2000`
   → `getJudger(...).judge()`.
3. Dump **both** `beatMap.notes` and `replayModel.replayData` with: index, timeStamp, column, duration,
   judgementStart, judgementEnd, offSetStart, offSetEnd, showAsLN, and `relatedActionOrNote` flattened
   to an **index** (it is a bidirectional reference — serializing it directly recurses forever).
4. Diff against the C# dump: `mrm dump-judge <beatmap> <replay> --out <file.json>`.

Reference API notes (the jar is Kotlin, so names differ from the source):
- `Config.INSTANCE.setDebug(...)` is **private** — do not call it.
- `ReplayModel.getRate()` (not `getR`), `isScoreV2()`.
- `BaseJudger.judgementFromJudging` has **no public accessor** — recompute the counting block yourself.
- `Note` ctor is `(long timeStamp, int column, long duration, ...)` — beware the `Int` vs `Long` overloads.

---

## 5. Corrections to the design document (important — do not trust these parts of the plan)

### 5.1 `WINDOWS_NATIVE_REWRITE_PLAN.md` §3.3 is wrong about replay file naming

The plan said the replay is found via the score record's replay hash. **Wrong.** Measured on the real
`scores.db`:

- The replay file is `Data\r\<beatmapMd5>-<FILETIME>.osr` where `FILETIME = dateTicks - 504911232000000000`.
- **5420/5420** records resolve exactly this way. All 2690 group beatmap-MD5s appear as `Data\r` name
  prefixes; **0/5420** replay hashes do.
- The record's `replayMd5` field *is* the replay hash (3rd string of the matching `.osr` header), it is
  just not the file name.
- 258 records have a legitimately **empty player name** (their `.osr` is empty too) — do not treat
  "player non-empty" as a validity test.
- 93 `Data\r` prefixes have no `scores.db` group (orphans); `Data\r` holds 5639 `.osr` + 4576 `.osg`.

Full spec: `docs/SCORESDB_FORMAT.md`; `osu!.db` spec: `docs/BEATMAPDB_FORMAT.md`. Reference Python
readers + spec-vs-reference differential test: `tools/osudb/`. **Re-run `cmd /c tools\osudb\run_validation.cmd`
to reproduce every number.** The correction is also now inlined into plan §3.3 itself.

### 5.2 `.osr` header strings have an extra lead byte — the plan's premise was incomplete

Each header string is **one lead byte, then a 7-bit-encoded length, then that many UTF-8 bytes**.
Decoding the lead byte as part of the length yields an 11-character truncated MD5 on *every* fixture and
desyncs everything after it. Implemented in `src/ManiaReplayMaster.Core/IO/OsuBinaryReader.cs`
(`ReadLine`) with a comment explaining this. Keep the differential suite green if you touch it.

Also: the `.osr` payload is **LZMA `FORMAT_ALONE`** (13-byte header: 5 property bytes + 8-byte
little-endian uncompressed size), decoded with **SharpCompress `LzmaStream`**. A hand-rolled decoder was
attempted and abandoned — do not reintroduce one.

### 5.3 `codecProfile: "unknown"` is fatal — deliberate divergence from the reference

The reference ships `codecProfile: unknown` and passes it verbatim as `-profile:v unknown`. Modern
libx264 **rejects** it (`invalid profile: unknown`, exit −22, no output). The port omits `-profile:v`
unless the value is a real H.264 profile (`baseline|main|high|high10|high422|high444`). Rationale lives
on `FfmpegCommandBuilder.AppendEncoderOptions`. Caveat: the gate keys on the profile string only, not on
`-c:v`, so a non-libx264 codec could still be handed a known-H.264 name. Only matters if the GUI exposes
a codec dropdown.

### 5.4 **NEW (this session): the signature table must be scanned process-wide, not module-wide**

Plan §3.4.2 presents the tosu/gosumemory byte patterns as things you find in `osu!.exe`. **Measured
otherwise:** `osu!.exe` is a **managed .NET assembly** — PE has a CLR data directory (RVA 0x2008, size
0x48), the only import is `mscoree.dll`, and the 4.2 MB `.text` section holds IL. Consequently **0 of the
10 table signatures occur anywhere in the mapped image**; they exist only as **JIT-compiled native code**
in the CLR's executable heaps at runtime. tosu's own scanner confirms the model: its native
`find_pattern` walks **every committed region** with `VirtualQueryEx` and reads each one.

Why it mattered: the production scanner used to scan `module.BaseAddress..module.End` for every entry
(`SearchWholeProcess` defaulted to `false`), which on a live game returns **0/10** and silently disables
the entire in-memory feature. Fixed by:

- `SignatureEntry.SearchWholeProcess = true` for **every** real entry (`SignatureTables.Stable`);
- `SignatureScanner.ResolveProcessWide(...)`: one region enumeration, one read per window, **all**
  patterns matched per window, **executable regions first** with early exit. Measured on the synthetic
  target: 55–74 regions / ~42 MB in **72–97 ms**; a full sweep of everything (303 regions / 77 MB) in
  126 ms. A live osu! should be a few hundred MB at worst, so attach + rescans are affordable;
- `GameStateWatcher` now tolerates a **partial** table at attach and re-sweeps for the missing entries
  with a growing back-off (2 s → 60 s, max 8 attempts), merging what appears. This is required: **JIT is
  lazy** — a signature only exists once the player has entered the screen whose method contains it, so
  "3/10 at attach, 10/10 during gameplay" is normal, not a version mismatch.

Evidence + per-signature table: `docs/SIGNATURE_SCAN_REPORT.md`, regenerated by
`gamecheck staticsig --report docs/SIGNATURE_SCAN_REPORT.md`.

**Static analysis cannot validate the table's semantics.** It can only prove the patterns are absent
from the image. Confirming that a resolved address really is the beatmap/score on screen needs a running
game (plan V2–V7) — do not claim otherwise.

### 5.5 The `osu!.db` trailer int32 is not a constant (also now in `docs/BEATMAPDB_FORMAT.md`)

The trailer was `5` in one save of `osu!.db` and `0` in the next (both measured on this machine, same
game build, both parsing to exact byte consumption with 22,123/22,123 records). Treat it as an opaque
int32 whose *presence* is the contract; do not assert a specific value. (This also tells you the game
was launched on 2026-10-16 23:35 — it rewrote `osu!.db`, changing both the trailer and the file size.)

---

## 6. What is NOT done / not verified

Read this before claiming anything works.

### 6.1 Memory layer — machinery verified, semantics unverified (⚠ no live game yet)

**Green now** (`gamecheck memory32`, 68 checks, 0 failures, all against a synthetic 32-bit target):

- `OpenProcess` with the 32-bit pointer-width assertion, and refusal of the wrong width (the classic
  silent-garbage trap).
- Toolhelp32 module enumeration (`TH32CS_SNAPMODULE32`), `VirtualQueryEx` region walking.
- **64-bit region walk: 496 regions, including regions above 4 GB.** This was a *real bug*: every
  `VirtualQueryEx` call passed `(nint)(uint)address`, truncating 64-bit addresses to their low 32 bits.
  The old "measured 2, required ≥ 20" failure the previous handoff called "possibly a harness
  expectation" was the production bug. Fixed via `VirtualQueryExAt`, asserted by a >4 GB check.
- Chunked reads over 4 KB boundaries: the 8 MB block verified byte-for-byte (0 mismatches, 14 ms).
- 4-byte pointer chains (1- and 2-step), string reads through chains, 8-byte read folding the
  neighbouring field (the wrong-width trap), unmapped-address refusal.
- **Chain endpoint validation (new).** A chain whose last step reads a non-pointer word used to be
  reported as success producing a bogus address (`0x7E7E7E7E`); now `MemoryChainWalker` requires the
  final address to be committed readable memory (`IMemoryAddressMap.IsAddressReadable`) and fails with a
  message naming the endpoint. Null pointers still terminate cleanly (and the test now runs inside the
  target's 4 GB space, so it proves null handling rather than "unmapped read failed").
- Wildcard signature parsing/matching (`??` = don't-care), anchor-prefiltered `FindAll`, whole-process
  resolution, and a signature that **straddles a 1 MB scan-window boundary** (found and resolved).
- Module-image vs heap: the harness proves a module-image scan cannot see a heap signature, which is the
  osu!stable situation.

**Still unverified / open:**

- **No live-game calibration has ever happened.** Offsets in `OsuStableStructure` are transcribed from
  the plan and marked `Verified = false`. Whether each resolved address is *semantically* the beatmap /
  score on screen is unproven. The game **was** launched once (2026-10-16) but not while this layer was
  being tested; ask the user before launching it, and never write under `D:\Game\osu`.
- `GameLinkService.PollSnapshot()` still returns `GameSnapshot.NotRunning(...)` with an honest message,
  and `src/ManiaReplayMaster.App/GameBridge.cs` documents that in-memory score detection is not wired
  into the service. `GameStateWatcher` is the piece that does the polling; wiring it into the service/UI
  is unstarted.
- No UI surface for partial signatures / rescan progress (only `Diagnostic` strings).
- Signature **ambiguity** is a real risk: the synthetic test produced a second, coincidental match for
  one pattern, and `Resolve` takes `matches[0]`. On the real game the table's patterns are long enough
  that this is unlikely, but if a live run resolves to a nonsense address, check for extra matches first.

### 6.2 File-system detection layer: VERIFIED with numbers ✅

Measured by running the harness against the real `D:\Game\osu` (read-only):

**`scores.db`** — 2690/2690 groups, 5420 records, **784295/784295 bytes consumed (0 trailing)**,
version 20260924, 4 empty groups, 0 records whose MD5 differs from its group. Sampled 30 records:
**30/30 (100%)** replay files exist under `Data\r`; **30/30 (100%)** parsed `.osr` headers agree with
the db record on all 12 fields; header-only reader == Core `OsuReplayParser` on 10/10 files.

**`osu!.db`** — 22123/22123 records, **26227638/26227638 bytes consumed (0 trailing)**, version
20260924, 22116 unique MD5s (the file was rewritten by the game; an earlier save was 26227647 bytes with
trailer 5 — see §5.5). Sampled 25: **25/25 (100%)** `Songs` paths exist, **25/25 (100%)** `.osu`
byte-MD5 equals the index MD5.

**`ReplayResolver` level 3 (header index over `Data\r` + `Replays`)** — **5,780/5,780 (100%)** of files
yielded a parseable header (cold 5,780 parsed; warm 5,780 from cache).

**Resolver accuracy (plan §7.3 V5)** — with a memory snapshot available: **Top-1 = 30/30 (100%)**, all at
≥99% confidence, 0 ambiguous ties within 5%. Without memory, the level-1 `scores.db` pointer offered the
expected file for **30/30 (100%)** and listed every local score of the beatmap that has a file,
**30/30 (100%)**. `Songs` scan: 24,833 `.osu` on disk, 24,537 unique MD5, cache reuse 24,833/24,833.

This is the half of the headline feature that works today and needs no game running.

### 6.3 Rendering / encoding / UI gaps

- **The WPF app is now smoke-tested through its real window** (UI Automation, `.\build.ps1 appcheck`,
  screenshot `docs/app-smoke.png`). What was exercised: launch → window render at 150% DPI → osu! install
  detection ("osu! located at D:\Game\osu") → setting both paths by editing the boxes → the action buttons
  enabling → **Judge only** → the judgement summary matching the committed golden exactly
  (`notes 2711, actions 2515, accuracy 75.89%, counts 805, 776, 585, 251, 16, 279`) → clean close, no
  crash log. The first such run found a **blocking bug**: `CanRender` is derived from both paths but was
  never re-notified, so the Render/Judge buttons could never enable at all — the app was unusable
  (fixed, and the smoke test now guards it).
- **Not yet exercised through the UI:** the `Render` path end-to-end (`appcheck -Render` exists but was
  not run in this session — the encode path itself is covered by the RenderEncode/E2E tests), the Browse /
  Locate / Swap dialogs, drag-and-drop, the score-candidate dropdown, and auto-detect during a live game.
- **Taiko has no fixture** — all 5 `.osu` files in `fixtures/` are `Mode: 3` (mania). Taiko stripes, note
  rings and duel-connection lines are covered **only by synthetic unit tests**. Any taiko claim is
  unverified on real data.
- **`rate ≠ 1.0` never ran through real ffmpeg** — all four replays are rate 1.0. Non-unit `atempo` is
  covered only by exact-argv tests.
- **No real-BGM fixture** — no fixture ships its audio file; the audio path was verified with a
  *synthesised* tone named per `AudioFilename`.
- **No pixel-level AWT↔GDI+ diff** — deliberately out of scope; parity claim is "judgement-identical +
  visually equivalent" (§4.2/§7.4 of the plan).
- Malody `.mr`/`.mc` parsing is ported but was **not** differentially verified (no golden dump for it),
  because the reference jar path only exercised `.osr`.

### 6.4 Known-fixed issues, for context

- **`CanRender` was never re-notified** (found by the first automated UI pass, §6.3): a derived property
  bound to `IsEnabled`, recomputed only when `IsBusy` changed — so Render / Judge only could never enable
  from a typed, pasted, browsed or auto-filled path. `ReplayPath`/`BeatmapPath` now raise it.
- 12 duplicate `using System.IO;` directives in the App project (CS0105 build noise) were removed and the
  four parameter boxes got `x:Name`s so UI Automation can address them.
- `FfmpegLocator` returned the bare string `"ffmpeg"` when ffmpeg was absent from PATH, killing the
  merge stage. Now checks PATH itself plus `C:\ffmpeg\ffmpeg.exe`, `%ProgramFiles%\ffmpeg\bin`, WinGet.
- `Config.Refresh` had two bugs found by tests: `TrimEnd()` left list indentation intact (so `- FFFFFF`
  never matched), and the pending-list flush ran *before* the key was captured. Both fixed.
- The previous handoff's §6.1 was partly a **misdiagnosis**: what it called "`??` wildcards decoded as
  literal 0x00 bytes" was `MemoryPattern.AnchorBytes`, which intentionally substitutes 0x00 for
  wildcards as a pre-filter representation (`FixedByteCount`/`Wildcards` are the authoritative view).
  The real symptoms it saw ("0 hits", "no match") came from the scan-range/bounds defects described in
  §5.4/§6.1 and were fixed before this session. Do not "fix" `AnchorBytes`.
- The previous handoff also claimed the game-link harness sits "one level deeper than
  `build.ps1 gamecheck` expects"; that was already resolved — `.\build.ps1 gamecheck` works.

---

## 7. Where things live

```
ManiaReplayMaster.sln
├─ src/ManiaReplayMaster.Core/          net8.0  — pure logic, no UI/Win32 deps
│   ├─ Model/       Note, BeatMap, ReplayModel, Config (27 fields, reference defaults)
│   ├─ IO/          OsuBinaryReader (7-bit strings + the lead-byte quirk)
│   ├─ Replay/      IReplayReader, OsuReplayParser (LZMA via SharpCompress), OsuReplayReader, MalodyReplayReader
│   ├─ Beatmap/     IMapReader, OsuMapReader, MalodyMapReader
│   ├─ Judger/      BaseJudger, OsuManiaJudger, OsuTaikoJudger, MalodyJudger
│   └─ Pipeline/    Pipeline.Prepare / Pipeline.Build  (Main.prepareJudgements + diffable snapshot)
├─ src/ManiaReplayMaster.Render/        net8.0-windows — System.Drawing port of Render.kt
├─ src/ManiaReplayMaster.Encode/        net8.0 — ffmpeg argv builder, runner, progress
├─ src/ManiaReplayMaster.Game/         net8.0-windows — locator, scores.db/osu!.db, resolver, memory
│   └─ Memory/      ProcessMemory (IMemoryReader + IMemoryAddressMap), SignatureScanner (module + whole-process),
│                   SignatureTables + OsuStableStructure (data), MemoryChainWalker, OsuStableReader, GameStateWatcher
├─ src/ManiaReplayMaster.Cli/           net8.0 — headless harness (`mrm`)
├─ src/ManiaReplayMaster.App/           net8.0-windows — WPF GUI
├─ tests/ManiaReplayMaster.Tests/       net8.0 — Core unit + 4 differential tests
├─ tests/ManiaReplayMaster.RenderEncode.Tests/  net8.0-windows — 73 tests (argv pinning + E2E)
├─ tests/golden/                        frozen reference JSON
├─ fixtures/                            copied beatmaps/replays (never write to D:\Game\osu)
├─ packages/                            committed local NuGet feed
├─ tools/osudb/                         analyst's Python reference parsers + validation
├─ tools/e2e/                           end-to-end render+encode harness
├─ tools/gamecheck/                     game-link validation harness (harness/ + 32-bit x86target/)
├─ tools/appcheck/                      UI smoke test: app-smoke.ps1 (UI Automation driven)
└─ docs/                                SCORESDB_FORMAT.md, BEATMAPDB_FORMAT.md,
                                        SIGNATURE_SCAN_REPORT.md, app-smoke.png, HANDOFF.md
```

### Useful commands

```powershell
.\build.ps1 build                       # whole solution
.\build.ps1 test                        # both test projects
.\build.ps1 cli dump-judge <map> <replay> --out out.json
.\build.ps1 cli trace-osr <replay.osr>  # byte-level .osr header trace
.\build.ps1 e2e                         # render+encode one fixture
.\build.ps1 app                         # launch the WPF app
.\build.ps1 appcheck                    # UI smoke test: judge a fixture, verify golden numbers, screenshot
.\build.ps1 appcheck -Render -Fps 5 -Width 180 -Height 320   # same, through render+encode (slow)
.\build.ps1 gamecheck [subcommand]      # game-link harness (default: all)
cmd /c tools\osudb\run_validation.cmd   # regenerate the DB format evidence
```

The game-link harness lives at `tools\gamecheck\harness\` (with its 32-bit synthetic target at
`tools\gamecheck\x86target\`); `.\build.ps1 gamecheck` resolves that path for you. To run it directly:

```powershell
$env:DOTNET_ROOT="D:\Tools\dotnet8\sdk"
$gc = "D:\Projects\CSharp\Replay-Viewer\tools\gamecheck\harness\ManiaReplayMaster.GameCheck.csproj"
& "D:\Tools\dotnet8\sdk\dotnet.exe" run --project $gc -- all
# subcommands: locate | scoredb | beatmapdb | replayindex | resolve | watch
#              | songsscan | memory32 | staticsig
# staticsig options: --exe <osu!.exe>  --report <path.md>
# log: tools\gamecheck\harness\out\gamecheck-<command>.log
```

The memory32 command builds its own synthetic 32-bit target; **rebuild it after changing its handshake**
or the harness will report stale/missing fields:

```powershell
& "D:\Tools\dotnet8\sdk\dotnet.exe" build tools\gamecheck\x86target\ManiaReplayMaster.X86Target.csproj
```

---

## 8. Recommended next steps, in order

1. **Wire `GameStateWatcher` into `GameLinkService` / `GameBridge` / the UI.** The watcher now polls,
   attaches, resolves whole-process and rescans for lazily-JIT'd signatures; nothing consumes it. This is
   the last piece of the headline feature that is pure plumbing.
2. **Real-machine calibration, with the user's explicit permission.** Launch osu! at least once (menu →
   song select → gameplay → results) and record, per signature, whether it resolved and whether the
   chain-read values match the screen. This is the only way to flip `StructureField.Verified` and the
   only way to answer plan §3.4.3 unknown #1 (is there a stable offset for *the selected leaderboard
   row*). Everything needed is already in place: `gamecheck staticsig` for the static half,
   `GameStateWatcher` + diagnostics for the dynamic half.
3. **Finish the UI paths the smoke test does not cover**: run `build.ps1 appcheck -Render` once (small
   settings), then exercise Browse / Locate / Swap, drag-and-drop, the candidate dropdown, and auto-detect
   with a live game. The judge path is already pinned to golden numbers, so anything failing here is
   plumbing.
4. **Add a Malody differential golden** if you want that path covered (`MalodyReplayReader` is the only
   reader never cross-checked against the reference).
5. **Add a taiko fixture and a non-unit-rate replay** so the render/encode gaps in §6.3 get real coverage.
6. **Commit the repository.** Nothing is committed; a first commit (or at least `git add -A && commit`)
   would make every future handoff safer.

### Working agreements that were used and should continue

- Teammates each owned disjoint paths; **do not edit another owner's project**.
- Never write under `D:\Game\osu` — read-only, always with `FileShare.ReadWrite` when the game may be up.
- Prefer *proving* a claim with a measured number over asserting it. Every "verified" statement in this
  document has a command behind it; keep that standard.
- When a handoff claim and the on-disk state disagree, **re-measure first** — this document's predecessor
  was already stale when written.

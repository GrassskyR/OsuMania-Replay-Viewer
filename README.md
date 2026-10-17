# Mania Replay Viewer

A **Windows-native** replay renderer for **osu!mania** (plus osu!taiko and Malody), rewritten from the
Kotlin/Java project [`Mania-Replay-Master`](https://github.com/Keytoyze/Mania-Replay-Master) as
C# / .NET 8 + WPF. It turns a replay (`.osr` / `.mr`) and its beatmap (`.osu` / `.mc`) into an mp4
showing the falling notes coloured by judgement.

The one feature the original does not have: **osu! process integration**. Instead of only being able
to render the map you just played, the app watches osu!stable and, when you open *any* score screen,
resolves that score's replay file and beatmap file and pre-fills them — because `scores.db` is the
index from a local score to its replay, and `Data\r` keeps those replay files forever.

---

## Status

| Layer | State |
|---|---|
| Core (parse + judge) | **Verified equal to the reference implementation**, field by field, on 14,672 notes / 15,149 actions across 4 fixtures |
| Render + Encode | Working: real ffmpeg export verified (540×960, h264, 60 fps, AAC) |
| CLI harness | Working |
| WPF GUI | Builds and runs the same proven pipeline; not yet smoke-tested interactively |
| osu! file-system detection (`scores.db`, `osu!.db`, `Data\r`) | Implemented against a measured format spec |
| osu! in-memory score detection | **Live-verified on stable b20260924** — score screen → exact local replay + beatmap; WPF auto-fill verified (2026-10-17). See [calibration notes](docs/LIVE_MEMORY_FIX.md) |

See [`docs/HANDOFF.md`](docs/HANDOFF.md) for the precise list of what is and is not verified, plus
the environment quirks you need to build this.

---

## Requirements

- Windows 10/11, x64
- **ffmpeg** (`ffmpeg.exe`). Looked up as: explicit path → next to the app → current directory → `PATH` → `C:\ffmpeg\ffmpeg.exe` → `%ProgramFiles%\ffmpeg\bin` → WinGet links.
- For building: .NET 8 SDK. On this machine it lives at `D:\Tools\dotnet8\sdk` (see below).
- Optional: an osu!stable install, for the auto-detection feature.

---

## Build and run

This machine has no usable system-wide .NET 8 SDK (the one on `PATH` is an x86 SDK 5.0.100, which
cannot build `net8.0-windows`). A portable SDK is extracted to `D:\Tools\dotnet8\sdk`, and `global.json`
pins `8.0.404`.

Use the helper script — it sets `DOTNET_ROOT` for you:

```powershell
.\build.ps1 build                        # build the whole solution
.\build.ps1 test                         # run every test project
.\build.ps1 app                          # launch the WPF application
.\build.ps1 e2e                          # render + encode one fixture end to end
.\build.ps1 cli dump-judge <map> <replay> --out out.json
```

Or invoke the SDK directly:

```powershell
$env:DOTNET_ROOT = "D:\Tools\dotnet8\sdk"
& "D:\Tools\dotnet8\sdk\dotnet.exe" build .\ManiaReplayMaster.sln -v q --nologo
```

### NuGet

The machine cannot reach `api.nuget.org` (an offline TLS revocation check fails; you will see
`NU1301`). `NuGet.config` therefore clears all remote sources and restores from the committed
`packages/` directory. To add a package, download its `.nupkg` with plain `curl` (which works) into
`packages/`:

```powershell
curl.exe -sSL --ssl-no-revoke -o "packages\my.package.1.0.0.nupkg" `
  "https://api.nuget.org/v3-flatcontainer/my.package/1.0.0/my.package.1.0.0.nupkg"
```

---

## Using the app

1. Drop a `.osr` onto the window (or use **Browse**).
2. The beatmap is located automatically from the replay's embedded MD5. For Malody `.mr` files, pick the `.mc`/`.mcz` yourself.
3. If osu! is running and auto-detect is on, the paths fill in by themselves; the **Detected score** row shows which score was recognised and offers a dropdown when several candidates match.
4. Adjust speed / resolution / FPS / codec, then press **Render**. **Judge only** runs the judgement and prints the counts without encoding.
5. Cancel is supported — it aborts rendering between windows and kills the running ffmpeg.

Videos are saved in the configured output directory as
`Player - Artist - Title [Difficulty] (yyyy-MM-dd) OsuMania.mp4` (with the replay's mode and local play date).
This also gives hash-named `Data\r` replays readable names. Invalid Windows filename characters are
replaced with `_`; very long names are shortened with a hash suffix. Malody replays and files missing
the naming metadata keep the original replay filename stem.

---

## Project layout

```
src/ManiaReplayMaster.Core      pure logic (no UI/Win32): models, .osr/.osu/.mr/.mc parsers, judgers
src/ManiaReplayMaster.Render    System.Drawing port of the original Render.kt (long-strip PNG slices)
src/ManiaReplayMaster.Encode    ffmpeg command builder, process runner, progress, cancellation
src/ManiaReplayMaster.Game      osu! locator, scores.db / osu!.db readers, replay resolver, memory reader
src/ManiaReplayMaster.Cli       headless harness (dump-map / dump-replay / dump-judge / benchmark)
src/ManiaReplayMaster.App       WPF GUI
tests/                          unit + differential tests, and frozen reference JSON in tests/golden
tools/                          validation harnesses (osu DB formats, end-to-end, game link)
docs/                           format specs and the handoff notes
fixtures/                       copied test beatmaps and replays
```

### How the rendering works (worth knowing before optimising anything)

The original — and this port — does **not** render video frames. It draws **one static long-strip PNG
per time window** (a 3-minute map is typically 10–50 images, not 10,000 frames) and lets ffmpeg's
filter graph animate the scroll:

```
color=s=WxH,fps=60[bg]; [bg][png]overlay=y=-h+H+t*fps*speed:shortest=1[v]; [v]trim=end=<sliceDuration>
```

So the renderer is cheap and ffmpeg does the work. This is also why the original's
`ffmpegMaxProcessingSize` (20) chunking exists: a single `-filter_complex` with hundreds of inputs
explodes. That behaviour is preserved.

---

## Licensing notes

- This is a rewrite of an **Apache-2.0** project; keep that licence and attribution.
- **ffmpeg is an external dependency** and is invoked as a separate process (never linked). The
  default codec is `libx264`, which is **GPL-only** — if you distribute ffmpeg with the app you must
  comply with the GPL, or switch to a non-GPL encoder (see §4.5 of the design document).
- **Process memory reading is strictly read-only.** The app never injects, never writes to another
  process, and never hooks. That is a deliberate design constraint: osu!'s rules forbid gaining an
  advantage with third-party tools, not reading memory, and the risky techniques (DLL injection for
  overlays) are exactly what this does not do. If this ever changes, the rules and the EULA both
  become a problem.

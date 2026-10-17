# Live osu!stable memory calibration — 2026-10-17

Verified against the already running `D:\Game\osu\osu!.exe`, stable b20260924, on a results screen.
All game access was read-only. Reference: [tosu stable.ts](https://github.com/tosuapp/tosu/blob/master/packages/tosu/src/memory/stable.ts).

The patterns were largely copied correctly, but their read semantics were not:

| Problem | Correction |
|---|---|
| WPF `PollSnapshot()` always returned `NotRunning` | Start `GameStateWatcher` in the bridge; consume its background snapshots and dispose it on shutdown. Respect the memory checkbox. |
| tosu `readPointer` treated as a single pointer read | Spell out operand → static slot → value. Beatmap is `read32(read32(baseAddr - 0xC))`; status is `read32(read32(statusMatch - 4))`. |
| CLR string object read as NUL-terminated UTF-8 | Read Int32 length at +4, then UTF-16 at +8 for x86. Keep native-string support for native fixtures. |
| Results used the gameplay score chain | Results: `read32(ruleset + 0x38)`. Gameplay: `read32(read32(ruleset + 0x64) + 0x38)`. |
| Score mods taken from menu/code bytes | XOR the score object's mods keys at `read32(score + 0x1C) + 8/+0xC`; menu mods use their own +9 operand. |
| 100/300 offsets swapped; combo read as Int32 | 300 at +0x8A, 100 at +0x88; max combo is UInt16 at +0x68. |
| DateTime binary interpreted as raw ticks | Decode `DateTime.FromBinary()` so kind bits do not invalidate the date. |
| `A1` game clock resolved as a relative call | Resolve its absolute data operand. |
| Replay flag dereferenced in the wrong order | `readByte(read32(canRunSlowlyAddr + 0x46))`. |
| CS used to guess mania | Read actual ruleset mode; CS 4 also occurs in other modes. |
| Comparing an auto-filled path with an empty textbox threw | Check blank paths before `Path.GetFullPath()`. This was reproduced in the real WPF window. |
| Module scan crawled inaccessible pages byte by byte | Partition a range by readable committed regions before scanning. |

The watcher also refreshes its region map as CLR heaps change, notices replacement process IDs,
and includes score date/mode/hit counts in change detection. The UI only auto-fills confident replay
matches on score screens.

## Measured result

- State: `Results / Ok`; signatures: **10/10**; structure fields: **25/25**.
- Player: `俊达萌`; mode: mania; score: **739977**; max combo: **806**; mods: none.
- Hits `[300,100,50,geki,katu,miss]`: **3260 / 214 / 9 / 4315 / 920 / 168**.
- Date: `2026-10-13T13:37:01.4384367Z`.
- Beatmap MD5: `c6487dd157b63778054999463c44e163`; file hash confirmed.
- Replay: `D:\Game\osu\Data\r\c6487dd157b63778054999463c44e163-134363722214384367.osr`.
- Beatmap: `D:\Game\osu\Songs\973787 Various Artists - Vibro Dan Courses (2-2)\Various Artists - Vibro Dan Courses (2-2) (Conversation) [Dan 10].osu`.
- Replay match: **100%**, matching player/mods/score/combo/hits/date. Both files exist.

## Repeatable verification

Use the portable SDK described in README (set `DOTNET_ROOT` accordingly):

```powershell
dotnet build ManiaReplayMaster.sln
dotnet build tools/gamecheck/x86target
dotnet run --project tools/gamecheck/harness -- memory32
dotnet run --project tools/gamecheck/harness -- live --poll 3
dotnet test ManiaReplayMaster.sln --no-build --no-restore
powershell -File tools/appcheck/memory-live.ps1
```

The x86 regression fixture reproduces instruction operands, global slots, CLR strings (Chinese and
surrogate pairs), differing gameplay/results scores, differing menu/score mods, packed UInt16 combo,
DateTime kind bits, and replay flags. The checks failed **27 assertions before the fix**, and pass
**111 checks after it**. The live check passes **20 checks**. The existing test projects pass **92 tests**.
The GUI check reads actual textbox values through Windows UI Automation without supplying paths.
Logs are under `out/` and `tools/gamecheck/harness/out/`.

This validates the installed stable build and current local score. Future stable layouts need
recalibration. An online score without a downloaded/exported replay has no local `.osr` path to find;
memory supplies score identity, while `scores.db` / replay headers supply the local file mapping.

# Static signature report - osu!stable

- exe: `D:\Game\osu\osu!.exe` (4,541,216 bytes)
- PE: I386 (32-bit), image base 0x400000, size of image 0x456000
- CLR directory: RVA 0x2008, size 0x48 (I386 (32-bit))
- managed assembly: **True**
- table: osu!stable (plan §3.4.2) - osu!stable 2026 build (b20260924) — scan scope verified, offsets UNVERIFIED

| signature | pattern | matches in image | process-wide | section @ RVA | note |
|---|---|---|---|---|---|
| `baseAddr` | `F8 01 74 04 83 65` | 0 | True | - | absent from the image (expected: JIT-only code) |
| `statusPtr` | `48 83 F8 04 73 1E` | 0 | True | - | absent from the image (expected: JIT-only code) |
| `playTimeAddr` | `5E 5F 5D C3 A1 ?? ?? ?? ?? 89 ?? 04` | 0 | True | - | absent from the image (expected: JIT-only code) |
| `settingsClassAddr` | `83 E0 20 85 C0 7E 2F` | 0 | True | - | absent from the image (expected: JIT-only code) |
| `rulesetsAddr` | `7D 15 A1 ?? ?? ?? ?? 85 C0` | 0 | True | - | absent from the image (expected: JIT-only code) |
| `canRunSlowlyAddr` | `55 8B EC 80 3D ?? ?? ?? ?? 00 75 26 80 3D` | 0 | True | - | absent from the image (expected: JIT-only code) |
| `gameTimePtr` | `A1 ?? ?? ?? ?? 89 46 04 8B D6 E8` | 0 | True | - | absent from the image (expected: JIT-only code) |
| `skinDataAddr` | `74 2C 85 FF 75 28 A1 ?? ?? ?? ?? 8D 15` | 0 | True | - | absent from the image (expected: JIT-only code) |
| `menuModsPtr` | `C8 FF ?? ?? ?? ?? ?? 81 0D ?? ?? ?? ?? ?? 08 00 00` | 0 | True | - | absent from the image (expected: JIT-only code) |
| `MANIA_SPEED` | `a3 ?? ?? ?? ?? eb ?? dd 45 08 db 5d e8 8b 45 e8 83 f8 28` | 0 | True | - | absent from the image (expected: JIT-only code) |

## Verdict

`osu!.exe` is a managed IL assembly, so the signature table **cannot be validated statically against the exe**. The native patterns exist only as JIT-compiled code in the CLR's executable heaps at runtime. This matches tosu's implementation, whose native `find_pattern` sweeps every committed region with `VirtualQueryEx` instead of the module image. The table entries are therefore all `SearchWholeProcess = true`; whether each resolved address is *semantically* the beatmap/score on screen still requires a running game.

Interpretation rules:

- For a managed image, `matches = 0` is the **expected** result, not a table failure: the patterns are IL-JIT code.
- For a native image, `matches = 1` is required per entry: `SignatureScanner.Resolve` takes `matches[0]`, so an ambiguous pattern is a coin flip.
- Runtime addresses are `module base + RVA`; absolute file bytes would be preferred-image-base VAs (image base 0x400000).
- Static analysis can never confirm semantics (that the MD5 read from memory is the map on screen). That needs a running game (plan V2-V7).

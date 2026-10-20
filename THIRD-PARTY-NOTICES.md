# Third-party notices

Mania Replay Viewer is distributed under **GPL-3.0-only** ([LICENSE](LICENSE)).
The project-wide license covers the combined application and our changes; it does
not replace the licenses or copyright notices of the original components below.

## Source provenance

| Source | Use | Original license |
|---|---|---|
| [Mania-Replay-Master](https://github.com/Keytoyze/Mania-Replay-Master), v2.4.0 | C# port of parsing, models, judgement, rendering and FFmpeg encoding; reference judgement test data | [Apache-2.0](licenses/Apache-2.0.txt) |
| [tosu](https://github.com/tosuapp/tosu), [stable memory reader](https://github.com/tosuapp/tosu/blob/master/packages/tosu/src/memory/stable.ts) | Reference for modified C# memory reads, pointer chains, signature and structure tables | [LGPL-3.0](licenses/LGPL-3.0.txt) |
| [gosumemory](https://github.com/l3lackShark/gosumemory) | Reference for osu!stable signatures and structure layouts | [GPL-3.0](LICENSE) |

Mania-Replay-Master: Copyright (c) 2020-present, project contributors.
tosu: Copyright (C) 2023-2026 Mikhail Babynichev and contributors.
gosumemory: l3lackShark and contributors.

The C# files are modified implementations, not unmodified upstream distributions.
The port uses .NET/Avalonia/GDI+ (with a retained WPF reference entry), adds process integration and localization, and changes
output naming, error handling, cancellation and scroll compositing. Apache source
attribution is preserved in [NOTICE](NOTICE); LGPL/GPL-derived material is covered
by the project-wide GPL-3.0 terms.

## Runtime dependencies

| Component | Version | License |
|---|---|---|
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | 0.37.2 | [MIT, Adam Hathcock](licenses/SharpCompress.txt) |
| [ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp) | 0.8.0 (transitive) | [MIT, Oleg Stepanischev](licenses/ZstdSharp.txt); underlying [Zstandard BSD license](licenses/Zstandard.txt) |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | 12.1.4 | [MIT](licenses/Avalonia.txt) |
| [SkiaSharp](https://github.com/mono/SkiaSharp) / Windows native assets | 3.119.4 | MIT and native third-party notices in `licenses/packages/` |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp) / Windows native assets | 8.3.1.3 | MIT and native third-party notices in `licenses/packages/` |
| [Avalonia ANGLE](https://github.com/AvaloniaUI/angle) | 2.1.27548.20260419 | BSD and third-party notices from package LICENSE in `licenses/packages/` |
| [System.Drawing.Common](https://github.com/dotnet/winforms) | 10.0.0 | MIT and third-party notices in `licenses/packages/` |
| [.NET Native AOT runtime](https://github.com/dotnet/runtime) | 10.0.13 (SDK 10.0.402) | MIT and runtime ThirdPartyNotices in `licenses/packages/` |
| .NET Windows Desktop Runtime (WPF reference package only) | .NET 10 | Microsoft runtime LICENSE and ThirdPartyNotices retained in legacy self-contained output |

Test-only dependencies (xUnit and Microsoft.NET.Test.Sdk) are restored by NuGet;
they are not shipped with the application.

## FFmpeg

Scripted releases include the Windows x64 static **FFmpeg 9.0.2 essentials** build
from [Gyan](https://www.gyan.dev/ffmpeg/builds/), including libx264, under GPLv3.
It is invoked as a separate process. The publish script verifies the pinned
archive's SHA-256 and preserves upstream notices and build/source references in
`licenses/ffmpeg/`. The original archive is cached under `out/dependencies/ffmpeg/`
and is not committed to this repository.

FFmpeg copyright (c) 2000-2026 the FFmpeg developers; included libraries retain
their respective authors' copyrights. Source: [FFmpeg 9.0.2](https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz)
and the [build provider's dependency sources](https://www.gyan.dev/ffmpeg/builds/#libraries).
Anyone redistributing FFmpeg must also provide the corresponding source,
including its libraries and build configuration, as required by GPLv3.
See [FFmpeg licensing](https://ffmpeg.org/legal.html).

## Test content

Beatmaps and replays under `fixtures/` are regression inputs, with authors and
players identified by their metadata. They retain their respective creators'
rights and are not relicensed under this project's GPL. They contain no bundled
song audio and are excluded from the application publish output. `tests/golden/`
contains reference results produced by Mania-Replay-Master for these fixtures.

When distributing a binary, include LICENSE, NOTICE, this file and `licenses/`,
and make the matching source code, including build scripts, available under GPL-3.0.

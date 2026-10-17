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
The port uses .NET/WPF/GDI+, adds process integration and localization, and changes
output naming, error handling, cancellation and scroll compositing. Apache source
attribution is preserved in [NOTICE](NOTICE); LGPL/GPL-derived material is covered
by the project-wide GPL-3.0 terms.

## Runtime dependencies

| Component | Version | License |
|---|---|---|
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | 0.37.2 | [MIT, Adam Hathcock](licenses/SharpCompress.txt) |
| [ZstdSharp.Port](https://github.com/oleg-st/ZstdSharp) | 0.8.0 (transitive) | [MIT, Oleg Stepanischev](licenses/ZstdSharp.txt); underlying [Zstandard BSD license](licenses/Zstandard.txt) |
| [.NET / Windows Desktop Runtime](https://github.com/dotnet/windowsdesktop) | .NET 8, as selected by the SDK | Microsoft runtime distribution's LICENSE and ThirdPartyNotices, retained in self-contained output |

Test-only dependencies (xUnit and Microsoft.NET.Test.Sdk) are restored by NuGet;
they are not shipped with the application.

## FFmpeg

FFmpeg is an external executable installed by the user. It is invoked as a
separate process and is not included in the publish output. Its license depends
on how it was compiled; builds with libx264 generally use GPL terms. Anyone
redistributing FFmpeg must provide the notices and source required by that build.
See [FFmpeg licensing](https://ffmpeg.org/legal.html).

## Test content

Beatmaps and replays under `fixtures/` are regression inputs, with authors and
players identified by their metadata. They retain their respective creators'
rights and are not relicensed under this project's GPL. They contain no bundled
song audio and are excluded from the application publish output. `tests/golden/`
contains reference results produced by Mania-Replay-Master for these fixtures.

When distributing a binary, include LICENSE, NOTICE, this file and `licenses/`,
and make the matching source code, including build scripts, available under GPL-3.0.

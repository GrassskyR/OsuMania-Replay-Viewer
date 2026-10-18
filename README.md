# Mania Replay Viewer

Windows 原生回放渲染工具，使用 C# / .NET 8 + WPF，将 osu!mania、osu!taiko 和 Malody 的谱面与回放渲染为 MP4。判定和渲染逻辑移植自 [Mania-Replay-Master](https://github.com/Keytoyze/Mania-Replay-Master)，不需要 Java。

## 功能

- 打开或拖入回放，按判定结果着色显示音符与操作。
- 联动 osu!stable：在成绩界面识别当前成绩，自动定位本地回放与谱面。
- 调整滚动速度、分辨率、FPS、编码器与预设；支持取消导出。
- 中文 / English 即时切换；默认跟随系统，中文系统显示中文，其余显示英文。
- 自动记忆语言、滚动速度、编码器和编码预设。
- 使用 osu! 导出风格的可读视频名称，包括玩家、歌曲、难度和日期。

## 运行

需要 **Windows 10/11 x64**。Release 包已附带 .NET 运行时和 FFmpeg，解压后即可运行，无需另行安装。普通构建产物需要 [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0)。

请保留 Release 包中与应用同目录的 `ffmpeg.exe`。自行使用普通构建产物时，安装包含 `libx264` 和 AAC 编码器的 FFmpeg，将其放在应用程序旁，或加入 `PATH`。运行端的查找顺序为：显式路径、应用目录、当前目录、`PATH`、常见安装目录与 WinGet 链接。

1. 启动 `ManiaReplayMaster.exe`。
2. 拖入 `.osr` 或 `.mr`，也可通过“浏览”选择文件；osu! 谱面会尝试根据回放中的 MD5 自动定位。未找到时手动选择 `.osu` / `.osz`，Malody 使用 `.mc` / `.mcz`。
3. 如需自动读取成绩，打开 osu!stable，启用自动检测与内存读取，再进入一条成绩的结果界面。可在 GUI 指定 osu! 安装目录。
4. 调整参数与输出目录，点击“渲染”。“仅判定 / Judge only”只计算判定并输出统计，不生成视频，也不需要 FFmpeg。

界面默认参数为 **540 × 960、60 FPS、流速 20、编码器 auto**。流速单位是像素/帧，改变 FPS 会影响相同流速下的滚动速度。

### GPU 自动编码

编码器下拉框选择 `auto`，导出前会使用当前 FFmpeg、输出分辨率与帧率实际试编码，按 **NVIDIA NVENC → Intel QSV → AMD AMF** 的顺序选择可用编码器。FFmpeg 列出某个编码器并不代表显卡或驱动能使用它；试编码失败会继续检测下一个，全部不可用时使用 CPU（`libx264 / ultrafast`）。自动模式下，正式编码遇到 GPU 故障也会用 CPU 重新编码，日志会显示最终采用的编码器与预设。

随包 FFmpeg 9.0.2 的 NVENC 需要 NVIDIA 驱动 **610.00 或更新版本**。较旧驱动可能仍支持其他版本的 FFmpeg，但会被当前版本的试编码检测排除；此时可更新驱动，或自行更换兼容驱动、包含所需编码器与 `libx264` 的 FFmpeg。

可手动选择 `libx264` 强制使用 CPU，或指定 `h264_nvenc`、`h264_qsv`、`h264_amf`。切换编码器时预设重置为 `auto`，分别使用 `ultrafast`、`p1`、`veryfast`、`speed`；也可手填该编码器支持的预设。编码器为 `auto` 时，编码器与预设均自动选择。手动指定的编码器失败时会报告错误。

已有的编码器偏好会保留；升级后如果仍显示 `libx264`，切换到 `auto` 即可启用自动检测。GPU 加速视频编码，音符绘制和滚动合成仍使用 CPU，因此低分辨率或较快 CPU 上不一定更快。长视频的分段合并直接复制已编码的视频流，避免重复编码及其画质损失。直接使用核心配置的默认编码器仍为 `libx264 / ultrafast`，配置文件也可设为 `codec: auto`。

视频命名示例：

```text
Player - Artist - Title [Difficulty] (2026-10-17) OsuMania.mp4
```

日期取回放的本地游玩日期；模式随回放变化。非法文件名字符替换为 `_`，过长名称缩短并加哈希后缀。Malody 或缺少必要元数据的回放使用原文件名。

设置保存在 `%LOCALAPPDATA%/ManiaReplayViewer/ui-settings.json`，异常日志位于同目录的 `crash.log`。

### 自动检测的范围

内存联动面向 **osu!stable**，曾在 b20260924 的成绩界面验证；游戏更新后可能需要更新签名与字段偏移。osu!lazer 不在当前支持范围内。

内存只提供成绩身份，程序再通过 `scores.db`、`osu!.db` 和本地回放头匹配实际文件。在线成绩必须先有本地下载或导出的回放；缺失的 `.osr` 无法从成绩信息重建。无匹配时可手动选择回放与谱面。进程访问使用只读 API。

## 构建

安装 [.NET 8 SDK x64](https://dotnet.microsoft.com/download/dotnet/8.0) 与 PowerShell。仓库的 `global.json` 指定最低 SDK 8.0.404，并允许使用更新的 .NET 8 feature band。

```powershell
./build.ps1 build       # Release 构建整个解决方案
./build.ps1 app         # 构建并启动 GUI
./build.ps1 test        # 不依赖 FFmpeg 的测试
./build.ps1 e2e         # 视频导出、错误/取消和逐帧滚动测试
./build.ps1 test-all    # 全部测试
./build.ps1 publish     # 自包含 Windows x64 发布
./build.ps1 publish-single # 单文件应用 EXE，附带 FFmpeg 和许可证的 Release ZIP
```

发布目录为 `artifacts/win-x64/`。分发整个目录，包括中文资源、FFmpeg、LICENSE、NOTICE、第三方声明及运行时许可文件。发布不包含测试、谱面、回放和歌曲音频。

单文件发布位于 `artifacts/win-x64-single/ManiaReplayMaster.exe`，已内置 .NET 运行时和中英文资源，无需另装 .NET 或携带 DLL。首次启动时，.NET 会把所需原生运行库解压到用户临时目录。分发时使用 `artifacts/ManiaReplayViewer-win-x64-single.zip`，其中包含应用 EXE、同目录的 `ffmpeg.exe` 及许可证文件。

两种发布命令均自动下载固定版本的 Gyan FFmpeg 9.0.2 essentials 静态构建，校验 SHA-256 后缓存到 `out/dependencies/ffmpeg/`，并保留上游说明和许可。仅构建时需要网络，用户运行时不需要下载。FFmpeg 通过独立进程执行；“单文件”指 .NET 应用本体，整个发布包包含两个 EXE。离线发布可事先将原始 `ffmpeg-9.0.2-essentials_build.zip` 放入该缓存目录。

脚本优先使用 `DOTNET_ROOT`，其次查找标准 x64 安装目录与 `PATH`。便携 SDK 可显式指定：

```powershell
./build.ps1 build -DotnetPath 'D:/Tools/dotnet8/sdk/dotnet.exe'
./build.ps1 build -Configuration Debug
```

也可直接使用标准 .NET 命令：

```powershell
dotnet build ManiaReplayMaster.sln -c Release
dotnet test ManiaReplayMaster.sln -c Release --filter 'Category!=EndToEnd'
dotnet publish src/ManiaReplayMaster.App -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

NuGet 默认从 nuget.org 恢复，依赖包不纳入源码管理。离线环境可准备完整的本地 NuGet feed，再使用 `-PackageSource <目录>`（直接调用 dotnet 时使用 `-p:RestoreSources=<目录>`）。自包含发布还需要对应 SDK 选择的 Windows runtime packs。

### 验证与诊断

默认测试保留判定边界、四组 Java 参考输出逐字段对照、渲染、文件命名和 FFmpeg 命令构造检查。需要外部 FFmpeg 的测试统一标记为 `Category=EndToEnd`，使用 `e2e` 单独运行；需同时安装 `ffmpeg` 与 `ffprobe`，可用 `MRM_FFMPEG` 指定 ffmpeg 路径。

```powershell
./build.ps1 cli -- dump-judge 'map.osu' 'replay.osr' --out out/judgements.json
./build.ps1 gamecheck -- memory32          # 构建并验证合成 x86 目标，无需启动游戏
./build.ps1 gamecheck -- live --poll 3     # 只读诊断已运行的 osu!stable
./build.ps1 gamecheck -- scoredb --root 'D:/osu!'
./build.ps1 appcheck                      # 交互桌面上的 GUI 判定冒烟检查
```

`memory32` 自动发布自包含 x86 测试进程，无需另装 x86 .NET。GUI 与真实游戏检查按需手动运行；截图写入 `out/appcheck/`。GitHub Actions 在 Windows 上执行构建、单元测试、合成内存检查、FFmpeg 测试和自包含发布，并保留构建产物。

### 发布 GitHub Release

推送 `v*` 标签会触发 **Windows release** 工作流，在 Windows 上通过构建、单元测试、合成内存检查和视频导出测试后，自动创建 GitHub Release 并生成发布说明：

```powershell
git tag v3.0.0
git push origin v3.0.0
```

Release 包含 `ManiaReplayViewer-win-x64.zip`（自包含目录版）和 `ManiaReplayViewer-win-x64-single.zip`（单文件应用版），均附带 FFmpeg 和许可证。包含 `-` 的标签（例如 `v3.0.0-rc.1`）会标记为预发布。

也可在 GitHub 的 **Actions → Windows release → Run workflow** 中填写已有标签手动发布；工作流始终检出该标签对应的源码。使用仓库自动提供的 `GITHUB_TOKEN` 发布，无需配置额外密钥。

## 项目结构

| 目录 | 内容 |
|---|---|
| `src/ManiaReplayMaster.Core` | 谱面、回放解析与判定 |
| `src/ManiaReplayMaster.Render` | GDI+ 音符长图渲染 |
| `src/ManiaReplayMaster.Encode` | FFmpeg 命令、进度与取消 |
| `src/ManiaReplayMaster.Game` | osu! 检测、数据库索引、回放匹配及内存读取 |
| `src/ManiaReplayMaster.App` | WPF 界面、本地化及设置 |
| `src/ManiaReplayMaster.Cli` | 无界面的解析与判定工具 |
| `tests/` | 回归测试与参考结果 |
| `fixtures/` | 测试谱面和回放，不含歌曲音频 |
| `tools/` | 可复用的 GUI 与游戏诊断工具 |
| `docs/FORMATS.md` | 数据库布局及内存读取维护说明 |

渲染器按时间窗口生成静态长图，再由 FFmpeg 按帧滚动、拼接并合成音频。滚动合成采用 YUV 4:4:4，最终输出常用的 YUV 4:2:0，避免奇数流速导致位移交替跳动。

## 许可证

本项目整体采用 **[GPL-3.0](LICENSE)**。选择该许可是为了兼容现有内存实现参考来源中的 GPL/LGPL 代码，并保留原 Java 项目的 Apache-2.0 权利声明。允许使用、修改与再分发；分发修改版或二进制时须遵守 GPL 的许可证及对应源代码提供要求。

原项目、tosu、gosumemory 和运行依赖的来源与许可见 [NOTICE](NOTICE) 和 [第三方声明](THIRD-PARTY-NOTICES.md)。测试谱面与回放保留原作者权利；FFmpeg 的许可取决于其具体构建配置。

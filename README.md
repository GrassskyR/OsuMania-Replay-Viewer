# Mania Replay Viewer

将 osu!mania、osu!taiko 和 Malody 的回放渲染为 MP4 视频的 Windows 工具。基于 C# / .NET 10，使用 Avalonia UI 框架，发布为 Native AOT 桌面应用。

## 功能

- **回放渲染**：拖入回放即可渲染，按判定结果对音符与按键着色。
- **联动 osu!stable**：在结算界面自动识别当前成绩，匹配本地回放与谱面。
- **参数自定义**：可调流速、分辨率、FPS 与编码器；支持仅计算判定（不导出视频）。
- **外部 FFmpeg**：可选择自己的 FFmpeg，检测 NVENC / QSV / AMF 硬件编码；内置精简版使用 libx264。
- **开箱即用**：内置中英文切换与配置记忆，按 `玩家 - 歌曲 [难度]` 格式自动命名导出文件。
- **窗口自适应**：默认约 900×620（逻辑尺寸），使用紧凑控件和日志行，根据当前显示器的可用区域和 DPI 缩放收缩；跨屏时自动避让屏幕边界，参数按可用逻辑宽度均分为三列、两列或一列，宽度低于 560 时路径标签移到输入框上方；最小窗口约 480×320，小窗口内容可滚动，底部操作按钮保持可见。

## 使用说明

支持 **Windows 10/11 x64**。Native AOT 包 `ManiaReplayViewer-win-x64-aot.zip` 解压后运行，无需安装 .NET；请保留同目录的全部本机 DLL、`ffmpeg.exe` 与许可证，支持离线导出。

1. 启动 `ManiaReplayViewer.exe`。
2. **加载回放**：拖入 `.osr` / `.mr` 文件。osu! 谱面会根据 MD5 自动定位，找不到时需手动选择 `.osu` / `.osz`（Malody 对应 `.mc` / `.mcz`）。
   - *自动联动*：打开 osu!stable 并开启界面的自动检测，进入成绩结算画面即可自动加载（仅支持 stable，不支持 lazer，且本地需有对应 `.osr` 文件）。
3. **导出**：调整参数后点击“渲染视频”。点击“仅判定”可跳过视频生成，直接查看判定统计。

### 补充说明

- **流速计算**：流速 `1` 对应 `60 像素/秒`（如默认流速 `20` = `1200 像素/秒`），调高 FPS 仅提升流畅度，不改变下落速度。
- **编码器选择**：默认为 `libx264`（CPU）。内置 FFmpeg 仅提供 libx264 视频编码，保留原有 AAC 音轨和静音 MP3。通过 FFmpeg 一行的“浏览”可选择外部 `ffmpeg.exe`，路径会保存；“恢复内置”清除此选择。菜单根据所选 FFmpeg 的编码器列表和实际试编码结果刷新，外部版本可提供 NVENC/QSV/AMF。`auto` 按 `NVENC → QSV → AMF → libx264` 选择；内置版直接使用 libx264。已保存的编码器/预设不会被后台检测改写；若不可用，日志提示重新选择。
- **配置文件**：保存在 `%LOCALAPPDATA%/ManiaReplayViewer/ui-settings.json`，崩溃日志见同目录的 `crash.log`。

## 构建与开发

需要 [.NET 10 SDK x64](https://dotnet.microsoft.com/download/dotnet/10.0)（global.json 锁定 10.0.402）、PowerShell；Native AOT 编译还需 Visual Studio 2022+ 的 C++ 桌面生成工具和 Windows SDK。打包/原生回归需 Python 3。精简 FFmpeg 从固定源码构建，需 MSYS2 MINGW64 的 GCC、zlib、pkgconf、make、diffutils、tar；源码和 NASM 自动下载并校验 SHA-256。用 `-DotnetPath <x64-dotnet.exe>` 指定 SDK，避免误用 PATH 中的 x86 SDK。

```powershell
./build.ps1 build          # Release 构建
./tools/ffmpeg/build-minimal.ps1 # 构建内置精简 FFmpeg；默认 MSYS2 位于 C:/msys64
./build.ps1 app            # Avalonia 开发入口（JIT）
./build.ps1 desktop        # app 的别名
./build.ps1 publish        # 全新 staging 原生发布及 ZIP (artifacts/win-x64-aot/)
./build.ps1 publish-aot    # publish 的别名
./build.ps1 app-aot        # 发布并启动 Native AOT GUI
./build.ps1 aotcheck       # 发布并运行原生 CLI/渲染/缓存/x86 内存回归
./build.ps1 appcheck-aot   # 对发布的原生 GUI 做 UI Automation 冒烟
./build.ps1 appcheck-headless # 无系统窗口的布局 / DPI / 编码器菜单回归
./build.ps1 appcheck       # 构建 Avalonia 并进行 JIT GUI 冒烟
./build.ps1 test           # 运行单元测试（无需 FFmpeg）
./build.ps1 e2e            # 运行视频导出等端到端测试（需 FFmpeg）
```

常用诊断与 CLI 命令：

```powershell
./build.ps1 cli -- dump-judge 'map.osu' 'replay.osr' --out out/judgements.json
./build.ps1 gamecheck -- live --poll 3     # 测试读取正在运行的 osu!stable
./build.ps1 gamecheck -- scoredb --root 'D:/osu!'
```

### 发布 Release

在 GitHub Actions 中手动运行 **Windows release**，选择分支并填写版本 tag（如 `v0.1.0`）。如果 tag 尚不存在，会构建本次运行所选分支的提交，通过测试后创建 tag 和 Release；如果 tag 已存在，则构建该 tag 对应的提交。推送 `v*` tag 也会自动触发发布。

### Native AOT 验收与排错

实施证据、完整体积对比及未覆盖环境见 [迁移结果](docs/NATIVE-AOT-MIGRATION-RESULTS.md)。WPF 入口已移除，`app` 启动 Avalonia，`publish` 生成 Native AOT 包；旧的 `publish-single` 命令已移除。Native AOT 并非只有一个 EXE：Skia、HarfBuzz 与 ANGLE 本机 DLL 属于 UI 依赖。缺少 DLL 时请重新完整解压；发布环境找不到 link.exe/Windows SDK 时安装 C++ 桌面生成工具。

`publish-aot` 只打包新 staging；目录、ZIP 和清单全部通过审计后一起替换，替换失败时回滚。旧输出整套保留在 `artifacts/win-x64-aot.previous-*/`，不会删除其中的用户导出视频。导出目录可在界面修改，建议放在发布目录之外。ZIP 用 DEFLATE level 6，文件清单/体积/SHA-256 写入同名 `.measurements.json`。

完整界面回归可运行 `./tools/appcheck/desktop-regression.ps1 -Exe "$PWD/artifacts/win-x64-aot/ManiaReplayViewer.exe"`，覆盖语言、原生文件选择、取消后重试和导出中关闭。它会暂时修改并恢复用户配置。无 .NET 验收可用 `./tools/appcheck/run-sandbox.ps1`；需要系统已提供 Windows Sandbox，网络在沙箱内关闭，结果写入 `out/aot-check/sandbox-*/`。`-PrepareOnly` 仅生成配置，不能算验收通过。

不占用桌面的布局与编码器菜单回归：`./build.ps1 appcheck-headless`。它通过 Avalonia Headless 加载实际界面，在内存中检查中英文布局、100%～300% DPI、参数分列、底部操作区和日志滚动，以及菜单展开、选择与关闭，不创建系统窗口或发送鼠标/键盘输入；选择绑定使用隔离测试状态。build/release CI 均包含此检查，并在发布 AOT 包后运行原生诊断与 UI Automation 冒烟。

视频测试的 `MRM_FFMPEG` 指向被测精简版；`MRM_FIXTURE_FFMPEG` 指向用于生成音乐/解码分析帧的完整版，`MRM_FFPROBE` 指定媒体检查工具。这些验证工具不进入发布包。精简实现结果见 [FFmpeg 结果](docs/FFMPEG-MINIMAL-RESULTS.md)。

## 项目结构

| 目录 | 说明 |
|---|---|
| `src/ManiaReplayMaster.Core` | 谱面、回放解析与判定 |
| `src/ManiaReplayMaster.Render` | 音符长图生成（GDI+） |
| `src/ManiaReplayMaster.Encode` | FFmpeg 裁剪合成与编码控制 |
| `src/ManiaReplayMaster.Game` | osu! 数据库解析与内存读取 |
| `src/ManiaReplayMaster.Desktop` | Avalonia 编译绑定 / Native AOT 界面 |
| `src/ManiaReplayMaster.Presentation` | 共用状态、设置、游戏适配与本地化 |
| `src/ManiaReplayMaster.Cli` | 命令行解析与判定工具 |
| `docs/FORMATS.md` | 数据库格式与内存偏移维护说明 |

## 致谢

- **[Mania-Replay-Master](https://github.com/Keytoyze/Mania-Replay-Master)**：本项目的核心谱面解析、判定与渲染逻辑均移植自该原版 Java 项目。
- **[tosu](https://github.com/tosuapp/tosu)**（及 [gosumemory](https://github.com/l3lackShark/gosumemory)）：osu!stable 内存读取的特征码与结构偏移参考自该项目。

## 许可证

采用 **[GPL-3.0](LICENSE)** 许可证。详细的开源协议与第三方依赖声明见 [NOTICE](NOTICE) 和 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

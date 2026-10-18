# Mania Replay Viewer

将 osu!mania、osu!taiko 和 Malody 的回放渲染为 MP4 视频的 Windows 工具。基于 C# / .NET 8 + WPF 构建。

## 功能

- **回放渲染**：拖入回放即可渲染，按判定结果对音符与按键着色。
- **联动 osu!stable**：在结算界面自动识别当前成绩，匹配本地回放与谱面。
- **参数自定义**：可调流速、分辨率、FPS 与编码器；支持仅计算判定（不导出视频）。
- **GPU 加速**：自动检测并使用 NVENC / QSV / AMF 硬件编码，失败自动回退 CPU。
- **开箱即用**：内置中英文切换与配置记忆，按 `玩家 - 歌曲 [难度]` 格式自动命名导出文件。

## 使用说明

支持 **Windows 10/11 x64**。直接下载 Release 包解压即可运行（已自带 .NET 运行时和 `ffmpeg.exe`，请勿删除同目录下的 FFmpeg）。

1. 启动 `ManiaReplayMaster.exe`。
2. **加载回放**：拖入 `.osr` / `.mr` 文件。osu! 谱面会根据 MD5 自动定位，找不到时需手动选择 `.osu` / `.osz`（Malody 对应 `.mc` / `.mcz`）。
   - *自动联动*：打开 osu!stable 并开启界面的自动检测，进入成绩结算画面即可自动加载（仅支持 stable，不支持 lazer，且本地需有对应 `.osr` 文件）。
3. **导出**：调整参数后点击“渲染”。勾选“仅判定”可跳过视频生成，直接查看判定统计。

### 补充说明

- **流速计算**：流速 `1` 对应 `60 像素/秒`（如默认流速 `20` = `1200 像素/秒`），调高 FPS 仅提升流畅度，不改变下落速度。
- **硬件编码**：默认 `auto` 会在导出前按 `NVENC → QSV → AMF → CPU (libx264)` 顺序实测可用编码器。随包附带的 FFmpeg 9.0.2 要求 NVIDIA 驱动 **610.00+** 才能启用 NVENC，旧驱动请更新驱动或自行替换旧版 FFmpeg。
- **配置文件**：保存在 `%LOCALAPPDATA%/ManiaReplayViewer/ui-settings.json`，崩溃日志见同目录的 `crash.log`。

## 构建与开发

需要 [.NET 8 SDK x64](https://dotnet.microsoft.com/download/dotnet/8.0)（8.0.404+）和 PowerShell。发布脚本会自动下载并缓存 FFmpeg 9.0.2。

```powershell
./build.ps1 build          # Release 构建
./build.ps1 app            # 构建并启动 GUI
./build.ps1 test           # 运行单元测试（无需 FFmpeg）
./build.ps1 e2e            # 运行视频导出等端到端测试（需 FFmpeg）
./build.ps1 publish        # 发布自包含目录 (artifacts/win-x64/)
./build.ps1 publish-single # 发布单文件版及打包 ZIP (artifacts/win-x64-single/)
```

常用诊断与 CLI 命令：

```powershell
./build.ps1 cli -- dump-judge 'map.osu' 'replay.osr' --out out/judgements.json
./build.ps1 gamecheck -- live --poll 3     # 测试读取正在运行的 osu!stable
./build.ps1 gamecheck -- scoredb --root 'D:/osu!'
```

### 发布 Release

在 GitHub Actions 中手动运行 **Windows release**，选择分支并填写版本 tag（如 `v0.1.0`）。如果 tag 尚不存在，会构建本次运行所选分支的提交，通过测试后创建 tag 和 Release；如果 tag 已存在，则构建该 tag 对应的提交。推送 `v*` tag 也会自动触发发布。

## 项目结构

| 目录 | 说明 |
|---|---|
| `src/ManiaReplayMaster.Core` | 谱面、回放解析与判定 |
| `src/ManiaReplayMaster.Render` | 音符长图生成（GDI+） |
| `src/ManiaReplayMaster.Encode` | FFmpeg 裁剪合成与编码控制 |
| `src/ManiaReplayMaster.Game` | osu! 数据库解析与内存读取 |
| `src/ManiaReplayMaster.App` | WPF 界面 |
| `src/ManiaReplayMaster.Cli` | 命令行解析与判定工具 |
| `docs/FORMATS.md` | 数据库格式与内存偏移维护说明 |

## 致谢

- **[Mania-Replay-Master](https://github.com/Keytoyze/Mania-Replay-Master)**：本项目的核心谱面解析、判定与渲染逻辑均移植自该原版 Java 项目。
- **[tosu](https://github.com/tosuapp/tosu)**（及 [gosumemory](https://github.com/l3lackShark/gosumemory)）：osu!stable 内存读取的特征码与结构偏移参考自该项目。

## 许可证

采用 **[GPL-3.0](LICENSE)** 许可证。详细的开源协议与第三方依赖声明见 [NOTICE](NOTICE) 和 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

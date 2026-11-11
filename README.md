# Mania Replay Viewer

将 osu!mania、osu!taiko 和 Malody 的回放渲染为 MP4 视频的 Windows 工具。基于 C# / .NET 10、Avalonia 12.1.5 和 AtomUI 6.2.13，采用暗色紧凑界面，支持自定义渲染参数、硬件编码、中英文界面及 osu!stable 成绩自动识别。

## 运行方式

支持 Windows 10/11 x64。解压 `ManiaReplayViewer-win-x64-aot.zip`，运行 `ManiaReplayViewer.exe`，无需安装 .NET。请保留包内的 DLL、`ffmpeg.exe` 和许可证文件。

1. 拖入 `.osr` / `.mr` 回放文件。
2. 确认谱面路径；未自动匹配时，选择 `.osu` / `.osz` 或 Malody 的 `.mc` / `.mcz`。
3. 调整分辨率、FPS、流速和编码器，点击“渲染视频”；“仅判定”可查看判定统计。

内置 FFmpeg 使用 CPU 编码（libx264）。需要 NVENC / QSV / AMF 时，可在界面选择支持对应编码器的外部 FFmpeg。osu! 自动识别仅支持 stable，需本地存在对应回放和谱面。

## 构建方法和要求

开发构建需要 Windows、PowerShell 7 和 [.NET SDK 10.0.402 x64](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
./build.ps1 doctor         # 检查开发环境
./build.ps1 build          # Release 构建
./build.ps1 app            # 构建并运行开发版
```

便携 SDK 可通过 `-DotnetPath <dotnet.exe路径>` 或环境变量 `DOTNET_ROOT_X64` 指定。

生成 Native AOT 发布包还需要：

- Visual Studio 2022 或更新版本的 C++ 桌面生成工具及 Windows SDK。
- Python 3。
- MSYS2 MINGW64，安装以下依赖：

```bash
pacman -S --needed make diffutils tar mingw-w64-x86_64-gcc mingw-w64-x86_64-zlib mingw-w64-x86_64-pkgconf
```

```powershell
./build.ps1 doctor -Profile Publish # 检查发布环境
./build.ps1 publish                # 构建内置 FFmpeg 并生成 Native AOT ZIP
```

MSYS2 默认路径为 `C:/msys64`，可通过 `-MsysRoot <目录>` 指定。首次构建内置 FFmpeg 需要联网下载源码和 NASM。发布目录为 `artifacts/win-x64-aot/`，压缩包为 `artifacts/ManiaReplayViewer-win-x64-aot.zip`。

## 致谢

- [Mania-Replay-Master](https://github.com/Keytoyze/Mania-Replay-Master)：核心谱面解析、判定与渲染逻辑移植自该 Java 项目。
- [tosu](https://github.com/tosuapp/tosu) 和 [gosumemory](https://github.com/l3lackShark/gosumemory)：osu!stable 内存读取的特征码与结构偏移参考。

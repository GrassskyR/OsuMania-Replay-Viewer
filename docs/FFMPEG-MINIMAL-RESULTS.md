# 精简 FFmpeg

2026-10-20，分支 `codex/native-aot-migration`。

已实现：

- 固定 FFmpeg 9.0.2、x264、LAME 源码及 SHA-256，MSYS2 静态构建，保留汇编优化。
- 实际视频编码器仅 libx264；保留 AAC/MP3，去掉 NVENC/QSV/AMF。wrapped_avframe 仅为内部帧适配。
- `Config` 和 `FfmpegCommandBuilder` 未修改，原 MP4/AAC、静音 MP3、码率/采样率/声道选择、预设和图像处理参数保留。
- Avalonia/WPF 都增加 FFmpeg 路径选择和恢复内置；验证后保存，错误路径不替换有效选择，导出期间禁用切换。
- 编码器检测先读取选中工具的 `-encoders`，再试编码确认可用性；刷新候选并保留当前编码器/预设，失效选择提示用户修改。
- GUI 探测和导出使用同一 FFmpeg；旧外部路径失效时明确报错。关闭窗口会取消检测并终止相关子进程。
- 打包、build/release CI 接入精简构建；完整版 FFmpeg/ffprobe 只用于测试素材和帧分析，不进入产品包。

最终产物：

| 文件 | 大小 |
|---|---:|
| `artifacts/ManiaReplayViewer-win-x64-aot.zip` | 20,878,247 字节（19.91 MiB） |
| 解压后完整目录 | 49,966,237 字节（47.65 MiB） |
| `ManiaReplayViewer.exe` | 17,944,064 字节（17.11 MiB） |
| 内置 `ffmpeg.exe` | 11,447,808 字节（10.92 MiB） |

- ZIP SHA-256：`284bb79a5a30b5ce99a8dec1033edd073049245884fdd3cb95514d3c52c729d4`。
- FFmpeg SHA-256：`df27586b2e4a64742dfa89961d80f39b8eb8ec8eccafb6a55be538a4cd7d5141`。
- 实际内置编码器检测：`libx264`；FFmpeg 仅导入 Windows 系统 DLL，无额外工具链 DLL。
- Release 构建和 25 项单元/参考检查、7 项视频端到端检查通过。
- 最新精简 FFmpeg 的 Native AOT 29 项检查通过，覆盖 Mania/Taiko、59/60/120fps、分段拼接、真实 BGM/AAC、静音 MP3、取消及进程清理。
- 无系统窗口的真实界面 6 项检查通过，覆盖菜单、中文/空格路径、外部 FFmpeg 切换、保存、错误路径保留原选择和恢复内置。
- 最终 AOT 发布、ZIP CRC/文件集合/全部 SHA-256 与清单一致；包中不含 ffprobe、ffplay、Headless 或 xUnit。产品无 IL/AOT 警告；现有 SharpCompress NU1902 未在此任务修改。
- 工作流 actionlint、PowerShell/Bash 语法和 `git diff --check` 通过；远程 GitHub Actions 尚未执行。
- 日志保存在 `out/ffmpeg-minimal-check/`。未生成对照包，未启动系统 GUI 或操控桌面输入。

# 精简 FFmpeg

- 内置视频编码只留 libx264，移除 NVENC/QSV/AMF 及其专用依赖。
- 保留 AAC、MP3 音频编码、音乐解码、PNG 输入及导出需要的滤镜/封装。
- 保持现有导出命令、默认编码参数和用户偏好。
- 界面提供选择外部 FFmpeg 和恢复内置，路径保存到现有用户配置。
- 编码器菜单读取当前 FFmpeg 的能力并试编码；切换后刷新候选。外部版可以使用现有硬件编码支持。
- 将精简源码构建接入打包和 CI，提供完整可运行 ZIP。

实现结果见 [FFMPEG-MINIMAL-RESULTS.md](FFMPEG-MINIMAL-RESULTS.md)。

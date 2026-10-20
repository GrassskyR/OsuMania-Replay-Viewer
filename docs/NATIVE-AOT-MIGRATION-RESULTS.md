# Native AOT 迁移实施与验证

日期：2026-10-20。分支：`codex/native-aot-migration`，起点 `e1ec981`。原计划是用户提供的未跟踪文件，保留原文。

## 当前结论

完整 Avalonia 桌面入口已实现并成功 Native AOT 发布，原生 GUI 实际完成判定、导出和生命周期回归。Core、Render/Encode、Game 分阶段验证后才迁移完整界面。完整 ZIP 同口径由 95.597 MiB 降到约 52.505 MiB，减少约 45.1%。

**整体验收尚未完成**：没有可用的无 .NET 干净 Windows 环境；100%/200% DPI、实际系统文件拖放、真实游戏退出重连及 GUI 成绩自动填充/候选选择仍待验收。默认 `app/publish` 按计划继续使用 WPF；新入口通过 `desktop/app-aot/publish-aot` 使用。没有推送、创建 tag 或发布 GitHub Release。

| 阶段 | 状态 | 主要证据 |
|---|---|---|
| 0 工具链与基线 | 通过 | 原 .NET 8 构建、30 项测试、111 项合成内存检查、全新旧版 staging |
| 1 Core/CLI | 通过 | .NET 8/10 原生发布；真实 Mania golden、LZMA、MC/MR/Taiko |
| 2 Render/Encode | 通过（硬件覆盖有边界） | 27 项原生行为/集成检查；AMF 实机；NVENC/QSV 本机不支持 |
| 3 Game | 合成检查通过，live 部分通过 | memory32 111/111；all 165/167，旧版同样失败；live 19/20，旧版同样失败 |
| 4 完整 UI | 已实现，部分环境/交互待验收 | 原生判定、59fps 导出、语言/文件选择/取消/关闭回归通过；150% DPI 截图 |
| 5 打包/CI/最终验收 | 包已生成，干净环境待验收 | 原生 PE 审计、许可证、ZIP/哈希清单；CI 已修改，未远程执行 |

## 阶段 0：工具链、依据和基线

- Windows 11 x64；没有适用的 AGENTS.md。先检查 Git 状态，再创建独立分支。
- 原 SDK：`D:/Tools/dotnet8/sdk/dotnet.exe` 8.0.404。PATH 中是旧 x86 SDK，不用于发布。
- 最终 SDK：10.0.402 x64，runtime 10.0.13，安装在 `D:/Tools/dotnet10-aot`。官方 ZIP 的 SHA-512 与官方 releases.json 一致；没有修改系统 PATH。NuGet 大包下载停滞时通过用户允许的 `127.0.0.1:7890` 代理下载至临时本地 feed，未改仓库包源。
- MSVC 14.29.30133 / VS BuildTools 2019 + Windows SDK 10.0.19041.0 在本机实际完成原生链接。README/CI 使用官方要求的 VS2022+ C++ 工具链。
- 先在 .NET 8 实测核心/绘图 AOT，再独立升级 SDK/TFM 到 .NET 10 LTS 并重复验证。逐个业务库设置 IsAotCompatible，入口通过参数启用 PublishAot；WPF、测试和 x86 helper 不受全局 AOT 属性影响。

官方依据：

- [.NET Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)：按 RID 原生发布并实际运行产物。
- [.NET 支持周期](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)：选择 .NET 10 LTS。
- [Avalonia Native AOT](https://docs.avaloniaui.net/docs/deployment/native-aot)：锁定 Avalonia 12.1.4、编译 XAML/绑定；12.0 起不需要禁用内置 COM。没有旧版 COM 开关或无差别保根。
- Render 锁定 System.Drawing.Common 10.0.0，保留 GDI+ 算法。UI 传递依赖 SkiaSharp 3.119.4、HarfBuzzSharp 8.3.1.3、ANGLE 2.1.27548.20260419。

旧版基线使用 .NET 8 SDK 执行 `build.ps1 build`、`test-all`、`gamecheck -- memory32`：构建通过，Core 7/7 + RenderEncode 23/23（含 FFmpeg E2E）、memory32 111/111。全新旧版自包含压缩单文件目录为 `out/aot-check/baseline`。

基线日志：`out/aot-check/logs/baseline-{build,test,memory,publish,package}.log`。官方文档副本：`out/aot-check/avalonia-aot.{html,txt}`、`dotnet-support.html`。下文日志默认位于 `out/aot-check/logs/`。`out/`、`artifacts/` 均忽略，只存在于执行机器；文末保留复验命令。

## 阶段 1：Core/CLI

- Malody 使用 MalodyJsonContext，保留数字字符串、大小写兼容和嵌套模型；CLI 快照使用 CliJsonContext，保留原 schema、空值和编码选项。
- CLI 普通构建仍可用；原生发布使用 PublishAot=true、OptimizationPreference=Size，没有强制 invariant globalization。
- SharpCompress 0.37.2 的真实 OSR LZMA 解压在原生 EXE 中通过，没有因为依赖包含 ZstdSharp 而重写解压器。
- 原生 CLI 执行 help、所有 fixture 的 dump-map/dump-replay、benchmark/dump-judge。四组真实 Mania 快照在 .NET 8 与 JIT 字节一致；.NET 10 原生结果逐字段匹配 tests/golden，浮点容差 1e-10。
- 补充可复现 `aot-minimal.mc/.mr`、`aot-taiko.osu/.osr`，含 LN、数字字符串/大小写和 Taiko 音符。生成器 `tools/aotcheck/generate-fixtures.py`；合成格式解析/判定通过，.NET 8/10 原生快照一致。

复验：`tools/aotcheck/verify-cli.py <published-mrm.exe>`。日志：`cli8-publish.log`、`cli10-{publish,run}.log`、最终 `final-aotcheck.log`。

## 阶段 2：Render/Encode

- 移除 Render 的 UseWindowsForms，使用独立 System.Drawing.Common。原生 PNG/视频实际可靠，故保留 Renderer/RgbPngWriter 的像素、坐标、字体、LN、Taiko 旋转和节奏；没有更换编码方案或降低导出质量。
- 初次原生入口验证奇数宽度 541x3960/RGB/黑色背景及 Carnation 47 段导出；后续直接调用既有 RendererTests、ScrollCadenceTests、EncoderFailureTests 等行为测试，避免反射测试发现。
- 最终 **27 项原生行为/集成检查通过**：Mania 60/120 FPS、PNG 通道/几何/LN、Taiko 圆环/背景/alpha、59/60/120 FPS 解码逐帧节奏/总帧数/分段合并、BGM/atempo/adelay/时长、失败产物拒绝、预取消及正在运行 FFmpeg 的取消/清理、硬件失败后 CPU 重试、设置/文化及缓存。
- MC/MR 与 Taiko 在 59/120 FPS、有/无 BGM、分段/单次导出组合实际生成 MP4，检查宽高、音视频流和时长。保留无 BGM 的既有静音音轨行为。测试路径含中文和单引号。
- 最终 `build.ps1 aotcheck` 使用**随包同一 FFmpeg**（MRM_FFMPEG 指向 `artifacts/win-x64-aot/ffmpeg.exe`）；早期 render10-suite/native-suite 使用 C:/ffmpeg、二进制不同，以 `final-aotcheck.log` 为最终证据。

| 编码路径 | 本机实际结果 |
|---|---|
| libx264 | 实际导出通过 |
| AMF | 探测 exit 0，auto 选择 h264_amf；硬件失败/CPU 重试行为通过 |
| NVENC | 驱动 API 13.0，随包 FFmpeg 要求 13.1 / 驱动 610.00+；未记为硬件成功 |
| QSV | MFX implementation unsupported；未记为硬件成功 |
| 选择逻辑 | NVENC → QSV → AMF → CPU、无 GPU 回退及显式编码器/预设检查通过，顺序未改 |

## 阶段 3：Game

- SongsIndexCache/ReplayHeaderIndexCache 使用 GameJsonContext，保留字段/schema、路径、旧缓存和损坏重建。没有机械替换 DllImport/结构封送。
- 原生 x64 gamecheck 配合 .NET 10 x86 helper：**memory32 111/111**。覆盖 32/64 位指针、BOOL/字符串、SafeHandle、内存区域和模块、跨区读取/越界拒绝；只读取目标内存。
- 原生 all：165 通过、2 失败。实际扫描 5,809 个回放 header、24,842 张谱面，读取旧缓存并再次复用、数据库、回放匹配、目录监听。
- all 失败：当时 osu!.db trailer=1，检查只接受 0/5；“warm scan 必须比 cold 快”的计时断言波动。未修改的 .NET 8 harness 重跑同样 165/167，见 `game8-all.log/game10-all.log`；没有改业务逻辑来消除失败。
- 初次 live 无游戏时明确 SKIP；随后启动本机 osu!stable，`final-game-live.log` 实测 **19 通过、1 失败**。已读到 Results/Ok，watcher 三次 10/10 特征码、谱面 MD5 与文件一致，匹配玩家 GrassMe、989322 分/1099 连击、命中统计及时间完全一致的本地回放（100% 匹配）。
- live 失败是最初独立原始链检查玩家名不可读，后续 snapshot 已有玩家名。再次执行时为选曲页（原始 status=5），新旧两版都 19/20、玩家链无当前成绩；见 `final-game-live-repeat.log/final-game8-live.log`。不把整个 live 检查记为通过，也不凭单次观测断言时序问题已修复。
- GUI 自动填充/候选选择及真实退出重连未完成。Computer Use 观察到游戏为选曲页，尝试打开本地成绩时用户按 Escape 停止，随即停止全部窗口操作。本次自行启动的 osu! PID 47132 因此留在运行状态，没有强制结束。

## 阶段 4：完整 Avalonia 界面

- 新建 Desktop，全部数据绑定使用 x:DataType/编译绑定；先最小原生窗口确认中文输入和依赖，再迁移完整六行密集布局。
- 抽离 Presentation，共用 MainViewModel、GameBridge、UiSettings、Localizer、资源和 CrashLog；WPF Brush 改颜色字符串。WPF 正常构建，没有复制两份业务状态。
- 已实现文件选择/拖放、交换/定位、候选、自动检测/自动开始/内存开关、参数与可编辑编码器、进度日志、判定/导出/取消、打开输出目录、语言/配置、UI 调度/异常记录和关闭收尾。
- 最小原生窗口 UIA 输入并读取中文路径；完整进程模块检查没有 coreclr/clrjit/WPF，最终包也无托管 PE。
- 原生 GUI 判定：2711 notes / 2515 actions / 75.89%，判定数量 `805, 776, 585, 251, 16, 279` 匹配 golden。见 `desktop-judge.log`。
- 原生 GUI 59 FPS、180x320 导出到中文/单引号目录，accuracy/视频/无新增 crash.log 检查通过。见 `desktop-render.log`。
- `desktop-regression.ps1` 七项 PASS：英文即时切换、中文即时切换、原生文件选择/中文路径、交换/键盘焦点、导出中取消/无 FFmpeg 遗留、取消后再次导出、导出中关闭/无子进程遗留。见 `desktop-regression.log`。
- 150% DPI 判定/导出截图已检查，中文、参数和日志可读：`out/aot-check/desktop-{judge,render}.png`。**100%/200% DPI、最小窗口缩放、实际系统 OLE 文件拖放尚未验收**，实现不等于验证通过。
- 旧配置路径、默认值、未知字段、中文/系统文化通过原生检查。测试曾改宿主语言，收尾恢复原 zh-Hans，保留 speed/codec/preset/未知字段。

已解决的迁移问题：

1. XAML 初始化触发 SelectionChanged 早于具名字段赋值：InitializeComponent 后再订阅语言事件。
2. WPF 监听 Item[]，Avalonia 编译索引器监听 Item：Localizer 同时通知两者，真实双语切换重跑通过。
3. Avalonia 12 编辑编码器采用 AutoCompleteBox；拖放用 DataTransfer，文件选择用 StorageProvider。
4. 关闭先停止检测、取消并等待工作、释放游戏联动，再真正关闭；导出中关闭及再次开始检查通过。
5. AOT 后期仍复制主 PDB：CopyOutputSymbolsToPublishDirectory=false，包审计拒绝 PDB。
6. 诊断入口 IsTestProject=false，避免 solution test 将其当成反射发现的测试宿主。

## 警告边界

- 产品 CLI、Game、完整 Desktop 原生发布无 IL/AOT 警告。未新增 AOT/裁剪 NoWarn、UnconditionalSuppressMessage、全程序集保根或关闭反射检查；仓库原有的 CA1416 平台分析抑制保留。
- 原生诊断工具复用 xunit.assert 2.9.2，有 IL2026/2060/2065/2067/2070/2072/2075/2077/3050，定位在 EqualityAsserts/AssertEqualityComparer、CollectionTracker、ArgumentFormatter、AssertHelper 的反射比较/失败消息格式化。27 项已调用检查通过，但不保证其他反射断言或所有失败格式化路径可用。TrimmerSingleWarn=false 保留详细输出，见 `final-aotcheck.log/native-suite-publish.log`。产品不引用或打包 xUnit。
- 已有 SharpCompress 0.37.2 NU1902 / GHSA-6c8g-7p36-r338 保持可见；实际 LZMA 验证通过不代表漏洞已解决，此次未混入独立的依赖安全升级。
- 未测量同场景重复启动时间/空闲内存，不宣称性能提升。

## 阶段 5：发布、CI 与体积

- `package-aot.ps1` 新建 staging，原生发布、调用原固定版本/哈希 FFmpeg 流程、收集实际 NuGet 图的 LICENSE/NOTICE，审计后生成 ZIP。目录/ZIP/清单验证后一起替换，移动失败时回滚；旧输出整套保留在 `artifacts/win-x64-aot.previous-*/`，不删除用户导出文件。
- `package-audit.py` 检查 EXE/DLL 的 PE CLR header，拒绝托管 PE、coreclr/clrjit、deps/runtimeconfig、PDB、MP4、ffprobe/ffplay，生成大小/SHA-256 清单。
- 完整包有主 EXE、Skia/HarfBuzz/ANGLE、本机 FFmpeg、全部项目/第三方许可证，无完整 CLR。FFmpeg 仍为原固定 9.0.2/哈希二进制，离线完整携带，不定制、不首次启动下载。
- build/release workflows 已选择 .NET 10、Python、MSVC，构建/测试后执行无窗口编码器菜单回归，发布 AOT，运行原生 CLI/Render/Game 与基本 UIA 冒烟，上传明确命名 AOT ZIP/清单，Release 同时附带清单；失败时也上传可用诊断输出。**工作流仅本地静态检查、对应后台命令本机验证，未远程执行 CI 或发布**；完整 GUI 回归保留为本地命令。
- 本机无 WindowsSandbox.exe，也未找到可用 VM/容器。`run-sandbox.ps1 -PrepareOnly` 已生成网络禁用、包/fixtures/脚本只读映射配置；sandbox-validation 会检查无现代 .NET，再做 GUI 判定/导出/设置持久化。**只生成配置，未运行，不能记为无 .NET 验收通过**。没有卸载宿主运行时或改全局设置来模拟。

体积采用同一 FFmpeg、同一 Python ZIP DEFLATE level 6、全新无视频目录；不用夹带 MP4 的 119.109 MiB 旧包比较。1 MiB=1,048,576 bytes。

| 版本 | 主 EXE | 完整目录 | ZIP |
|---|---:|---:|---:|
| 原 .NET 8 WPF，压缩单文件自包含 | 63.785 MiB | 164.620 MiB | 95.597 MiB |
| .NET 10 Avalonia Native AOT（含 UI/编码器修正） | 17.083 MiB | 约 137.203 MiB | 约 52.505 MiB |

完整 ZIP 减少约 **45.1%**，目录减少约 **16.7%**。最终 README/许可证文字更新也计入同名 `.measurements.json`，以该清单逐字节统计/SHA-256 为准。

当前精确统计：AOT EXE 17,912,320 / 目录 143,868,459 / ZIP 55,055,368 bytes；旧版分别 66,883,634 / 172,616,700 / 100,240,909 bytes。ZIP 减少约 45.0769%。当前 ZIP SHA-256：`8697bedc516e7ae48b19cbfe2cafed0b471aaa3c67947fc53821755786eaa80d`。

| AOT 主要文件 | bytes |
|---|---:|
| ManiaReplayViewer.exe | 17,912,320 |
| ffmpeg.exe | 105,423,872 |
| libSkiaSharp.dll | 11,628,896 |
| av_libglesv2.dll | 5,394,096 |
| libHarfBuzzSharp.dll | 1,816,088 |

交付：`artifacts/win-x64-aot/`、`artifacts/ManiaReplayViewer-win-x64-aot.zip`、同名 `.measurements.json`。FFmpeg 仍占约 100.540 MiB 目录体积。

## 复验命令与剩余验收

在仓库根目录按顺序执行；不要并行 publish 共用 bin/obj，以免输出互相覆盖。FFmpeg/ffprobe 在 PATH，ffprobe 只用于验证。

```powershell
$sdk = 'D:/Tools/dotnet10-aot/dotnet.exe' # 其他机器用其 .NET 10 x64 SDK
./build.ps1 build -DotnetPath $sdk
./build.ps1 test-all -DotnetPath $sdk
./build.ps1 publish-aot -DotnetPath $sdk
$env:MRM_FFMPEG = "$PWD/artifacts/win-x64-aot/ffmpeg.exe"
./build.ps1 aotcheck -DotnetPath $sdk
./build.ps1 appcheck-aot -DotnetPath $sdk
./tools/appcheck/desktop-smoke.ps1 -Render -Fps 59 -Width 180 -Height 320
./tools/appcheck/desktop-regression.ps1 -Exe "$PWD/artifacts/win-x64-aot/ManiaReplayViewer.exe"
./artifacts/aot-tools/game/gamecheck.exe live --poll 3
python tools/package-audit.py artifacts/win-x64-aot artifacts/ManiaReplayViewer-win-x64-aot.zip --baseline out/aot-check/baseline
./tools/appcheck/run-sandbox.ps1 # 需可用 Windows Sandbox；检查 result.json/日志
```

最终全量构建/30 项测试/原生检查证据：`final-build.log`、`final-test.log`、`final-aotcheck.log`；GUI 证据如上。收尾只改文档/脚本说明，PowerShell/Python 语法、CI YAML 结构与 git diff --check 通过，重新原生发布无 IL/AOT 警告；见 `final-package.log/final-size-audit.log`。ZIP CRC、文件集合和逐文件 SHA-256 复核通过，产品无 xUnit。

首轮用户停止 Computer Use 后没有继续操控 GUI；当时的 GUI 证据来自同一产品源码的先前发布。首轮收尾重新链接的 EXE 与先前包大小相同、.text 段完全相同，但有 PE 时间戳和 .rdata 差异。此记录描述首轮产物，后续 UI 修正见下节，不宣称当前包已经重跑完整 UI 检查。不把重复普通测试代替缺失的环境验收。

剩余工作：无 .NET 的 Windows 10/11 x64 实机/VM 运行完整包和原生文件选择，补 100%/200% DPI/缩放/实际拖放、真实游戏 GUI 填充/候选选择及退出重连，运行远程 CI；全部确认后才能切换默认入口、勾选计划全部最终验收项。

## 后续：UI 对齐修正（2026-10-20）

用户手工反馈基本功能正常，但新界面元素位置不齐。本轮读取 WPF 原布局/样式，打开旧版与 AOT 窗口对照，确认默认 Slider 的行高撑大宽高输入框、流速数字靠顶，以及参数跨行共用列造成左右错位。

- 保留六行主布局与功能分区，使用与 WPF 默认字号一致的 Segoe UI 12 和布局取整。
- 输入框、按钮、下拉框、编码器编辑器、滑块统一 32 DIP 行高/垂直居中；标题与语言/游戏状态居中，状态点居中。
- 文件输入两行统一行距和右边缘，检测成绩文字居中。
- 参数使用一致的标签/编辑器列：宽度与编码器、以及高度与预设分别对齐左边缘；流速数值放回滑块同行并居中，FPS/输出目录标签共用左边缘。
- 底部结果文字限制在剩余宽度并省略，避免较长结果把动作按钮挤出窗口。

验证：Desktop Release 构建与 Native AOT 发布通过，无产品 IL/AOT 警告；日志 `ui-alignment-build.log/ui-alignment-publish.log`。新 staging 包的原生 PE 审计、ZIP CRC、文件集合、逐文件 SHA-256 校验通过；更新上方体积与哈希。此次只修改布局/样式，未重复业务算法测试。

用户再次按 Escape 停止 Computer Use 后，停止全部窗口操作，因此**修改后的截图、缩放和交互复核未执行**。对照用旧窗口未强制关闭；请关闭旧窗口、重新运行当前 `artifacts/win-x64-aot/ManiaReplayViewer.exe` 查看修正。

## 后续：编码器下拉选择与默认值（2026-10-20）

- 用户要求后续不再使用 Computer Use。本轮只编辑文件和运行后台/无窗口检查，没有操控任何桌面窗口、鼠标或键盘。
- 编码器编辑框补上可点击下拉箭头，完整显示候选列表；选择条目会提交 Codec 并收起菜单，仍允许输入自定义编码器名称。
- 启动后后台逐个试编码 libx264/NVENC/QSV/AMF，将实际可初始化的硬件编码器加入菜单，不因第一个 GPU 成功而漏掉其他 GPU。auto 的既有选择顺序不变；列表变化不覆盖当前编码器/预设，关闭时取消检测并清理子进程。WPF 参照入口同步使用检测结果。
- 无配置或缺少 codec 时默认 libx264；保留已保存的显式选择（含 auto/自定义编码器）。Core 原有默认就是 libx264，此次修复 UiSettings 把它覆盖为 auto 的行为。
- 新增后台 `tools/appcheck/headless` 回归入口，Avalonia.Headless 仅用于测试，不进入产品包。它加载实际 MainWindow/XAML，在内存中触发下拉/选择/关闭；测试选择绑定替换成隔离对象，不写宿主的编码器偏好。

验证结果：

- solution Release 构建通过（`encoder-dropdown-build.log`）。编码器相关 5 项测试通过，包括同时保留多个可用 GPU 和剔除不可用项（`encoder-dropdown-tests.log`）。
- 发布的原生诊断 EXE `aotcheck.exe . --encoders --require-native` 通过默认值/旧配置/未知字段/列表不重复/保留编码器及预设检查；随包 FFmpeg 实机检测为 **libx264、h264_amf**。NVENC 仍受 API 版本限制，QSV 仍不支持 MFX；日志 `encoder-dropdown-native-{publish,run}.log`，诊断 xUnit 警告边界不变。
- `dotnet run --project tools/appcheck/headless -c Release` 通过：实际下拉箭头展开、弹出列表包含检测结果、选择更新绑定并关闭菜单、箭头再次收起、关闭取消后台检测。日志 `encoder-dropdown-headless.log`。这是无系统窗口的框架交互检查，不冒充实机 UIA/DPI 验收。
- 重新全新 staging Native AOT 发布，无产品 IL/AOT 警告；日志 `encoder-dropdown-package.log`。产品未引用 Avalonia.Headless/xUnit，原生 PE/许可证及 ZIP 文件审计随发布执行，末次 ZIP CRC/文件集合/逐文件 SHA-256 复核与 git diff --check 通过。上方体积表和哈希已更新。

## 提交前整理与打包/CI 复核（2026-10-20）

- 当前分支 `codex/native-aot-migration`；提交包括迁移实现、UI/编码器修正、计划与本 RESULTS。只创建本地提交，不推送、不创建 tag/Release。
- 核查旧 staging、五份旧 AOT 输出、重复 CLI/Render/Game/Desktop 发布、临时 headless 工程、NuGet feed 和已安装 SDK 的下载 ZIP。递归删除被自动审批策略拦截，因此将确认的 19 项临时路径集中归档至忽略目录 `out/cleanup-archive/20261020-133149/`；这是整理归档，未声称释放磁盘空间。清单见 `out/aot-check/logs/cleanup-archive.json`。最终包、baseline、日志/截图、已有视频/其他发布目录保留；未关闭用户运行中的程序。
- 修复发布一致性：先在 staging 内生成并验证 ZIP/清单，再保存旧目录/ZIP/清单并替换；移动失败则恢复旧的一套。审计检查改为显式异常，Python `-O` 下也不能跳过。既有 FFmpeg 固定版本/哈希不变。
- 新增 `build.ps1 appcheck-headless`，build/release CI 均调用；超时调为 60 分钟，容纳 GUI/三个诊断入口的 AOT 编译及原有 WPF 发布。上传诊断输出，Release 包含 AOT 体积/哈希清单。默认入口仍保留 WPF，待剩余验收后再切换。

本轮验证（日志均在 `out/aot-check/logs/`）：

| 检查 | 结果与证据 |
|---|---|
| solution Release build | 通过，`precommit-build.log`；已有 SharpCompress NU1902 仍可见 |
| 单元/参考 + 视频端到端 | 24 + 7 项通过，`precommit-tests.log` / `precommit-e2e.log` |
| 无系统窗口菜单回归 | 5 项通过，`precommit-headless.log`，未操控桌面/鼠标/键盘 |
| 发布替换失败回滚 | 正常替换及六个移动步骤分别注入失败全部通过，`precommit-package-rollback.log` |
| 审计拒绝非法包 | Python `-O` 下正常包通过，缺文件/托管 PE/MP4/PDB/运行时元数据/无效 PE 六类拒绝，`precommit-package-audit.log` |
| 工作流/脚本静态检查 | actionlint 1.7.7 通过（下载校验官方 SHA-256），`precommit-actionlint.log`；2 个 YAML、24 个 workflow run PowerShell 片段、8 个 PS 脚本、3 个 Python 脚本语法通过 |
| 实际 AOT 打包 | 通过，无产品 IL/AOT 警告，`precommit-package.log` |
| CI 同款原生验证 | Native AOT 标志通过；CLI 四份 Mania golden + MC/MR/Taiko 六项、28 项渲染/编码/设置/缓存/取消检查、x86 内存 111/111 全部通过，`precommit-aotcheck.log`；诊断 xUnit 反射警告边界仍按上文记录 |
| 原生编码器检测 | 默认值/设置兼容/列表保持通过，实机可用 libx264、h264_amf，`precommit-native-encoders.log` |
| 最终 ZIP | 77 文件 CRC/集合/全部 SHA-256/测量清单一致，`precommit-zip-verification.log`；不含 Headless/xUnit 产品文件 |

重新打包产生的上一套包也移入同一归档目录；最终提交前修复两处空白格式问题并检查暂存 diff，无构建输出/日志/下载物误入提交。

尚未执行远程 GitHub Actions。无 .NET 干净 Windows、DPI/实际拖放和完整实时游戏 GUI 验收仍按上文列为待完成，本轮不以后台检查替代这些验收。

## 2026-10-20：精简内置 FFmpeg

内置视频编码只留 libx264，保留原有 AAC/MP3 和导出参数；两套界面均支持选择外部 FFmpeg、恢复内置及按实际能力刷新编码器菜单。已生成完整 Native AOT ZIP（19.91 MiB），内置 FFmpeg 为 10.92 MiB。最新精简工具的原生 29 项检查和界面后台 6 项检查通过。详细产物、哈希及验证结果见 [FFMPEG-MINIMAL-RESULTS.md](FFMPEG-MINIMAL-RESULTS.md)。

## 2026-10-20：osu! 运行状态

顶栏状态改为检测 `osu!.exe` 进程，显示“osu!：运行中”（绿色）或“osu!：未运行”（灰色）。两套界面沿用 500 ms 定时刷新；关闭成绩自动检测、关闭内存读取或正在渲染时仍更新状态，不再以安装目录是否存在作为运行依据。Release 构建和现有无系统窗口界面检查通过；日志为 `out/ffmpeg-minimal-check/osu-status-build.log` 和 `osu-status-headless.log`。

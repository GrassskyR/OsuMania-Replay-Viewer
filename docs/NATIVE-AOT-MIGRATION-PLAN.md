# Native AOT 迁移执行计划

日期：2026-10-20。编写时仓库：`D:\Projects\CSharp\Replay-Viewer`，HEAD：`e1ec981`。

## 目标与执行边界

将完整桌面程序迁移到可发布 Native AOT 的实现，保持 Windows 10/11 x64、无需用户安装 .NET、解压后离线运行与导出视频，并降低发布包体积。

这是一项完整迁移任务，不能以 CLI 能发布 AOT、新建空窗口或普通 JIT 测试通过作为完成。先逐步验证依赖，再迁移界面；实现并验证每个阶段后继续下一阶段，常规实现选择自行判断，不要求用户逐步批准。确有外部阻塞时记录复现、已尝试方案和剩余工作，不能把未验证内容写成通过。

- 默认路线：保留 C# 业务模块，新增 Avalonia 桌面入口，采用该版本官方支持的 Native AOT 配置；使用编译绑定。
- 迁移期间保留可运行的 WPF 入口作行为参照，新入口通过全部验收后再切换默认启动/发布命令。是否保留旧入口作为回退由维护成本决定，记录结果。
- 第一轮保持现有 FFmpeg 二进制和功能，便于准确测量 AOT 的收益。定制 FFmpeg 是后续独立优化，不混入此次迁移。
- 目标仍是 Windows，不为跨平台重写游戏联动。不要修改判定算法、滚动节奏、GPU 选择顺序或导出质量来换取体积。
- 不通过隐藏裁剪/AOT 警告、给整个程序集无差别保根、关闭反射检查等方式制造成功。
- 不强求真正只剩一个 EXE；AOT UI/绘图依赖可能有本机 DLL，必须完整打包并计入体积。不捆绑完整 .NET CLR，不要求安装 .NET。

## 已确认的现状

| 模块/文件 | 现状与迁移关注点 |
|---|---|
| `src/ManiaReplayMaster.App` | .NET 8 WPF，不能直接打开 PublishAot；MainWindow、App 样式与本地化 MarkupExtension 需要迁移 |
| `App/MainViewModel.cs` | 已有状态管理，但混入 WPF Brush、Application.Resources、Binding.IndexerName，需解耦 |
| `App/GameBridge.cs` | 游戏联动适配层，尽量复用 |
| `App/UiSettings.cs` | JsonNode 配置；保留现有文件路径、字段、默认值和未知字段；按分析器和发布行为适配，不假定所有 JSON DOM 都要改成 DTO |
| `Core/Beatmap/MalodyMapModels.cs` | 默认 JsonSerializer.Deserialize，需要源生成类型信息；保留数字字符串兼容、大小写规则 |
| `Core/Replay/OsuReplayParser.cs` | SharpCompress 0.37.2 的 LZMA 解压，是第三方 AOT 验证重点 |
| `Cli/Program.cs` | JSON 快照导出需要源生成，CLI 仅依赖 Core，不覆盖 Render、Game 和 UI |
| `Game/BeatmapLocator.cs`、`ReplayResolver.cs` | 索引缓存 JSON 需要适配；缓存兼容和损坏回退行为不能退化 |
| `Game/Memory/ProcessMemory.cs` | Win32 DllImport、SafeHandle、结构封送；AOT 下验证，必要时改 LibraryImport，不机械替换全部 P/Invoke |
| `Render/ManiaReplayMaster.Render.csproj` | UseWindowsForms=true，用来获取 System.Drawing.Common；需解除桌面框架引用 |
| `Render/Renderer.cs`、`RgbPngWriter.cs` | GDI+ Bitmap/Graphics/字体和锁像素；自定义 PNG 写入、BGR/RGB/stride 是迁移关注点 |
| `Encode` | 通过独立进程调用 FFmpeg，基本可复用；必须测试取消、失败、GPU 回退 |
| `build.ps1`、`.github/workflows/*.yml` | 自包含 x64，publish-single 已开内部压缩、关闭 PDB；CI 同时构建两个包 |

当前发布目录有 WinForms DLL，不能据此推断删 UseWindowsForms 即可瘦身：同版本 SDK 的纯 WPF 空项目也会发布包含这些文件的 WindowsDesktop runtime pack。最终应通过新入口的实际依赖图和 AOT 产物证明已解除依赖。

## 体积基线与已有材料

本轮之前已在隔离输出目录完成发布体积实验，未修改源码或发布脚本：

| 方式（均保留同一 FFmpeg） | 主 EXE | 完整目录 | ZIP |
|---|---:|---:|---:|
| 当前自包含，单文件内部压缩 | 63.785 MiB | 164.620 MiB | 95.597 MiB |
| 当前自包含，不开内部压缩 | 140.864 MiB | 241.699 MiB | 95.697 MiB |
| 框架依赖，不内置 .NET | 1.503 MiB | 102.215 MiB | 37.733 MiB |

框架依赖版不满足此次目标，只作参考。以上 ZIP 使用 Python DEFLATE level 6；最终前后对比必须采用同一种压缩方法。

- FFmpeg 本体约 100.540 MiB。AOT 不会压缩或加速这个独立程序。
- 本地旧 `artifacts/ManiaReplayViewer-win-x64-single.zip` 为 119.109 MiB，其中夹带导出 MP4，ZIP 中占 22.266 MiB，不能拿它作为 AOT 改善的基线。
- 原始数据：`out/package-size-audit-20261020/measurements.json`。
- 测量脚本：`out/package-size-audit-20261020/audit.py`。
- 详细说明：`out/package-size-audit-20261020/分析报告.md`。
- `out/` 是忽略目录，可能只存在于当前机器；材料缺失时重新生成基线。

## 阶段 0：工具链、版本选择和可复现基线

1. 先读取适用的 AGENTS.md，检查当前 git 状态，保护其他窗口的改动；不假定仍停在上述 HEAD。
2. 确认 Windows x64 SDK、MSVC C++ 编译工具和 Windows SDK。机器 PATH 的 dotnet 指向 x86 旧 SDK；之前可用的 x64 SDK 是 `D:/Tools/dotnet8/sdk/dotnet.exe`（8.0.404），`C:/Program Files/dotnet` 当时仅有运行时。重新检查实际安装。
3. 查询所选 Avalonia、绘图库和 .NET 版本的官方 Native AOT 支持说明，锁定版本并记录依据。推荐最终使用受支持的 .NET LTS；可先在现有 .NET 8 验证核心，SDK/TFM 升级单独实施和验证，不同时盲升所有依赖。不要只为 AOT 升级到预览版。
4. 用现有脚本跑 build、test、gamecheck memory32，确认 FFmpeg/ffprobe 可供端到端测试使用。记录原有失败，不把原有失败算作迁移回归。
5. 从全新 staging 目录构建旧版基线，记录文件清单、各文件体积、总目录体积、ZIP 体积与版本。
6. 在 `docs/NATIVE-AOT-MIGRATION-RESULTS.md` 建立持续更新的实施记录：选型、阶段进度、验证命令、通过/失败/未验证、体积对比。

验收：当前功能基线和可复现工具链明确；所选 UI/SDK 组合有官方支持依据或实际发布验证。开始迁移不要求先重写界面。

## 阶段 1：Core + CLI 的原生发布

1. 给适用的业务库逐个启用 AOT/裁剪兼容分析，例如 IsAotCompatible；不要把 PublishAot 放到 Directory.Build.props 使 WPF、测试和 x86 辅助程序一起受影响。
2. 为 Malody 模型、CLI 快照和实际发现的其他 DTO 增加 JsonSerializerContext，显式使用生成的 JsonTypeInfo。覆盖嵌套模型、集合及所需选项，避免退回默认反射解析器。
3. 保持现有 JSON schema、数字格式、空值处理和序列化语义。涉及非 public 缓存类型时按最小范围调整可见性和嵌套结构。
4. CLI 单独开启 AOT 发布，优先 OptimizationPreference=Size。不要照搬自包含单文件的 EnableCompressionInSingleFile 等选项，按 Native AOT 官方发布方式配置；无必要不启用 InvariantGlobalization。
5. 实测 SharpCompress 的实际 LZMA 解压路径。若存在兼容问题，优先尝试合适的兼容版本，再考虑局部替换；不得仅因依赖包含 ZstdSharp 就先重写压缩模块。
6. 发布后直接运行 mrm.exe 的 help、dump-map、dump-replay、dump-judge/benchmark；比对 fixtures 与 tests/golden。OSU 和 Malody 都要覆盖，缺失的 Malody 最小样例需补充。

命令形式（版本、路径按阶段 0 的实际结果）：

```powershell
& '<x64-sdk>/dotnet.exe' publish src/ManiaReplayMaster.Cli/ManiaReplayMaster.Cli.csproj `
  -c Release -r win-x64 -p:PublishAot=true -p:OptimizationPreference=Size `
  -o out/aot-check/cli
& ./out/aot-check/cli/mrm.exe help
```

验收：原生 CLI 能解析全部目标格式，判定和结构化快照与原版一致；项目自身相关的 AOT/裁剪问题已解决，第三方警告逐项有依据。普通 dotnet test 不能替代运行发布 EXE。

## 阶段 2：Render/Encode 的 AOT 验证

1. 建立尽量小的原生导出验证入口，引用真实 Render/Encode，不是只创建空位图。优先复用现有工具，必要时增加独立诊断工具，避免为了测试污染 GUI 用户流程。
2. 去掉 Render 对 UseWindowsForms/WindowsDesktop 框架的依赖。先验证独立 System.Drawing.Common NuGet 在所选版本下的 AOT 编译和实际绘图，不预先承诺其兼容。
3. 若该路线不能可靠发布或运行，则将内部绘图迁移到 AOT 兼容绘图库，默认候选 SkiaSharp，并锁定 Windows x64 本机资产；避免同时携带两份不兼容版本的 Skia 本机库。保持 RenderExporter 等上层调用尽量稳定。
4. 若换绘图库，明确保留坐标取整、画布尺寸、色彩、线宽、LN、Taiko 旋转、时间戳字体与基线。检查 RgbPngWriter 的像素通道、行方向、stride、alpha 和 PNG 数据，不把换编码器作为不相关重构。
5. 用原生验证入口实际导出：有 BGM、无 BGM、单次与分段导出、Mania、Taiko、Malody、不同 FPS（包括现有 59/120 等节奏案例）、取消和失败。
6. 对比代表性 PNG/解码后视频帧。字体与抗锯齿差异需记录并评估，音符位置、滚动速度、时间和判定不能变。保留或迁移现有 RendererTests、ScrollCadenceTests、EncoderFailureTests 等。
7. 保留 libx264、NVENC/QSV/AMF 检测和 GPU 失败回退。无可用 GPU 时不能伪报硬件路径通过；单元验证选择逻辑，明确真实硬件覆盖情况。

验收：原生程序实际完成有声/无声导出、分段合并、取消和 CPU 回退；导出尺寸、音视频时长、帧节奏正确；产品依赖图不再要求 WindowsDesktop CLR。UI 迁移可以在此前完成小型可行性验证，但不先全面重写。

## 阶段 3：游戏联动与缓存适配

1. 为 BeatmapLocator 的 SongsIndexCache、ReplayResolver 的 ReplayHeaderIndexCache 等实际类型实现源生成 JSON，兼容旧缓存并保留损坏重建逻辑。
2. 在原生发布的 gamecheck 或独立等效验证入口中覆盖：谱面目录定位、数据库读取、回放解析/匹配、索引首次创建与二次读取、目录监听。
3. 验证 ProcessMemory 的布局、BOOL/字符串封送和 SafeHandle 生命周期；按需要使用 LibraryImport 或显式封送，DllImport 本身不代表不支持 AOT。
4. 发布并运行 x64 AOT 内存检查入口，配合现有 x86target 执行 memory32。x86 辅助目标不要求 AOT；不能因为全局 AOT 属性破坏它。
5. 在游戏可用时再执行 live，测试连接、退出重连、成绩页面识别和自动填充；不能写入或修改游戏内存来完成测试。

验收：合成 x86 内存检查通过，旧缓存可读、新缓存可复用，联动缺失或失败时程序仍可手动工作。真实 osu! 不可用时明确未验证项，保留可执行验证命令。

## 阶段 4：建立 Avalonia 桌面入口并迁移交互

1. 新建例如 `src/ManiaReplayMaster.Desktop`，使用已验证 AOT 支持的 Avalonia 版本；先最小化窗口原生发布、启动、文本输入与中文显示，确认本机依赖完整后逐步加功能。
2. 抽离 MainViewModel 的 WPF 类型：Brush/Resources 改为普通状态，界面负责颜色映射；移除 WPF Binding 常量依赖。INotifyPropertyChanged、ObservableCollection 等可保留。
3. 合理复用 GameBridge、UiSettings、资源字符串和任务管理；共享代码通过项目引用或清晰的共享层组织，不复制两份长期维护的业务实现。只新增确有边界价值的接口，不做无关架构重构。
4. 迁移主窗口与样式，使用编译绑定并声明数据类型；逐项迁移文件拖放、浏览/路径交换、回放定位、候选成绩、自动检测开关、参数选择、进度日志、仅判定、导出、取消和打开输出目录。
5. 迁移 UI 调度、定时器、生命周期、后台任务收尾、异常捕获和崩溃日志。测试渲染中关闭窗口、取消后再开始，避免后台 ffmpeg 遗留。
6. 迁移实时中英文切换，保留自动跟随系统语言以及现有配置文件 `%LOCALAPPDATA%/ManiaReplayViewer/ui-settings.json`。不得为了体积移除中文/文化支持。
7. 保留界面布局与功能密度，处理 DPI 100%/150%/200%、窗口缩放、下拉框可读性、键盘焦点；不要把框架迁移变成未经要求的产品重设计。
8. 发布原生 GUI 后执行 UI 冒烟。原 tools/appcheck/app-smoke.ps1 依赖 WPF/UI Automation，不能直接声称可复用；根据新框架的可访问性支持调整脚本，保留控件 AutomationId/等价标识以便验证。

验收：上述完整用户流程在原生 GUI 内可用，旧配置可直接继承；读写中文路径、拖放和语言即时切换正常；无 UI 线程异常、死锁或未清理的子进程。

## 阶段 5：发布、CI、干净环境和最终体积

1. build.ps1 增加明确的 AOT 发布/启动入口，输出到独立目录，例如 artifacts/win-x64-aot；每次先创建全新 staging，校验后替换结果，避免夹带 out/*.mp4 等旧产物。处理替换时核对绝对路径，不删除用户导出文件来“清理包”。
2. 打包整个可运行依赖集合和所有必需许可证，包含绘图库/UI 本机文件；保留 tools/package-ffmpeg.ps1 的版本与哈希校验。只携带 FFmpeg，不因测试需要把 ffprobe/ffplay 发给用户。
3. CI 安装/选择支持的 SDK、MSVC 与 Windows SDK，先构建与单元测试，再 AOT 发布并实际运行原生验证工具。ffprobe 可以是测试依赖，不是产品依赖。
4. 为 Release 添加明确命名的 AOT 包。迁移验收完成后默认发布可切换到新版本；不得在无人要求时推送 tag、发布 GitHub Release 或部署外部资源。
5. 在没有安装 .NET 的 Windows 10/11 x64 干净机器、VM 或等效可靠环境中解压运行，验证启动、文件选择、仅判定、真实导出和配置持久化。不要卸载宿主机 .NET 或改全局运行时配置来模拟。没有干净环境则记录该验收尚未完成。
6. 全量统计新包：主 EXE、UI/绘图 DLL、其他本机文件、FFmpeg、许可证，总目录与最终 ZIP。比较时用同一 FFmpeg、同一压缩算法与级别，不混入视频，不只报 EXE 体积。
7. 如测启动时间/空闲内存，使用相同硬件、相同场景、重复测量并注明方法；没有测量就不宣称性能提升。主要验收是功能、免安装和实际包体积。
8. 更新 README 的启动、SDK、开发依赖、打包命令和排错说明，移除用户需要安装 .NET 的错误提示；不要误称 Native AOT 无任何本机依赖。

验收：AOT ZIP 可在无 .NET 环境启动并导出；同口径完整下载包小于旧版干净基线。如果 AOT 本身成功但包没有变小，说明体积目标尚未达成，分析具体依赖并记录，不虚报瘦身。不得用首次联网下载 FFmpeg 替代原定离线完整包目标。

## 建议拆分的实现批次

1. 工具链/基线、AOT 分析开关与实施记录。
2. Core/CLI JSON 源生成及原生 CLI 验证。
3. 渲染依赖解耦、必要的绘图库适配与原生导出验证。
4. Game JSON/PInvoke 兼容与原生合成内存检查。
5. UI 无关状态抽取与 Avalonia 原生最小窗口。
6. 完整界面、实时本地化、设置兼容与生命周期。
7. UI/端到端回归、干净环境验证。
8. 发布脚本、CI、体积对比、默认入口和文档更新。

每批只做相关改动并执行相应验证；如记录提交，使用能说明行为变化的提交信息。无需在每批结束后停下来询问是否继续。

## 最终交付清单

- [ ] 完整 Native AOT 桌面应用，非仅 CLI/空窗口；依赖版本与选型依据明确。
- [ ] Core/Render/Encode/Game 原有能力保留，必要的 AOT/裁剪警告已解决或有精确说明。
- [ ] OSU Mania/Taiko、Malody、判定、音视频、取消、GPU 回退均有验证证据。
- [ ] 游戏合成内存测试通过，真实 GPU/游戏覆盖与未覆盖项诚实列出。
- [ ] 旧配置、多语言、中文路径、DPI、日志与异常处理正常。
- [ ] 全新 staging 打包，不夹带导出视频；FFmpeg 和许可证完整。
- [ ] 无 .NET 环境运行验证完成；若不可用，显式保留为待验收。
- [ ] 同口径新旧体积表，AOT 包实际更小；不以污染的 119 MiB 旧包比较。
- [ ] README/脚本/CI 更新，提交或改动清单与测试命令齐全。
- [ ] `docs/NATIVE-AOT-MIGRATION-RESULTS.md` 记录最终结果、限制与下一步。

## 建议技能

仅按实际需要调用：难以定位的迁移回归用 diagnose；需要操作浏览器核对官方文档时用相应可用工具；运行 UI 验证时优先现有脚本和所选框架支持的自动化。此计划不要求委派子代理或额外创建聊天，也不要求将计划发布成 GitHub issue。

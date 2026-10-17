# Mania Replay Master — Windows 原生重写方案（定稿）

> **版本 v2（已定稿）**｜调研对象：`D:\Projects\Java\Mania-Replay-Master`（master @ `99c0a67`，v2.4.0）
> **目标**：重写为 Windows 原生程序，带 GUI；并与 osu! 进程联动——**在游戏里打开任意一条成绩，程序自动识别该成绩对应的回放文件与谱面文件并预填**。

---

## 0. 决策记录（本版锁定）

| 项 | 决策 | 说明 |
|---|---|---|
| 形态 | **Windows 原生重写**（不是给 Java 加 GUI） | 按你的要求：必须用 Windows native 语言。给 Java 加 GUI 虽更省事（JNA 一样能读内存），但**不满足该要求，已否决** |
| 语言 / UI | **C# / .NET 8 + WPF** | 内存读取用 P/Invoke；GUI、JSON、DB、压缩生态齐全；可自包含发布 |
| 首个游戏 | **osu!mania（osu!stable）** | taiko / Malody 保留解析与判定能力，但内存联动第一版只做 stable + mania |
| 核心新功能 | **打开任意成绩界面 → 自动定位该成绩的回放文件 + 谱面文件 → 预填 → 用户确认后渲染** | 不再局限于"刚打完那一局"（§2.2、§5.3） |
| 内存读取 | **核心机制** | 用于游戏状态与"你打开的是哪条成绩"；**文件路径**由 `scores.db` + `Data\r` 给出 |
| 回放定位 | **内存（身份）+ `scores.db`（映射）+ `Data\r`（实体）三层组合** | 关键发现：`scores.db` 就是"成绩 → 加密回放文件"的索引 |
| 实施顺序 | **先壳，再换心** | M1/M2 先交付 GUI + 游戏联动（渲染可暂调 Java jar 作为内部过渡），M3–M5 换内核，最终产物**不含 JVM 依赖** |
| **编码路线** | **E1：照抄现有 ffmpeg filter graph**（保真优先） | 你的决定。代价：ffmpeg + `libx264`（GPL-only）仍是必需依赖，GPL 合规问题**保留为待处理风险 R8**；且不走向逐帧渲染 → 将来若要做"游玩中实时叠加"需重新评估（R13） |
| **开发环境** | **允许安装 .NET 8 SDK** | 你的决定。本机现状仅 SDK 5.0.100（x86 目录） |
| 尚未决定 | 真机（装了 osu!stable 的机器）从哪来；`scores.db` 在游戏运行时是否可读；是否保留 keytoix 服务端协议；taiko/Malody 范围；代码放哪 | 见 §10 |

---

## 1. 现状调查

### 1.1 调用链

```
ManiaReplayMaster.bat
  └─ java -jar ManiaReplayMaster.jar
       └─ Main.main()                                   [Main.kt]
            ├─ Config.init/refresh()                    [model/Config.kt]  ← config.txt (YAML)
            ├─ UpdateHelper.checkUpdate()               [UpdateHelper.kt]  ← HTTP 版本检查
            ├─ prepareJudgements(beatmap, replay)       [Main.kt]
            │    ├─ IReplayReader.matchReplayReader()   [replay/IReplayReader.kt]  ← 按扩展名分派
            │    │    ├─ OsuReplayReader   (.osr)       [replay/OsuReplayReader.kt]
            │    │    └─ MalodyReplayReader(.mr)        [replay/MalodyReplayReader.kt]
            │    ├─ readBeatMapMD5() → MD5 → 校验谱面 MD5
            │    ├─ IMapReader.matchMapReader()         [beatmap/IMapReader.kt]
            │    │    ├─ OsuMapReader     (.osu/.osz)
            │    │    └─ MalodyMapReader  (.mc/.mcz)
            │    ├─ beatMap.checkValidation()           [model/BeatMap.kt]  ← 找 bgm，找不到就静音
            │    ├─ judger.judge()                      [judger/*.kt]
            │    └─ 可选: 导出 judgement_*.json → 调 judgementStatistics.exe
            ├─ Render(beatMap, replayModel, temp.png).start()   [Render.kt]
            │    └─ 逐"窗口"渲染 BufferedImage → PNG 分片 (_0.png, _1.png, …)
            └─ ReplayMaster.generateVideo()             [ReplayMaster.kt]
                 └─ Encoder(...).generateVideo()        [Encoder.kt]
                      └─ 调 ffmpeg.exe：overlay 滚动 + trim + concat + atempo/adelay → mp4
```

#### ⚠️ 1.1.1 最容易看错的一点：ffmpeg 是"动画引擎"，不是"编码器"

`Render.kt` 的 `start()` **不渲染视频帧**。它对每个窗口只画**一张静态长条 PNG**：

- 画布尺寸 `BufferedImage(gameWidth, windowSize)`，`windowSize = stepSize + height`（默认 `3000 + 960 = 3960` 像素高）；
- 内容是该时间窗口内所有音符按 `timeToHeight` 排好的**静态**画面；
- 输出 `_0.png, _1.png, …`，**图片数等于窗口数**（一首 3 分钟的歌约 10–40 张，而不是 10800 帧）。

真正的"下落动画"由 `Encoder.kt` 交给 ffmpeg 完成：

```
color=s=WxH,fps=60[bg]
[bg][png]overlay=y=-h+H+t*<fps*speed>:shortest=1[v]   ← 时间表达式滚动静态长图
[v]trim=end=<sliceDuration>[video]
… concat=n=N:v=1 …
```

**三个直接结论：**

1. **性能模型完全不同**：现版渲染开销极低（几十张 PNG），压力全在 ffmpeg。改成逐帧渲染后是每帧 CPU/GPU 成本——但换来"裸帧直接进编码器"（省 PNG 落盘 IO），且**同一套渲染器可同时服务 GUI 预览与离线导出**。
2. **"每 20 张拼一次"（`ffmpegMaxProcessingSize=20`）纯粹是被 filter graph 逼出来的 hack**：输入文件一多，`-filter_complex` 会爆炸。改逐帧后这个 hack 自然消失。
3. **现有架构原理上无法支持"游玩中实时叠加"**：实时需要"游戏时钟 → 立即出帧"，现架构是"离线批量出图 → 事后滚动"。**如果实时叠加是核心目标，编码路线必须选 E3，且语言天平会向 C++ 倾斜**（§4.2）。

### 1.2 三类运行模式（重写必须全部考虑）

| 模式 | 触发 | 行为差异 |
|---|---|---|
| CLI 交互（默认） | 无参数运行 | 打印欢迎横幅、交互式输入谱面/回放路径、字符进度条、结束后按回车打开视频 |
| `isDesktop` | 配置项 | 屏蔽横幅/日志/提示，只输出 `progress: <n>` 与 `out: <path>` / `error: <code>`，失败即 `exitProcess(1)`。**给桌面外壳程序用的机器可读模式** |
| `isServer` | 配置项 | 无交互，进度写 `outputDir/progress.txt`、错误写 `error.txt`、模式写 `game_mode.txt`、判定导出 `judgement_*.json`。**keytoix.vip 在线渲染站的协议** |

> 重写应保留 `isServer` 文件协议（或在文档里明确放弃），因为线上渲染站仍依赖它。

### 1.3 关键算法与必须保真的语义

| 位置 | 内容 | 重写风险 |
|---|---|---|
| `OsuReplayReader.getJudgement` | mania 判定窗口由 OD + Mods(HR/EZ) + ScoreV2 推导；taiko 三档窗口 | **必须逐位对齐**，否则判定颜色全变 |
| `OsuReplayReader.readReplay` | 事件流还原按键 → 合并 Note（含按住时长）；`rate` 由 DT(1.5)/HT(0.75)；mirror 由 bit30；ScoreV2 由 bit29 | 需对齐取整与排序稳定性 |
| `utils.adjust()` | 非 1.0 倍速时对**回放与谱面同时**缩小时间戳；镜像翻转列 | 顺序敏感 |
| `OsuManiaJudger.canHit` | LN 的"太早/太晚"，以及 `omScoreV2Adjust` 下"看下一个同列音符决定结束位置"的怪算法 | **最易写错**，必须差分测试 |
| `OsuManiaJudger.judgeRelease` | LN 尾判、`LNHasJudged`、`isLNJudgedWith` 的 1.2/1.1/1.0 系数、注释里的 "fxxking ppy" 分支 | 保真优先，别顺手"修好" |
| `OsuTaikoJudger` | 红蓝错击、大音符拆 `duelNote`、双打 30ms 规则、`taikoSingleColumn` 重映射 | 分支多，需单测 |
| `MalodyJudger` + 判定表 | 5 档 judge A–E × PE/PC × 倍速 | 表是常量，照抄 |
| `BaseJudger.judge` | `LinkedList` 迭代中删除的精确语义、`cannotJudge` 集合、判定统计的 `count` 手法 | 迭代顺序影响结果，别改成 LINQ |
| `Render.timeToHeight` | `speed/1000*fps` 的像素/毫秒比例；`maxStepSize` 分窗口；`windowFrameCount = stepSize/speed` | 决定观感，别"优化" |
| `Encoder` filter graph | `overlay=y=-h+H+t*fps*speed`、`trim=end=sliceDuration`、`atempo=rate`、`adelay=bgmOffset+1000`、`transpose=1`（taiko） | 照抄 ffmpeg 参数 |

### 1.4 配置项（= 新 GUI 的参数面板）

`Config.kt` 共 27 个字段：`speed`(20)、`width`(540)、`height`(960)、`stroke`(3)、`actionHeight`(7)、`blockHeight`(40)、`framePerSecond`(60)、`isMalodyPE`、`omScoreV2Adjust`、`judgementColor`(5 色)、`missColor`、`longNoteColor`、`taikoBackgroundRed/Blue`、`taikoForegroundRed/Blue`、`taikoSingleColumn`、`debug`、`codec`、`codecPreset`、`codecProfile`、`outputDir`、`isServer`、`isDesktop`、`maxStepSize`(3000)、`showHolding`、`showTimestamp`、`ffmpegMaxProcessingSize`(20)、`maxEnabledDuration`、`exportJudgementResults`、`viewJudgementStatistics(+Path)`。

约 12 个是日常会调的（速度、分辨率、FPS、键位色、暗色 notes、镜像、码率预设、输出目录），其余收进"高级"折叠页。**UI 要点：`speed` 与 `showTimestamp` 最常改，放最显眼处并支持即时预览。**

### 1.5 依赖与构建现实（本机实测）

| 项 | 实测结果 | 影响 |
|---|---|---|
| 构建系统 | **没有** Gradle/Maven，只有 IntelliJ `.iml` + artifact 配置；仓库里没有 jar | 现有 Java 版**无法命令行构建**，要产出基准必须先补构建脚本 |
| Kotlin 编译器 | 未安装 | 同上 |
| Gradle | `C:\gradle-8.2.1\bin\gradle.bat` 存在 | 可给 Java 版补 `build.gradle.kts`（首次需联网拉 Kotlin 插件） |
| Java | 17.0.7 | 可运行构建产物 |
| ffmpeg | `C:\ffmpeg\ffmpeg.exe` 存在 | 编码链路可用 |
| .NET | **SDK 仅 5.0.100**（`C:\Program Files (x86)\dotnet`，x86），运行时 5/6/10 存在 | 写 .NET 8 项目前**必须装新 SDK** |
| C++ | `cl` 不存在（无 VS 工具链） | C++ 路线需额外装数 GB 工具链 |
| Rust | `cargo` 存在 | 可用 |
| Python | 3.11（本次调研用来自证 `.osr` 格式） | 仅验证脚本 |
| osu! | **本机未安装**（注册表无关联、无进程、常见目录均不存在） | 内存联动**无法在本机验证**，需一台装了 osu!stable 的机器 |
| 网络 | `web_search` 无 API key；osu! wiki 渲染页有登录墙（但 GitHub 原始 Markdown 可读） | 外网结论均来自直接抓取的具体 URL |

### 1.6 源码里值得注意的观察

- `IReplayReader` / `IMapReader` / `IJudger` 三个接口把"游戏"维度隔离得很干净 → 可直接映射成 C# 接口，**多加一个游戏只要加实现**。
- `Main.prepareJudgements` 硬编码 `delay = 1000L`，同时给谱面和回放加 1000ms 前置；`Encoder` 里 `adelay = delay + bgmOffset` 与之配对，**不能只改一边**。
- `beatMap.notes` 在判定过程中被原地修改（`judgementStart`、`column` 镜像/taiko 重映射、`relatedActionOrNote`），是"可变模型 + 就地标注"。C# 照抄成可变 class，别用不可变结构。
- `Main.judgementFromReplay` / `judgementFromJudging` 是**全局可变状态**，Malody 读取器直接往里写。重写应改成显式传参（不影响输出）。
- 在线渲染站协议是**约定式**的，没有版本号，改动即破坏兼容。
- `.gitignore` 里出现 `src/cpp/.idea` 与 `cmake-build-debug`（历史里没有对应提交）。

### 1.7 现有测试的强度（重要）

`src/test/OsuManiaJudgementTest.kt` 只有一个类、5 个方法：

- `testRice`：**唯一有真实断言的测试**（5 档 rice 判定窗口边界）；
- `testLN` / `testLNMapV1` / `testLNMapV2` / `testRiceMapV1`：只调用不崩溃，`testLNMapV2` 大量逻辑只为打印字幕。

**结论：现有测试不足以保护重写。** 必须先在 Java 侧补"导出中间结果"的基准模式（§7.1），否则 C# 判定层写完无从验证。

---

## 2. 目标功能定义（按你的最新澄清）

### 2.1 场景 A（游戏未运行）— 纯手动

选择回放（`.osr`/`.mr`）→ 自动解析出谱面 MD5 → 自动定位 `.osu`/`.mc` → 填入参数面板 → 渲染。

### 2.2 场景 B（游戏运行中）— **打开任意一条成绩即可识别**

> 你的原话：*"我想要的是打开任意一个成绩界面就能读取到对应的谱面文件和 replay 文件，而不是只有每局打完以后才能发现。"*

精确定义：

1. 程序常驻后台，识别 `osu!.exe` 是否运行；
2. 你在 osu! 里**进入任意一条成绩的界面**（本地排行榜里点开某条成绩、结算界面、或进入观看回放）时，程序识别"你正在看哪条成绩"；
3. 自动解析出**该成绩对应的回放文件路径**与**谱面文件路径**；
4. 主界面两个输入框自动填好，状态栏提示"已从 osu! 检测到：…"；你调参数后点渲染。

**与旧版的差别**：不依赖"刚打完一局"这个时机，因此**旧成绩也能识别**；也不依赖文件是否刚刚写入。

### 2.3 这个需求的技术链路（一句话概括）

> **内存告诉你"你看的是哪条成绩"，`scores.db` 告诉你"这条成绩的回放文件是哪个"，`Data\r` 提供文件本体，`osu!.db`/文件夹名告诉你谱面在哪。**

```
你在 osu! 里打开某条成绩
      │
      ├─(内存) 游戏状态 + 当前谱面身份 ────────────→ 谱面 MD5 / 歌曲文件夹 / .osu 文件名
      │                                              ↓
      │                                    (磁盘) Songs\<folder>\*.osu  → 谱面文件绝对路径
      │
      ├─(内存) 你正在看的这条成绩的字段 ──────────→ 玩家 / Mods / 分数 / 最大连击 / 日期
      │                                              ↓
      │                                    (磁盘) scores.db：该谱面 MD5 下的本地成绩列表
      │                                           每条含 {玩家, Mods, 分数, 连击, 命中数, 日期, 回放指针}
      │                                              ↓  ← 用内存读到的字段比对，唯一确定是哪一条
      │
      └─(磁盘) 回放指针 → Data\r\<加密文件名>.osr  ← 回放文件本体
                  ↳ 校验：解析该 .osr 头部，核对 MD5 / Mods / 分数 / 玩家
                  ↳ 指针不可用时：扫描 Data\r + Replays\ 全部 .osr 头部，按字段打分匹配
      ↓
GUI 自动填入「回放路径 + 谱面路径」，并顺带带出 Mods / 倍速 / 镜像 / 键数等参数
```

**为什么这条路能成立（关键事实）：**

- osu!stable 每局**通过**的 solo 会把回放自动写入隐藏目录 `Data\r`（`.osr` + `.osg`，**文件名加密**）；
- **`scores.db` 保存"本地成绩 → 加密回放文件"的指针映射**——官方 wiki 明确写着：删除 `scores.db` 会**丢失所有未导出的回放**。这句话反过来证明：`scores.db` 就是那张索引表；
- `Data\r` **从不清理**，所以旧成绩的回放文件通常仍在磁盘上 → "任意成绩"可行。

### 2.4 边界与限制（必须让你知道，避免期望落空）

| 场景 | 能否自动定位 | 说明 |
|---|---|---|
| **你自己的本地成绩**（`scores.db` 里有记录） | ✅ **可以**，正是本方案的主目标 | 回放文件在 `Data\r`，通过指针或字段匹配定位 |
| 你已手动导出到 `Replays\` 的回放 | ✅ 可以 | 作为额外候选目录一起扫描 |
| **别人的 / 在线榜单上的成绩** | ⚠️ **基本不行** | 本地没有对应回放文件；osu! 观看服务器回放时会下载到**临时位置并在播完后删除**。只能尝试抓临时文件，非常脆弱，**列为"不支持"** |
| 失败局 / 多人局 / Auto（且未按 F2） | ⚠️ 没有文件 | osu! 本身不写盘。程序应明确提示"此局没有回放文件，请在游戏内按 F2 导出" |
| 用户删过 `scores.db` 或 `Data\r` | ⚠️ 没有文件 | 属于数据已被清理，无法恢复 |
| 服务器回放在播放中途退出 | ⚠️ 文件被删 | 同上 |

---

## 3. 可行性分析

### 3.1 `.osr` 格式：实测零障碍

我用 Python 独立实现了 `.osr` 解析器（附录 B 是完整输出），跑通全部 4 个 fixture：

- 头结构：`byte mode` + `int version(YYYYMMDD)` + 3 个字符串（`0x0B` 标记 + 7-bit varint 长度 + UTF-8 字节）+ 6 个 `ushort` 计数 + `int` 分数 + `ushort` 最大连击 + `byte` perfect + `int` mods + 字符串血条 + `long` 时间戳 + `int` 回放长度 + LZMA 流；
- LZMA：`FORMAT_ALONE`，props `5D 00 00 20 00`（字典 2 MB），未压缩大小在头部声明；
- 事件流是纯文本 `w|x|y|z,` 逗号序列（结束标记 `-12345|0|0|…`），最大样本 46,247 个事件。

**结论：解析层（M3）与判定层（M4）在格式侧没有技术风险。** 现有 Java 依赖的 `lib/osu-replay.jar` 只有 class 文件、没有源码，是唯一的"不可读依赖"，也完全可以被替换。

### 3.2 谱面定位：两条已验证的路径

- **MD5 → 谱面文件**：回放头里的谱面 MD5 **就是** `.osu` 文件字节的 MD5（`certutil` 逐文件验证，4/4 精确匹配；Malody `.mr` 内嵌 MD5 同样等于其 `.osu` 的 MD5）。
  - 路径一：解析 `osu!.db`（MD5 → 歌曲文件夹名），快且精确，但要处理版本号差异；
  - 路径二：后台扫描 `Songs\*\*.osu` 算 MD5 建索引（持久化缓存 + 按 修改时间/大小 失效），首次慢、之后增量。
- **内存 → 文件夹名（最快）**：当前谱面对象的歌曲文件夹（`+0x78`）与 `.osu` 文件名（`+0x90`）可直接读出 → 直接拼出 `Songs\<folder>\<file>.osu`，**完全跳过 MD5 索引**。这是内存读取最直接的收益。

### 3.3 成绩 → 回放文件：`scores.db` 是关键，本需求的核心

这是本版新增的、也是你那个需求能否成立的关键。

**已查实的事实：**

- osu!stable 每局**通过**的 solo 会把回放自动写入**隐藏目录 `Data\r`**（`.osr` + `.osg`，**文件名相同且加密**）；**`Replays\` 只在按 F2 导出时才有拷贝**。
  出处：osu! wiki `Gameplay/Replay/en.md`；lazer 源码 `LegacyScoreImporter.cs` 的 `ImportFromStablePath => Path.Combine("Data","r")`。
- **`scores.db` 保存"本地成绩 → 加密回放文件"的指针映射**；官方 wiki 明确警告"删除 `scores.db` 会丢失所有未导出的回放"。
  出处：osu! wiki `Client/Program_files/en.md`。
- osu! 根目录可由 `osu!.exe` 的进程映像路径或 `%LOCALAPPDATA%\osu!` 推出；`osu!.<user>.cfg` 的 `BeatmapDirectory` 可以把 `Songs` 挪走（要尊重），但**没有** `ReplayDirectory` 这类键。

> **⚠ 本节初版关于"回放文件如何定位"的说法已被实测推翻，实现以修订为准**
> （详细证据：`docs/HANDOFF.md` §5.1、`docs/SCORESDB_FORMAT.md`）
>
> 1. **回放文件名不是成绩记录里的 replay hash。** 正确规则是
>    `Data\r\<谱面MD5>-<FILETIME>.osr`，其中 `FILETIME = dateTicks - 504911232000000000`
>    （即 .NET ticks → Windows FILETIME 的 1601-01-01 偏移）。在真实 `scores.db` 上
>    **5420/5420** 条记录都按此规则精确命中；2690 个组 MD5 全部出现在 `Data\r` 文件名前缀里，
>    而成绩记录里的 replay hash **0/5420** 出现。`replayMd5` 字段确实是回放 hash（等于 `.osr`
>    头部第 3 个字符串），但它**不**是文件名。
> 2. **玩家名为空的记录是合法的**（258 条，其 `.osr` 也是空的），不能把"玩家非空"当作有效性判据。
> 3. `Data\r` 里还有 93 个前缀没有对应的 `scores.db` 组（孤儿文件），目录共 5639 个 `.osr` + 4576 个 `.osg`。
>

**`ReplayResolver` 的四级策略（可靠性从高到低）：**

1. **`scores.db` 指针（首选，精确定位）**：解析 `scores.db`，拿到该谱面 MD5 下的本地成绩列表与各自的回放文件指针 → 用内存读到的"当前成绩字段"比对，唯一确定 → 直接得到 `Data\r\<加密名>.osr`。
2. **`Data\r` 目录监听（不依赖内存**，也覆盖"刚打完"场景）：`FileSystemWatcher` 盯 `Data\r` / `Replays\` 新增 `*.osr` → 解析头部 → 定位谱面。**内存读取完全失败时这条路径仍成立。**
3. **全量头部扫描 + 打分（兜底）**：把 `Data\r` + `Replays\` 全部 `.osr` 头部解析一遍（几千个文件也秒级完成），按"谱面 MD5 必须相等 + Mods + 玩家 + 分数/连击/命中数 + 修改时间"加权排序取最高分；**前几名接近时 GUI 给候选下拉**。
4. **内存直接 dump（终极兜底）**：内存里正在播放的回放动作数据是可读的，极端情况下可自行合成合法 `.osr`。工作量高，仅在前面全部失败时启用。

**需要实测确认的一点**：`scores.db` 的二进制格式是**版本化且无官方文档**的。但 osu!lazer 的源码（**MIT 许可**）里包含读取 stable 格式的实现，可作为解析参考——这比从零逆向快得多。

### 3.4 内存读取的技术前提与风险

| 项 | 说明 |
|---|---|
| 目标进程 | `osu!.exe`，**32 位**（WOW64）。64 位宿主读 32 位目标**没有障碍**；反过来才有（`ERROR_PARTIAL_COPY`）。**工具编 x64** |
| 权限 | 同用户、同完整性级别下 `OpenProcess(PROCESS_VM_READ \| PROCESS_QUERY_INFORMATION)` 即可，**无需提权、无需 `SeDebugPrivilege`**。只有 osu! 以管理员运行时工具才需跟着提权。**明确 non-goal**：不触碰受保护进程（PPL）/反作弊保护的目标 |
| 用到的 API | `OpenProcess`、`ReadProcessMemory`、`VirtualQueryEx`、`CreateToolhelp32Snapshot` + `Module32First/Next`、特征码扫描、多级指针解引用 |
| 地址稳定性 | osu!stable 被混淆，**每次更新都可能改变偏移**。业界做法是**特征码扫描 + 版本表**，而不是硬编码偏移 |
| 社区先例 | gosumemory（Go，GPL-3.0）、tosu（LGPL-3.0）、StreamCompanion 长期做同样的事 → **只读内存访问在实践中不会被封**（osu!stable 没有内核反作弊） |
| 安全立场 | 本工具**只读、不注入、不写入、不修改游戏、不做 hook**。osu! 官方规则第 2 条 "Play fair" 禁止的是"用第三方工具**获取优势**"，**并未禁止只读内存**。反面教材：gosumemory 的"游戏内叠加层"是**注入 DLL** 实现的，它自己的 wiki 就警告可能被封——**注入才是真正有风险的部分，而本功能不需要它。** 这一点必须写进 README |
| 失败降级 | 内存失败（版本不认识 / 权限不足 / 未运行）→ 自动降级到"监听 `Data\r`/`Replays\` + `scores.db` + 手动选择"。**GUI 在没有内存能力时仍然 100% 可用** |

#### 3.4.1 实现时必须踩对的五个坑（均为微软文档明确的行为）

1. **`ReadProcessMemory` 是"全有或全无"**：只要请求区间跨越不可访问内存，**整个调用就失败**。所以必须先 `VirtualQueryEx` 逐区域走，再按区域 / 4 KB 分块读。
2. **只扫 `MEM_COMMIT` + 可读 + 非 `PAGE_GUARD` 的区域**：`MEMORY_BASIC_INFORMATION` 按"状态/保护/分配"聚合连续页；地址向下对齐到页边界；超过最高可访问地址返回 `ERROR_INVALID_PARAMETER`。用返回的 `RegionSize` 步进，避免越界读。
3. **模块基址要用 `TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32`**：从 64 位进程枚举 32 位目标的模块**必须带 `TH32CS_SNAPMODULE32`**。另外 `CreateToolhelp32Snapshot` 可能返回 `ERROR_BAD_LENGTH`，**要重试**。
4. **指针宽度必须是 4 字节（最易静默出错）**：目标是 32 位进程。若按 8 字节读，会得到"看起来像合法地址"的垃圾值，再解一层引用后通常变全 0，**表现是"不崩溃但什么都读不到"**，极难排查。把指针宽度做成显式断言参数。
5. **指针链没有库，就是一个循环**：`模块基址 + 偏移 → 读出指针 → 再 + 偏移 → 再解引用…`。ASLR 正是不硬编码绝对地址的原因。

#### 3.4.2 可直接落地的特征码与指针链（摘自 tosu / gosumemory 公开源码原文）

> **⚠ 实测补正（必须遵守，否则内存层 100% 静默失效）：特征码必须全进程扫描，不能只扫模块镜像。**
>
> `osu!.exe` 是一个**托管 .NET 程序集**（PE 里有 CLR 目录，只导入 `mscoree.dll`，4.2 MB 的
> `.text` 装的是 IL，不是原生代码）。因此下面这些 **x86 机器码特征码在磁盘上的 exe 里根本不存在**
> ——实测 10 条特征码在映射镜像中 **0 命中**（`gamecheck staticsig`，PE 逐节解析）。它们只以
> **JIT 编译后的原生代码**形式存在于 CLR 的可执行堆里。tosu 的做法正是如此：它的原生
> `find_pattern` 用 `VirtualQueryEx` 遍历**整个进程的所有已提交区域**逐个 `ReadProcessMemory`，
> 而不是扫模块镜像。
>
> 实现要求：`SignatureEntry.SearchWholeProcess = true`（表内所有真实条目），扫描器按"枚举区域一次、
> 每个区域读一次、一次匹配全部特征码"的批量方式做，且**可执行区域优先**以便尽早收敛；
> 未命中条目要能**后续重扫并合并**（JIT 是惰性的——某个方法只有在玩家进到相应界面时才被编译，
> 所以"刚 attach 时只有部分特征码"是正常现象，不是版本不匹配）。
>
> 这条也解释了为什么"对着 exe 做静态校验"**做不到**：静态分析只能证明特征码不在镜像里，
> 语义正确性仍然必须真机确认。

**原则：不硬编码偏移，用"特征码扫描 + 指针链"，并把整张表做成可更新的数据。**

**基础特征码**（`??` = 通配字节）：

| 名称 | 特征码 | 用途 / 指针链 |
|---|---|---|
| `baseAddr` | `F8 01 74 04 83 65` | 一切指针链的根 |
| `statusPtr` | `48 83 F8 04 73 1E`（偏移 `-0x4`） | **游戏状态** `[Status - 0x4]` → **0 主菜单 / 1 选歌 / 2 游玩中 / 7 结算** |
| `playTimeAddr` | `5E 5F 5D C3 A1 ?? ?? ?? ?? 89 ?? 04` | 游玩时间 |
| `settingsClassAddr` | `83 E0 20 85 C0 7E 2F` | 设置（→ Songs 目录） |
| `rulesetsAddr` | `7D 15 A1 ?? ?? ?? ?? 85 C0` | 当前 ruleset（谱面）根 |
| `canRunSlowlyAddr` | `55 8B EC 80 3D ?? ?? ?? ?? 00 75 26 80 3D` | **是否正在看回放**：`readByte(readInt(canRunSlowlyAddr + 0x46)) == 1` |
| `gameTimePtr` | `A1 ?? ?? ?? ?? 89 46 04 8B D6 E8`（偏移 `0x1`） | 游戏时钟 |
| `skinDataAddr` | `74 2C 85 FF 75 28 A1 ?? ?? ?? ?? 8D 15` | 皮肤 |
| `menuModsPtr` | `C8 FF ?? ?? ?? ?? ?? 81 0D ?? ?? ?? ?? ?? 08 00 00` | 菜单 Mods |
| `MANIA_SPEED` | `a3 ?? ?? ?? ?? eb ?? dd 45 08 db 5d e8 8b 45 e8 83 f8 28` | mania 下落速度 |

**当前谱面（本功能最核心）**：

| 字段 | 指针链 / 偏移 |
|---|---|
| Beatmap 对象 | `[Base - 0xC]` |
| **谱面 MD5** | `[[Beatmap] + 0x6C]` |
| **歌曲文件夹名** | `[[Beatmap] + 0x78]` |
| **`.osu` 文件名** | `[[Beatmap] + 0x90]`（应以 `.osu` 结尾） |
| 在线 ID / 谱面集 ID | `+0xC8` / `+0xCC` |
| 音频 / 背景文件名 | `+0x64` / `+0x68` |
| 曲师 / 标题 / 作者 / 难度名 | `+0x18` / `+0x24` / `+0x7C` / `+0xAC` |
| AR / CS / HP / OD | `+0x2C` / `+0x30` / `+0x34` / `+0x38` |
| 物件数 | `+0xF8`（tosu）／ `+0xFC`（gosumemory，较旧） |
| **Songs 目录** | `[[[Settings + 0x8] + 0xB8] + 0x4]` |

**成绩 / 游玩数据**（这条链正是"识别你看的是哪条成绩"要用的）：

| 字段 | 指针链 / 偏移 |
|---|---|
| ruleset | `readInt(readInt(rulesetsAddr - 0xB) + 0x4)` |
| gameplay base / score base | `readInt(ruleset + 0x64)` / `readInt(gameplayBase + 0x38)` |
| **重试次数** | `readInt(readInt(baseAddr - 0x33) + 0x8)` |
| **玩家名** | `[[[Ruleset + 0x68] + 0x38] + 0x28]` |
| **命中数**（300/100/50/geki/katu/miss） | `scoreBase + 0x88 / 0x8A / 0x8C / 0x8E / 0x90 / 0x92` |
| **连击 / 最大连击** | `+0x94` / `+0x68` |
| **分数**（V1 / V2） | `scoreBase + 0x78` / `ruleset + 0xF8` |
| 准确率 | `[[gameplayBase + 0x48] + 0xC]` |
| HP | `[[gameplayBase + 0x40] + 0x14 / 0x1C]` |
| **Mods** | `ModsXor1 ^ ModsXor2`（两个 XOR 值相异或） |
| 结算界面成绩对象 | `readInt(ruleset + 0x38)`，其 `+0x4` 在线 ID、`+0x78` 分数、`+0x64` 模式、`+0x68` 最大连击、`+0xA0/0xA4` 日期、`0x88..0x92` 命中数、玩家名 `[[base + 0x28]]` |

#### 3.4.3 仍需真机确认的未知（本需求的主要不确定性）

1. **"当前选中的那条成绩"是否有稳定偏移。** tosu/gosumemory 提供的是**游戏状态 / 游玩中 / 结算界面**的成绩对象，以及排行榜（`Leaderboard`）的数据；**"本地排行榜里我点开的是第几条"这个选中状态是否有可靠偏移，需要真机确认。**
   - **如果没有**：退化为"用 `scores.db` 列出该谱面的全部本地成绩 + 用内存能读到的字段（分数/连击/Mods）做匹配 + GUI 一键候选"。**功能依然成立，只是可能需要你点一下确认。**
2. **`scores.db` 的具体二进制格式**（版本化、无官方文档）。参考 lazer 的 MIT 源码实现。
3. 特征码在**你当前 osu! 版本**上的实际有效性（逐条校验）。

### 3.5 osu!lazer 与 Malody

**osu!lazer —— 有官方本地 WebSocket，不用读内存。**

- `OsuWebSocketProvider.cs`：`int port = 49727;`，可用 `OSU_WEBSOCKET_SERVER_PORT` 覆盖；`WebSocketServer` 文档注释写明"供外部集成（如直播叠加层）使用，**只监听 localhost**"。
- **需显式开启**：`OSU_WEBSOCKET_SERVER=1` 时才启动。
- 数据：`BeatmapStateWebSocketDataSource` + `UserActivityWebSocketDataSource`；消息含 `beatmap{beatmap_id, beatmap_set_id, beatmap_hash, metadata, difficulty, ruleset_id, bpm, star_rating, max_combo, status, total_length, drain_length, object_count}, ruleset_id, mods[]`。
- **局限**：只有**谱面状态**，不是成绩/回放；id 与 hash 是**在线标识**（未提交的本地谱面为空）。
- **存储也不同**：根目录 `%APPDATA%\osu`，**没有 `Songs`/`Replays` 目录**；内容按 SHA-256 寻址存于 `files/<h[0]>/<h[0:2]>/<sha256>`，主库 `client.realm`；回放是哈希命名 blob，**没有可监听的文件名**，也没找到"打完自动导出 `.osr`"的路径。→ **对"找到某条成绩的回放文件"这件事，lazer 比 stable 更难。**
- **本版不做 lazer**，但保留 `IGameStateProvider` / `IReplaySource` 接口给它。

**Malody —— 可行，有 MIT 参考实现。**

- `TKazer/Malody-Reader`（**C++，MIT**）附加到 `malody.exe`，读取游戏状态、当前谱面、游戏内/结算成绩、Mods；README 与 `Read_Map.hpp` 给出逐字段偏移（`BPM {0xC,0x6C}`、`KeyCounts {0x8,0x114}`、`Mode {0x18}`、`LevelName {0xC,0x4}`、`SongName {0xC,0x58,0}` …）。
- **Malody 的 Windows 回放/谱面存储路径无任何可核实文档**（官网域名无法解析，闭源）。社区说的 `Malody/replay`、`Malody/beatmap` **未经验证**。→ **做成用户可配置目录 + 递归扫描 `*.mr`，不要猜路径。**
- `.mr` 文件名惯例是 `YYYYMMDD-HHMMSS-<曲名>.mr`——仓库 fixture `20220812-110445-Sewing.mr` 正是此格式；另有 MIT 的 `uzxn/MalodyReplayChecker`（C）独立实现了解析，说明格式已被多方验证，§3.1 的实测结论可靠。
- 本版**保留 `.mr`/`.mc` 的渲染能力**，但内存联动第一版只做 osu!stable。

**可直接复用的参考实现（许可证已查）**

| 项目 | 语言 | 许可证 | 用途 |
|---|---|---|---|
| `ppy/osu`（lazer） | C# | **MIT** | stable 格式（`Data\r` 导入、legacy 分数/回放解码）的实现参考，**可直接借鉴** |
| `Raregendary/rtosu-dataprovider` | Rust | **MIT** | 最贴近的 stable 内存读取参考 |
| `TKazer/Malody-Reader` | C++ | **MIT** | Malody 内存读取 + 偏移 |
| `maotovisk/OsuWineMemReader` | C# | **MIT** | 进程/内存管道写法参考 |
| `l3lackShark/gosumemory` | Go | **GPL-3.0** | 特征码可参考，**代码不能借用** |
| `tosuapp/tosu` | TypeScript | **LGPL-3.0** | 同上；**建议把 tosu 当外部进程、走它的 localhost API** 以回避 copyleft |
| `Piotrekol/ProcessMemoryDataFinder` | C# | **GPL-3.0** | 地址 DSL 的设计参考，**代码不能借用** |

---

## 4. 技术选型

### 4.1 语言与 UI 框架（决策已定：C# / .NET 8 + WPF）

| 维度 | **C# / .NET 8 + WPF** | C++ / Win32 + Direct2D | Rust + windows-rs + egui |
|---|---|---|---|
| 内存读取 | P/Invoke 直接调 Win32，最省事；社区样本多 | 最原生，但要手写大量样板 | `windows-rs` 绑定完整 |
| GUI | WPF 成熟，MVVM 适合"参数面板 + 成绩候选列表" | 手写控件/布局，成本高 | egui 控件少、观感偏工具 |
| 2D 绘图 | SkiaSharp 或 Direct2D(Vortice) | Direct2D 最强 | Skia / wgpu |
| JSON/YAML/DB | 内置 + `YamlDotNet` | 需引第三方 | serde 一流 |
| 单文件发布 | 支持（自包含） | 天然 | 天然 |
| 本机前提 | 需装 .NET 8+ SDK（现仅 5.0） | **需装 VS Build Tools（`cl` 不存在）** | cargo 已装 |
| 上手/维护成本 | **最低** | 最高 | 中 |

**加权打分（满分 500）：C# 420 / C++ 405 / Rust 395**——差距很小，敏感性必须讲清楚：若把"实时延迟 + 原生编码"设为主导权重，**C++ 反超（400 vs 355）**。

> **你的需求已明确是"成绩界面自动识别 + 渲染导出"，不是"游玩中低延迟叠加"，所以 C# 胜出且无需纠结。** 若将来真要做游玩中实时叠加，需重新评估（§9 R13）。

**必须点明：内存读取不构成语言选择的理由。** gosumemory（Go，730★）与 tosu（LGPL-3.0，573★）已在带 GC 的运行时上稳定读取 osu!stable 内存多年。**驱动选型的只有 GUI 成熟度与移植成本。**

**明确不要选 WinUI 3**：未打包应用要额外部署 Windows App SDK 运行时 + Bootstrapper API + VC++ Redistributable，对 zip 分发的工具是不必要的安装摩擦。WPF 或 Avalonia（MIT）都更合适。

**进程位数：编译为 x64，永不编 x86。** 微软文档明确：只有"32 位进程访问 64 位进程"才会 `ERROR_PARTIAL_COPY`；64 位宿主读 32 位 osu! 完全正常。因此未公开的 `NtWow64*` 私有 API 完全不需要。

### 4.2 渲染与编码路线（本方案最重要的架构分叉）

绘图层统一用 **SkiaSharp**（MIT）复刻 `Render.kt` 的图元（圆角矩形、椭圆、描边、抗锯齿、离屏 ARGB 位图）。**同一套绘图代码可同时服务 GUI 预览与离线导出**——这是相对现版最实质的架构升级。

⚠️ **预期管理**：Java AWT 的 `drawRoundRect(arc=1,1)`、`BasicStroke(w*2-1)` 与 Skia/D2D 的抗锯齿像素**不可能逐像素一致**。所以 parity 目标定为**"判定结果逐音符一致 + 视觉等价（结构/颜色/位置一致，抗锯齿差异允许）"**，不做像素级 diff。

| | **E1 照抄现状** | **E2 逐帧 → ffmpeg** | **E3 逐帧 → 媒体基础（MF）** |
|---|---|---|---|
| 做法 | 静态长条 PNG + `overlay=t*…` 滚动 + `concat`，`libx264` | Skia 逐帧 → `rawvideo` 管道喂 ffmpeg + `libx264` | Skia 逐帧 → `IMFSinkWriter` 系统 H.264（可硬件） |
| 与现版输出 | **几乎完全一致** | 视觉等价 | 视觉等价，编码参数不同 |
| 依赖 | 必须带 `ffmpeg.exe` | 必须带 `ffmpeg.exe` | **视频不需要任何第三方编解码器**；音频仍需 ffmpeg 一次预处理 |
| 许可 | **`libx264` 是 GPL-only** | 同左 | **摆脱 libx264 的 GPL 暴露** |
| 复用渲染器做 GUI 预览 | ❌ | ✅ | ✅ |
| 支持游玩中实时 | ❌ 原理上不行 | ⚠️ 可行但有管道延迟 | ✅ 最佳 |
| 实现成本 | 最低 | 低 | 中（COM interop） |

**音频（三条路线共同）**：媒体基础**没有** `atempo` 等价物（`IAudioClockAdjustment` 是重采样，会变调），而 DT 1.5× 不处理会变成"花栗鼠音"。所以**音频保留一次 ffmpeg 预处理**最优：用一条很小的 `atempo=rate, adelay=…` 生成 wav/aac 再合流。**顺带删掉了巨大的视频 `-filter_complex`**，也就删掉了 `ffmpegMaxProcessingSize=20` 的分片 hack。
（实时模式**不需要** `atempo`：按自然速度放音乐、用 `ReplayModel.rate` 缩放音符时间戳即可，这正是 `Utils.kt` 的 `adjust()` 已在做的事。）

**已定路线（你的决定）：全线使用 E1。** 也就是 M5"换心"时只把**解析 / 判定 / 绘图**换成 C#，**编码与"下落动画"继续由 ffmpeg filter graph 负责**。

- 好处：与现版输出保真度最高；绘图代码最简单（只需画静态长条 PNG，不必实现逐帧动画）；M5 工作量最小。
- 代价：ffmpeg（含 GPL-only 的 `libx264`）是**必需依赖**，GPL 合规问题保留（R8）；且不走向逐帧渲染，**将来若要做"游玩中实时叠加"必须重新评估**（R13）。

**E2 / E3 记录为备选，不在本期实施**：E3（媒体基础 H.264）可摆脱 `libx264` 的 GPL 暴露并支持逐帧/实时，但需要把"下落动画"从 ffmpeg 手里接过来自己做、还要处理 BGRA→NV12 色彩转换，且输出不再与现版逐一对应。

**许可结论（非法律意见）**：`Config.kt` 硬编码的 `codec: libx264` 是 GPL-only，ffmpeg.org 自己的合规清单就写着"确保你的程序没有使用任何 GPL 库（尤其是 libx264）"。以子进程调用未修改的 `ffmpeg.exe` 通常被视为"单纯聚合"，但仍再分发 GPL 二进制；**把 libavcodec 链接进自己的程序会传染整个应用**。走 E3 可干净绕开视频侧的这个问题。正式商用前请找法务确认。

### 4.3 目标项目结构

```
ManiaReplayMaster.sln
├─ src/ManiaReplayMaster.Core/        (net8.0)         纯逻辑，无 UI 依赖
│   ├─ Model/      Note, BeatMap, ReplayModel, JudgementWindow, RenderConfig
│   ├─ Beatmap/    IBeatmapReader → OsuBeatmapReader(.osu), MalodyBeatmapReader(.mc), ZipResolver
│   ├─ Replay/     IReplayReader  → OsuReplayReader(.osr + LzmaStream), MalodyReplayReader(.mr)
│   └─ Judges/     BaseJudger → ManiaJudger, TaikoJudger, MalodyJudger
├─ src/ManiaReplayMaster.Render/      (net8.0-windows)  SkiaSharp 渲染器 → 帧 / PNG 分片
├─ src/ManiaReplayMaster.Encode/      (net8.0)          FfmpegAudioPrep, MfH264Encoder, 进度解析
├─ src/ManiaReplayMaster.Game/        (net8.0-windows)  游戏联动（§5）
├─ src/ManiaReplayMaster.App/         (net8.0-windows)  WPF MVVM 外壳
└─ tests/ManiaReplayMaster.Tests/     (xUnit)           fixture 直接用仓库 test/ 目录
```

**分层规则**：`Core` 不允许引用 WPF/Skia/Win32 → 判定与解析可纯单元测试，换 UI 或做 CLI/服务端模式都不用重写逻辑。

### 4.4 第三方依赖

| 用途 | 选择 |
|---|---|
| LZMA 解压（.osr） | 公有领域 LZMA SDK（零依赖）或 `SharpCompress`（MIT） |
| ZIP 解包（.osz/.mcz） | `System.IO.Compression`（内置） |
| YAML 配置（兼容 config.txt） | `YamlDotNet`（MIT） |
| JSON（judgement 导出 / 服务端协议） | `System.Text.Json`（内置） |
| 2D 绘图 | `SkiaSharp`（MIT）；注意带原生二进制，影响"单文件"体积 |
| **视频编码（E1，已定）** | 沿用 **ffmpeg.exe** 的 filter graph（`overlay` 滚动静态长图 + `trim` + `concat` + `libx264`）。⚠️ 需处理 `libx264` 的 GPL 合规（R8）：以**独立子进程**方式调用可维持"单纯聚合"，但仍需为该二进制履行 GPL 义务；若要彻底规避则需回到 E3 |
| 内存读取 | 自己写 P/Invoke 薄层（推荐）；参考 `erfg12/memory.dll`（LGPL-2.1） |
| 音频（`atempo`/`adelay`） | ffmpeg 子进程一次调用（无原生等价物） |
| 音频预览 | NAudio；或 ffmpeg 转临时 wav（`.ogg` 在媒体基础下不稳） |
| 测试 | xUnit |

### 4.5 如果必须去掉 GPL：需要什么（R8 的可执行落地清单）

**先给结论：本项目里真正触发 GPL 的只有 `libx264`（`Config.kt` 的 `codec: libx264`）这一个组件。E1 的整条管线本身是 LGPL 干净的，所以不必推翻 E1。**

#### 4.5.1 为什么只需替换"编码器"，不用替换整个 ffmpeg

ffmpeg 官方许可页（`https://ffmpeg.org/legal.html`）写明：FFmpeg 本身是 **LGPL-2.1+**，只有当构建启用了那些"可选的 GPL 部分"时，**整个 FFmpeg 才变成 GPL**。合规清单第 1 条是"**不带 `--enable-gpl` 且不带 `--enable-nonfree`** 编译"，第 18 条是"确保你的程序没有使用任何 GPL 库（**尤其是 libx264**）"。

对照本项目 E1 实际用到的东西：

| 用到的能力 | 组件 | 许可 |
|---|---|---|
| `color`、`overlay`、`trim`、`concat`、`transpose`、`atempo`、`adelay` | libavfilter 原生滤镜 | **LGPL 部分**（非 GPL） |
| `mp4`/`mov` 封装 | libavformat | **LGPL 部分** |
| BGM 解码（mp3/ogg） | 原生解码器 | **LGPL 部分** |
| **H.264 视频编码** | **`libx264`** | ⚠️ **GPL-only（唯一问题点）** |

**所以"替换 GPL 组件" = 换掉编码器 + 换一个不带 `--enable-gpl` 的 ffmpeg 构建。** 静态长条 PNG + `overlay` 滚动动画 + `atempo/adelay` 音频处理**全部保留**。

#### 4.5.2 需要做的事情（四项）

**① 换用 LGPL 构建的 ffmpeg。**
ffmpeg.org 只提供源码；Windows 二进制来自 gyan.dev 与 BtbN，**两家都提供 GPL 与 LGPL 两套包**。注意：LGPL 构建里**必然没有 libx264/libx265**（libx264 需 `--enable-gpl` 才能编入），所以必须同时做 ②。

**验证方法（可立刻执行）**：
```
ffmpeg -version                      # 看打印出的 configure 行里有没有 --enable-gpl / --enable-nonfree
ffmpeg -encoders | findstr h264      # 看这个构建里有哪些 H.264 编码器可用
```
若 configure 行**含 `--enable-nonfree`**，该二进制**不可再分发**（只能自用），必须自建或让用户自备。

**② 换编码器。** 候选与取舍：

| 编码器 | 许可 | 画质/体积 | 需要什么 |
|---|---|---|---|
| `mpeg4`（FFmpeg 原生 MPEG-4 Part 2） | **LGPL（内置）** | 明显差于 x264，体积偏大 | **零新增依赖**，LGPL 构建自带；但 `-preset` 无效，改用 `-qscale:v` |
| `libopenh264` | BSD-2（Cisco 分发的二进制含其专利授权） | 面向实时会议，压缩率一般 | 需构建带 `--enable-libopenh264`（LGPL 构建通常**不含**）→ 可能要自建 ffmpeg |
| `libvpx-vp9` / `libsvtav1`（AV1） | BSD | **压过 x264** | 需构建带对应库；AV1 编码慢，需调并行 |
| `h264_nvenc` / `h264_amf` / `h264_qsv`（硬件） | 厂商 SDK：免费但**专有** | 接近 x264，速度极快 | 需构建含对应编码器；**须确认是否带 `--enable-nonfree`**，带则不可再分发；且依赖用户 GPU |
| **媒体基础 H.264（E3 路线）** | **系统内置，无第三方编解码器** | 好，可走硬件 | 需要放弃 E1 的"ffmpeg 滚动动画"，改为逐帧渲染（即放弃 E1） |

> 实用建议：**先用 `mpeg4` 验证"去 GPL 后整条管线还能跑通"**（零依赖、改动最小），再按画质需求决定是否投入自建带 `libvpx`/`openh264` 的 LGPL ffmpeg。硬件编码器是最接近 x264 的替代，但要按机器能力做运行时探测与回退。

**③ 做"编码器参数映射"（否则直接报错）。**
现在 `Config` 里的 `codecPreset: ultrafast`、`codecProfile: unknown` 是 **x264 专用**语义：`mpeg4` 不认 `-preset`；`libvpx` 用 `-deadline`/`-cpu-used`；`openh264` 有自己一套；硬件编码器用 `-preset p1..p7`/`-cq`。因此需要**按编码器建一张参数表**（不支持的参数就不传），并保留用户自定义通道。

**④ 若打包 ffmpeg，履行 LGPL 义务。** 官方清单共 18 条，与本项目相关的关键几条：
- 以**独立进程 / DLL 动态链接**方式使用（不要把 libavcodec 静态链进自己的程序）；
- 随附 FFmpeg 源码（与二进制**完全对应**，托管在同一站点）；
- 在 About 框与 EULA 中声明使用了 LGPLv2.1 下的 FFmpeg；
- **移除 EULA 中的反向工程禁令**（清单第 13 条）——这一条与本工具"读取进程内存"的功能观感直接相关，务必注意。

#### 4.5.3 两个容易混淆的点

1. **GPL ≠ 专利。** 换掉 x264 解决的是**著作权许可（GPL）**问题；**H.264 格式本身的专利问题与 x264 无关**——换成硬件编码器或媒体基础都不改变这一点。ffmpeg 官方的专利 FAQ 说得很直白：个人自用基本无需担心；**一旦是商业产品，MPEG LA 这类专利权人会非常积极地收费**。所以两条路满足的目标不同：
   - 目标 = "**彻底无 GPL**" → 换编码器即可（LGPL ffmpeg + 非 GPL 编码器），**E1 保留**；
   - 目标 = "**彻底无许可/专利负担**" → 需换**编码格式**（VP9/AV1 自身也有专利问题）、或走系统内置的媒体基础（把责任交给操作系统）、或只导出 LGPL 的无损中间格式（如 FFV1，文件极大）。
2. **还有一处"来源不明"的依赖**：`lib/osu-replay.jar` **没有源码、也没有可见的许可证声明**。它不是 GPL 问题，而是"来源不明 + 无法审计"的问题。**M3 解析层移植会把它整个替换掉**，届时该风险自动消失。

---

## 5. 游戏联动子系统设计（核心新增）

### 5.1 模块划分

```
ManiaReplayMaster.Game
├─ GameLocator             定位 osu! 根目录（进程映像路径 / %LOCALAPPDATA%\osu!）
│                          → Replays\ / Data\r\ / Songs\ / osu!.db / scores.db
│                          尊重 osu!.<user>.cfg 的 BeatmapDirectory
├─ ProcessMemory           P/Invoke：打开进程、读内存、枚举模块、枚举可读区域
├─ SignatureScanner        模块镜像内的特征码扫描（通配符），返回匹配地址表
├─ OsuStableReader         特征码 → 指针链 → 生成 GameSnapshot（§3.4.2 的表）
├─ GameStateWatcher        轮询 ~100–200ms，产出状态变化事件（含 isWatchingReplay、retries）
├─ ReplayDirectoryWatcher  FileSystemWatcher 盯 Data\r 与 Replays\ 的新增 .osr（不依赖内存）
├─ ScoreDatabaseReader     【新增·关键】解析 scores.db：谱面 MD5 → 本地成绩列表（含回放指针）
├─ ReplayResolver          §3.3 的四级定位算法（scores.db 指针 → 目录监听 → 头部打分 → 内存 dump）
└─ BeatmapLocator          内存文件夹名/文件名 → 直接定位；否则 osu!.db / 缓存索引 → MD5 定位
```

### 5.2 状态机与事件

```
NotRunning ──(进程出现)──► Idle ──► SongSelect ──► Playing ──► Results
                             │                        │
                             ├──► ScorePanelOpen  ◄────┘   ← 打开某条成绩的界面（本功能主触发点）
                             └──► WatchingReplay           ← 真正进入回放播放
```

`GameStateWatcher` 每轮产出 `GameSnapshot`：

```csharp
sealed class GameSnapshot
{
    public OsuGameState State;        // Idle / SongSelect / Playing / Results / ScorePanelOpen / WatchingReplay
    public string? BeatmapMd5;        // 当前谱面 MD5            ← Beatmap + 0x6C
    public string? BeatmapFolder;     // 歌曲文件夹名            ← Beatmap + 0x78
    public string? BeatmapOsuFile;    // .osu 文件名             ← Beatmap + 0x90
    public int? BeatmapId, BeatmapSetId, ObjectCount;
    public GameMode Mode;
    public int Mods;                  // ModsXor1 ^ ModsXor2
    public string? PlayerName;        // 当前成绩的玩家
    public int? Score, MaxCombo;      // 当前成绩的分数/连击
    public int[]? HitCounts;          // [300,100,50,geki,katu,miss]
    public DateTimeOffset? ScoreDate; // 当前成绩的日期
    public bool IsWatchingReplay;     // canRunSlowly + 0x46
    public int Retries;
}
```

### 5.3 成绩界面自动填充流程（本方案的主流程）

```
① 触发
   GameStateWatcher 检测到「谱面 MD5 变化」或「状态进入 ScorePanelOpen / WatchingReplay」
        ↓
② 定位谱面（内存优先，磁盘兜底）
   内存: BeatmapFolder + BeatmapOsuFile      → Songs\<folder>\<file>.osu   （最快，无需哈希扫描）
   兜底: BeatmapMd5 → osu!.db / 缓存索引      → 谱面文件路径
        ↓
③ 枚举该谱面的本地成绩（磁盘）
   ScoreDatabaseReader 读 scores.db → 该 BeatmapMd5 下的成绩列表
   每条含 { 玩家, Mods, 分数, 最大连击, 命中数, 日期, 回放文件指针 }
        ↓
④ 确定"你看的是哪一条"（内存 + 打分匹配）
   内存读到的 { 玩家, Mods, 分数, 最大连击, 日期 } 与 ③ 的列表逐条比对 → 唯一确定
   ▸ 若"选中成绩"的偏移不可用（§3.4.3 未知项 1）：
       用可读到的字段做打分排序；若仍不唯一，GUI 弹出该谱面的本地成绩列表供一键选择
   ▸ 若分数完全相同（同一成绩重打多次）：按日期 + Mods 区分，必要时也走候选列表
        ↓
⑤ 得到回放文件（磁盘）
   ③ 的回放指针 → Data\r\<加密名>.osr
   校验：解析该文件头部，核对 谱面 MD5 / Mods / 分数 / 玩家 —— 全部相符才接受
   兜底：扫描 Data\r + Replays\ 全部 .osr 头部，按字段打分匹配（§3.3 第 3 级）
        ↓
⑥ 预填 + 参数推导
   GUI 填入「回放路径 + 谱面路径」；并从回放/谱面自动带出：
       游戏模式 / 键数 / OD / 判定窗口 / 倍速(rate) / 镜像 / ScoreV2 / BGM 路径
   状态栏提示："已从 osu! 检测到：Kuit - Doin - Pine nut [ATTang's Lv.27]  ·  分数 458,556  ·  Mods: 无"
        ↓
⑦ 用户调整参数 → 点「渲染」
```

**三个开关**：`自动填充`（可关）、`自动开始渲染`（**默认关**，避免误触）、`内存读取`（可关，纯手动模式）。

**体验细节**：若 ④ 出现歧义，**默认仍然填入最高分候选**，同时在旁边显示"还有 N 条成绩"的下拉——比弹窗打断更好。

### 5.4 内存不可用时的降级链

| 级别 | 条件 | 能力 |
|---|---|---|
| 1 | 内存 + `scores.db` 都可用 | 完整自动填充，精度最高（含失败局"无文件"提示） |
| 2 | 内存失败，`scores.db` 可用 | 内存失败但能读到游戏进程 → 用"当前谱面"仍无法获取；退化为**监听新增 `.osr` + `osu!.db`** → 覆盖"刚打完"场景 |
| 3 | 只有文件系统 | 手动选择 → 自动解析 MD5 → 自动定位谱面（场景 A） |

> **这条降级链是安全垫**：即使内存特征码因游戏更新失效，工具也不会变成砖头。

---

## 6. GUI 设计

### 6.1 主界面（单窗口）

```
┌──────────────────────────────────────────────────────────────┐
│  Mania Replay Master                         [osu!: 已检测到●]│
├──────────────────────────────────────────────────────────────┤
│  回放文件  [ D:\osu!\Data\r\0f3a…9c.osr                  ▾]│  ← 自动填入
│  谱面文件  [ D:\osu!\Songs\1234567 Kuit - Doin - Pine nut\…] │  ← 自动定位
│  ┌ 检测到的成绩 ────────────────────────────────────────┐   │
│  │ Kuit · 458,556 · 135x · Mods:无 · 2020-10-03   [换一条 ▾]│  │  ← 歧义时的候选入口
│  └──────────────────────────────────────────────────────┘   │
│            ☑ 从 osu! 自动检测   ☐ 检测到后自动开始渲染        │
├──────────────────────────────────────────────────────────────┤
│  ┌ 参数 ─────────────────────────────────────────────────┐   │
│  │ 下落速度 [20] ─────●────   分辨率 [540]×[960]  FPS [60]│   │
│  │ 键位配色 ■■■■■   长按色 ■   漏键色 ■   暗色 notes ☐   │   │
│  │ 输出目录 [out] ▾        编码预设 [ultrafast ▾]         │   │
│  │ ▸ 高级（taiko / Malody / 分片 / 判定导出 / debug）      │   │
│  └───────────────────────────────────────────────────────┘   │
├──────────────────────────────────────────────────────────────┤
│  [ 渲染 ]  [ 仅判定（不编码） ]   进度 ███████░░░ 62% 预估 1:20│
│  日志：读取回放… 判定完成（300/100/50/miss = …） 编码中…       │
└──────────────────────────────────────────────────────────────┘
```

### 6.2 交互要点

- **成绩候选列表**：同谱面多条本地成绩时，用一个紧凑下拉列出（玩家 / 分数 / 连击 / Mods / 日期），一键切换，**改变选择即重新定位回放文件**。
- **参数面板直接映射 `Config` 全字段**，默认值与原版一致；「高级」折叠避免吓人。
- **实时预览**：参数改动只渲染首屏做静态预览（用当前窗口的静态长图即可，走 E1 时天然支持）。
- **进度**：复用原版 30（渲染）/60（拼接）两段语义，但换成真进度条 + 预计剩余时间。
- **可取消**：渲染跑在后台线程，`IProgress<T>` + `CancellationToken`（原版**不支持取消**，必修）。
- **完成后**：显示"打开视频 / 打开输出目录"按钮，替代原版"按回车"。
- **判定结果**：渲染前展示判定统计（各档数量 + 准确率），可导出 JSON 并一键调起 `judgementStatistics.exe` 画偏移分布图（原版功能）。
- **拖拽**：把 `.osr` 拖进窗口即开始解析。
- **无游戏时**：状态胶囊显示"未检测到 osu!"，一切照常手动可用。

---

## 7. 验证策略

### 7.1 M0：先造"尺子"（必须在写 C# 之前完成）

1. 给 Java 版补 `build.gradle.kts`（Gradle 8.2.1 已装，Kotlin 插件首次需联网），产出可运行 jar。
2. 加 `--benchmark` 模式：对 `test/` 的 5 组 fixture 输出**逐音符 JSON**（结构见附录 C）。
3. 输出提交为 `test/golden/*.json`——**从此成为唯一真相**。

验收：本机 `gradle run --args="--benchmark"` 一键重放。

### 7.2 差分测试（M4 门禁）

C# 判定层跑同一批 fixture，与 golden JSON 做**逐音符全字段比较**（不是"能跑通"这种弱断言）。任何字段不一致即失败并打印首个差异。这是保证"判定颜色不变"的唯一可靠手段。

### 7.3 游戏联动的验证（需要真机）

| 编号 | 验证项 | 方法 | 通过标准 |
|---|---|---|---|
| ~~V1~~ | ~~是否每局自动落盘~~ | **已查实**（§3.3）：通过的 solo 局自动写入 `Data\r` | 前提成立；改为确认自己机器上 `Data\r` 的实际路径与可读性 |
| V2 | 能否读到当前谱面身份 | 在选歌/游玩/结算各状态读 `GameSnapshot`，与界面比对 | MD5 / 文件夹 / `.osu` 文件名 / Mods 一致 |
| **V3** | **`scores.db` 能否解析出"成绩 → 回放指针"** | 用 `ScoreDatabaseReader` 解析真实 `scores.db`，与游戏内本地成绩列表逐条核对 | 能列出成绩且指针指向的文件确实存在 |
| **V4** | **"当前选中的成绩"能否确定**（§3.4.3 未知项 1） | 在本地排行榜点开不同成绩，看内存字段与 `scores.db` 比对是否唯一 | 若能唯一定位 → 全自动；若不能 → 走候选列表，功能仍达标 |
| V5 | 端到端：打开任意成绩 → 自动填对 | 对 ≥20 条不同成绩（含多难度、多 Mods、同名分数）测试 | Top-1 正确率 ≥ 95%，且**错误必须可被用户发现**（候选列表可见） |
| V6 | 例外场景 | 失败局 / Auto / 多人局 / 在线榜单他人成绩 | 明确提示"此局/此成绩本地没有回放文件"，不静默失败 |
| V7 | 版本鲁棒性 | 在 2–3 个 osu! 版本上跑 | 特征码失效时明确提示并降级，不崩不误导 |

**注意：本机没有 osu!（已实测）**，V2–V7 必须在你的游戏机上做。我可以在无 osu! 环境用**自建的 32 位测试目标进程**验证读取器骨架（`OpenProcess` / `ReadProcessMemory` / 特征码扫描 / 指针链 / `scores.db` 解析），**但真实特征码与成绩界面行为只能在你机器上调**。

### 7.4 渲染 parity（M5 门禁）

- **结构对比**：同一输入，C# 与 Java 版输出 PNG 的尺寸、分片数量、`windowFrameCount`、`gameWidth` 完全一致；
- **像素对比**：允许抗锯齿差异，用容差阈值 + 人工抽查（键位位置、判定颜色、LN 连线条、时间戳文字位置）；
- **视频对比**：时长、帧数、分辨率、音画对齐一致；码率/文件大小允许差异；
- **第三方参考**：线上渲染站 keytoix.vip/mania/ 跑的就是这套 Java 引擎。

---

## 8. 里程碑与实施顺序

**核心策略：先做壳，再换心。** 让"GUI + 成绩界面自动识别"在 **M2 就可用**（M2a 完全不依赖内存），而不是等整个内核移植完。

| 阶段 | 范围 | 交付物 | 验收标准 | 预估 |
|---|---|---|---|---|
| **M0** | 基准线 + Java 可构建化 | `build.gradle.kts`、`--benchmark`、`test/golden/*.json` | 本机一键产 golden JSON | 1–2 天 |
| **M1** | C# WPF 外壳（手动模式） | 主界面 + 参数面板 + 进度 + 取消 + 暂调 Java jar | 无游戏环境下选文件→渲染→出 mp4，功能与现状等价 | 4–6 天 |
| **M2a** | **游戏联动：文件系统层**（不依赖内存） | `GameLocator` + `ReplayDirectoryWatcher` + `.osr` 头部解析 + **`ScoreDatabaseReader`** + `BeatmapLocator` + 自动填充 UI | 真机：打开成绩界面 / 打完一局后自动填入回放与谱面；**完全不需要内存读取** | 5–8 天 |
| **M2b** | **游戏联动：内存层**（去歧义 + 提前量） | `ProcessMemory`/`SignatureScanner`/`OsuStableReader`/`GameStateWatcher`（§3.4.2 表）+ 与 M2a 融合 | 真机：读到游戏状态、当前谱面 `.osu` 绝对路径、当前成绩字段；特征码失效时明确提示并退回 M2a | 5–8 天（含真机校准） |
| **M3** | Core 移植：解析层 | C# `.osr`(含 LZMA)/`.osu`/`.mr`/`.mc`/MD5/ZIP | 5 组 fixture 的 MD5、音符数、事件数、头部字段与基准一致 | 3–4 天 |
| **M4** | Core 移植：判定层 | Mania/Taiko/Malody Judger + 准确率 | **逐音符差分 = golden 全等**；`OsuManiaJudgementTest` 5 用例全覆盖 | 4–7 天 |
| **M5** | 渲染 + 编码（"换心"，**E1 路线**） | SkiaSharp 复刻 `Render.kt` 的静态长条 PNG 分片绘制 + **保留 ffmpeg filter graph**；摘掉 Java 依赖 | §7.4 全部通过；输出与 Java 版视觉等价；**仍依赖 ffmpeg** | 5–8 天 |
| **M6** | 打包发布（可选） | 自包含发布、ffmpeg 打包策略与许可说明、更新检查、（可选）服务端协议兼容 | 干净机器双击即用 | 2–4 天 |

合计约 **30–49 个工作日**（单人）。关键路径上最容易超时的是 **M2a/M2b（`scores.db` 解析与真机校准）** 与 **M4（判定保真）**。M2a 不依赖内存，风险最低而用户价值最高，**建议最先做**。

**可选的最短路径**：只做 M0 + M1 + M2a + M2b，内核永远调用 Java jar → **3–4 周**即可得到一个"原生 GUI + 打开任意成绩自动识别回放"的工具（代价是保留 JVM 依赖）。

### 8.1 开工前建议先做的 5 个技术验证（各 0.5–1 天）

1. **`scores.db` 解析 spike**（本需求最关键）：写一个小程序解析真实 `scores.db`，打印"谱面 MD5 → 成绩列表 + 回放指针"，与游戏内列表核对。**这一个 spike 决定 M2a 的主体能否按设计走。**
2. **逐帧渲染 + 媒体基础编码最小样例**（Skia 出帧 → `IMFSinkWriter` → H.264 mp4）。需确认 `RGB32` 输入到 `H.264` 输出是否需要显式插色彩转换（文档没有承诺自动转换，**必须实测**）。
3. **内存读取骨架在无 osu! 环境自测**：自建一个 32 位测试目标进程（放已知结构与特征码），验证 `VirtualQueryEx` 区域遍历、分块读、4 字节指针链、特征码扫描。**能在没有 osu! 的机器上测掉 90% 读取器逻辑，非常划算。**
4. **真机验证**：§7.3 的 V2–V7（特征码校准、选中成绩偏移、例外场景）。
5. **打包体积实测**：WPF + SkiaSharp + ffmpeg（仅音频）的自包含发布体积，确认分发形态（很可能是"启动器 exe + natives 目录"打 zip）。

---

## 9. 风险登记表

| 编号 | 风险 | 概率 | 影响 | 应对 |
|---|---|---|---|---|
| R1 | osu! 更新导致特征码/偏移失效 | 高（长期必然） | 中 | 特征码扫描 + 版本表做成**可远程更新的数据** + 明确提示 + 降级到文件系统（§5.4） |
| R2 | 开发机无 osu!、.NET SDK 过旧 | **已确认** | 中 | 需装 .NET 8+ SDK；需一台 osu!stable 测试机 |
| **R3** | **`scores.db` 格式版本化且无官方文档** | **中** | **高（本需求核心）** | 参考 lazer 的 MIT 源码实现；写解析 spike（§8.1-1）；解析失败时退回"头部打分匹配"（§3.3 第 3 级），功能仍成立 |
| **R4** | **"当前选中的成绩"没有稳定偏移** | **中** | **中** | 退化为"`scores.db` 列出候选 + 内存字段打分 + GUI 一键选择"，功能仍达标（§3.4.3） |
| R5 | 失败局 / Auto / 多人局不落盘（已查实） | 高 | 中 | 明确提示"此局没有回放文件，请按 F2 导出"；或走"内存 dump 回放数据自行合成 `.osr`"（多一个 `IReplaySource` 实现） |
| **R6** | **他人/在线榜单成绩本地无回放文件** | **高** | **中** | 明确列为**不支持**并在 UI 说明；不承诺不误导（§2.4） |
| R7 | AWT→Skia 无法像素级一致 | 高 | 低 | parity 目标定为"判定全等 + 视觉等价"，不做像素 diff（§4.2） |
| R8 | ffmpeg 打包的 GPL 传染（`libx264`）｜**已选 E1，故此项保留** | 中 | 中 | 已定 E1 → 四选一：① 以**独立进程**调用未修改的 ffmpeg，维持"单纯聚合"，同时按 GPL 为该二进制履行义务；② **不打包** ffmpeg，要求用户自行安装（沿用现策略）；③ 走 E3；④ **去掉 GPL 但保留 E1**——只需把**编码器**换成非 GPL 的（LGPL ffmpeg 构建 + `mpeg4`/硬件编码器等），**完整清单见 §4.5**。**非阻塞项，但发布前必须落地** |
| R9 | 媒体基础**没有** `atempo` 等价物 | 高 | 低 | 音频保留一次 ffmpeg 预处理；实时模式不需要它 |
| R10 | 媒体基础 `RGB32 → H.264` 是否自动插转换器无文档保证 | 中 | 低 | §8.1-2 先实测；不行就显式用 `ID3D11VideoProcessor` 转 NV12 |
| R11 | 上游仓库仍在演进，长期分叉 | 中 | 低 | Apache-2.0 允许分叉；新代码独立目录/新仓库，保留上游为参照 |
| R12 | `Data\r` 从不清理 → 磁盘膨胀 + 去重困难 | 中 | 低 | 用内容哈希去重；提供"检测到但未使用"的缓存清理建议 |
| R13 | 将来要做"游玩中实时叠加" | 低（你已明确不是） | 高 | 需改逐帧 + 重新评估语言（C++ 在实时权重下反超）；作为独立里程碑 |
| R14 | EULA 禁止逆向 与 "读进程内存" 观感冲突 | 低 | 中 | ffmpeg LGPL 合规清单第 13 条要求移除 EULA 的反向工程禁令；README 明确只读、不注入 |
| R15 | SmartScreen 首次发布必然报警 | 高 | 低 | 接受首次警告；可选 Azure Artifact Signing（比 EV 证书便宜） |
| R16 | 读内存触发封号担忧（玩家观感） | 低（技术风险低） | 中 | **永不注入、永不写内存**；README 用官方规则原文说明"只读不等于作弊"；提供"关闭内存读取"开关 |

---

## 10. 需要你确认或提供的事项

**硬前置**

1. ~~是否允许在本机安装 .NET 8+ SDK？~~ **已确认：允许安装。** 本机现状：仅 SDK 5.0.100（x86 目录），运行时最高 10.0.10。
2. **有没有一台装了 osu!stable 的机器用于联调？** 本机已确认没有 osu!。M2b 的特征码校准与 V2–V7 必须在那台机器上做——**但 M2a 不需要**（`scores.db` + 文件系统即可，只要能把 `scores.db` 拷出来就能在本机开发）。

**需要你拍板**

3. ~~编码路线选 E1 / E2 / E3？~~ **已确认：全线 E1**（照抄 ffmpeg filter graph，保真优先）。`libx264` 的 GPL 合规问题因此**保留为待处理风险 R8**，不作为阻塞项。
4. **接受"内存读取失败时自动降级到文件系统 + `scores.db`"这条安全垫吗？** 我强烈建议接受——它保证工具不会因一次游戏更新而报废。
5. **`scores.db` 在游戏运行时可读吗？**（osu! 可能占用/缓存它）→ 需要在真机上确认；若被锁，可能要"启动时抓取一份副本"或等游戏关闭。**这是我目前无法在本机回答的问题。**
6. **是否需要保留 keytoix.vip 在线渲染站的文件协议？**（`progress.txt`/`error.txt`/`game_mode.txt`/`judgement_*.json`）
7. **osu!taiko 与 Malody 在重写第一版是否都保留？** 建议：解析与判定**全部保留**（fixture 齐全、成本低），内存联动只做 osu!mania + osu!stable。
8. **代码放哪里？** 建议在现有仓库新建 `src/csharp/` 与 `docs/`（Apache-2.0 不变），或另开仓库。

**回答完第 1–2 条我就可以进入 M0 开工；第 3 条决定 M5 形态；第 5 条是本需求在真机上第一个要压的未知。**

---

## 附录 A：调研已完成项 / 仍待确认项

### A.1 已查实（含出处）

| 结论 | 出处 |
|---|---|
| `.osr` / `.osu` / `.mr` / `.mc` 格式可自实现，无需闭源库 | 本机实测（附录 B） |
| 回放头里的谱面 MD5 == `.osu` 文件字节 MD5 | 本机 `certutil` 逐文件校验，4/4 精确匹配 |
| **`scores.db` 保存"本地成绩 → 加密回放文件"的指针；删除它会丢失所有未导出的回放** | osu! wiki `Client/Program_files/en.md` |
| osu!stable 每局**通过**的 solo 自动把回放写入隐藏目录 `Data\r`（`.osr`+`.osg`，文件名加密）；`Replays\` 仅 F2 导出才有 | osu! wiki `Gameplay/Replay/en.md`；lazer `LegacyScoreImporter.cs` 的 `Path.Combine("Data","r")` |
| **失败局 / Auto / 多人局不会**自动落盘 | 同上 |
| 服务器回放会被下载到**临时位置并在播完后删除** | 同上 |
| `BeatmapDirectory` 可挪走 `Songs`；**没有** `ReplayDirectory` 这类键 | osu! wiki cfg 页；lazer `StableStorage.cs` 只解析 `BeatmapDirectory` |
| osu! 根目录可由进程映像路径或 `%LOCALAPPDATA%\osu!` 推出 | osu! wiki `Client/Program_files/en.md` |
| osu!lazer 有**官方** localhost WebSocket（`OSU_WEBSOCKET_SERVER=1`，端口 49727，仅谱面状态） | `osu.Desktop/IPC/OsuWebSocketProvider.cs`、`osu.Game/IPC/WebSocketServer.cs`、`osu.Desktop/Program.cs` |
| osu!lazer 存储按 SHA-256 内容寻址 + `client.realm`，**无 `Songs`/`Replays` 目录**，无自动导出 `.osr` | lazer `File_storage` wiki、`OsuGameBase.cs` |
| osu!stable 的**特征码与指针链**（§3.4.2 的表） | tosu `packages/tosu/src/memory/stable.ts`（在维护）、gosumemory `memory/read.go` |
| 特征码会随版本漂移；内存里的 Songs 路径不可靠 | 两项目表不一致（`objectCount` 0xF8 vs 0xFC）；gosumemory 的兼容 hack |
| WOW64 / 内存读取的 API 行为与坑（§3.4、§3.4.1） | 微软文档 |
| 媒体基础**没有** `atempo` 等价物；`libx264` 是 GPL-only | 微软文档 + ffmpeg.org 合规清单 |
| Malody 内存读取有 MIT 参考实现与逐字段偏移 | `TKazer/Malody-Reader`（C++，MIT） |
| 可复用项目的许可证（§3.5 的表） | GitHub API / 各仓库 LICENSE |

> 方法论修正：**osu! 官方 wiki 的渲染页有登录墙，但原始 Markdown 在 GitHub 上完全可读**（`raw.githubusercontent.com/ppy/osu-wiki/master/wiki/...`）——这是拿到上述事实的关键。本会话 `web_search` 无 API key，所有外部结论均来自直接抓取的具体 URL。

### A.2 仍需在你的机器上确认（按优先级）

1. **`scores.db` 在游戏运行时是否可读**（§10 第 5 条）——本需求第一个要压的未知。
2. **`scores.db` 的实际二进制格式**（版本化、无官方文档）→ 参考 lazer 的 MIT 源码（§3.3）。
3. **"当前选中的成绩"是否有稳定偏移**（§3.4.3）→ 没有就退化为候选列表。
4. **特征码真机校准**：§3.4.2 表来自 tosu/gosumemory，**必须在你的 osu! 版本上逐条验证**。
5. `Data\r` 的实际路径与可访问性（默认隐藏目录）。
6. 例外场景：失败局 / Auto / 多人局 / 在线榜单他人成绩（§7.3 V6）。
7. `osu!.db` 各版本二进制格式（MD5 → 歌曲文件夹的快速通道）。
8. osu! 安装目录发现的注册表键位与兜底顺序（本机未安装游戏，无法确认）。
9. `.osr` 中 `perfect` 字节与 count 字段的精确语义（本次 4 个样本 `perfect` 均为 0）。
10. Malody 在 Windows 上的实际数据目录（无公开文档）。
11. 媒体基础 `RGB32 → H.264` 是否自动插色彩转换（§8.1-2、R10）。

---

## 附录 B：`.osr` 实测解析结果（本机运行，可复现）

用自写 Python 探针解析 `test/` 全部 4 个 `.osr`：

| 文件 | 谱面 MD5（与 `.osu` 文件 MD5 一致 ✓） | 玩家 | Mods | 事件数 | 解压后 |
|---|---|---|---|---|---|
| `- ck - Carnation (ck remix) [LN Master] (2021-12-06) OsuMania.osr` | `fc7ec551f8e0e485719c1b92386fe370` | *(空)* | `0x20000000` | 9,274 | 150,088 B |
| `Kuiiiiteeee - Sound Horizon - Raijin no Hidariude […] (2018-09-23) OsuMania.osr` | `05f0366dd3e7b506619a7151d1d5dcda` | `Kuiiiiteeee` | `0` | 11,410 | 183,871 B |
| `Kuit - Doin - Pine nut [ATTang's Lv.27] (2020-10-03) OsuMania.osr` | `587b6f84904d37fac7310f2a4aee799a` | `Kuit` | `0` | 9,869 | 119,052 B |
| `Kuit - Various Artists - 4K LN Dan Courses v2 - […] (2022-01-08) OsuMania.osr` | `ec14ce28a853cf7ac1e74adbed61a820` | `Kuit` | `0x20000000` | 46,247 | 560,705 B |

- LZMA 参数一致：props `5D 00 00 20 00`，字典 2 MB，`FORMAT_ALONE`。
- 所有文件以 `-12345|0|0|…` 结束标记收尾。
- 4 个 `.osu` 谱面文件 MD5 与回放头**逐个精确匹配**；Malody `20220812-110445-Sewing.mr` 内嵌 MD5 == `20220812-110445-Sewing.osu` 的 MD5（`b88576122c32d651f50322ef3f43424f`）✓。

**这些字段正是 §3.3 第 3 级"头部打分匹配"所能依赖的全部依据**（MD5 / 玩家 / Mods / 分数 / 连击 / 命中数 / 时间戳）——已在 4 个真实文件上验证可读出。

## 附录 C：M0 基准导出 JSON 结构（建议）

`--benchmark` 模式对每组 fixture 输出一个 JSON，作为 C# 侧差分测试的唯一真相（全部为**判定完成后**的快照）：

```jsonc
{
  "fixture": "Kuit - Doin - Pine nut [ATTang's Lv.27] (2020-10-03) OsuMania.osr",
  "beatmapMd5": "587b6f84904d37fac7310f2a4aee799a",
  "gameMode": "osu!mania",
  "mode": 3,
  "key": 4,
  "od": 8.0,
  "rate": 1.0,
  "mirror": false,
  "isScoreV2": false,
  "judgementWindow": [16, 34, 67, 97, 121, 158],
  "delay": 1000,
  "durationMs": 123456,
  "acc": 0.9871,
  "judgementCounts": { "max": 781, "300": 0, "200": 241, "100": 9, "50": 0, "miss": 279 },
  "notes":      [ { "index":0, "timeStamp":0, "column":0, "duration":0,
                    "judgementStart":0, "judgementEnd":-1,
                    "offSetStart":12, "offSetEnd":0,
                    "showAsLN":false, "relatedActionIndex":3 } ],
  "replayData": [ { "index":0, "timeStamp":0, "column":0, "duration":100,
                    "judgementStart":0, "judgementEnd":-1,
                    "offSetStart":12, "offSetEnd":0,
                    "showAsLN":true, "relatedNoteIndex":3 } ]
}
```

要点：
- `notes` 与 `replayData` **都要导出**——双方向都会被判定过程原地修改，只导一边会漏掉一半状态；
- 用 `index` 互指而不是嵌套对象，避免循环引用（原版 `relatedActionOrNote` 是**双向引用**，直接 JSON 化会无限递归——导出时必须绕的坑）；
- 浮点（`acc`/`od`/`rate`）以原始精度输出，比较用固定容差（如 1e-9）而非字符串相等；
- Malody / taiko 额外导出各自特有字段（`taikoIsRed`、`duelNoteIndex`、Malody 判定表）。

## 附录 D：本机环境实测记录

| 项 | 结果 |
|---|---|
| OS | Windows 11 家庭版 中文版，10.0.26200 |
| Java | 17.0.7 LTS |
| Gradle | 8.2.1（`C:\gradle-8.2.1`） |
| kotlinc / Maven | 未安装 |
| ffmpeg | `C:\ffmpeg\ffmpeg.exe` |
| .NET SDK | 仅 5.0.100（x86 路径）；运行时 5.0.0 / 6.0.35 / 10.0.10 |
| MSVC `cl` | 不存在 |
| cargo | 存在 |
| Python | 3.11 |
| osu! | **未安装**（注册表无关联、无进程、常见目录均不存在） |
| 仓库构建产物 | 无 jar、无构建脚本，仅有 IntelliJ `.iml` + artifact 配置 |
| 工作区权限 | 启动时根目录缺当前用户完全控制项，已一次性修复（文件内容与所有者未变；撤销命令见 `D:\Projects\Java\dsh-acl-recovery`） |

## 附录 E：本版相对 v1 的变更

| 变更 | 原因 |
|---|---|
| 需求从"打完一局自动出片"改为"**打开任意成绩界面自动识别回放 + 谱面**" | 你的最新澄清（§2.2） |
| **`scores.db` 从"较低实用性"提升为方案核心**（§3.3、§5.1、M2a） | 它是"成绩 → 加密回放文件"的索引，是"任意成绩"可行的关键 |
| 新增 §2.4 边界与限制（他人/在线成绩不支持） | 避免期望落空 |
| M2 拆为 **M2a（文件系统，低风险）** 与 **M2b（内存，需真机）** | 让核心体验尽早可用且不被内存风险阻塞 |
| 新增 §3.4.2 特征码与指针链表、§3.4.3 未知项 | 调研完成 |
| 新增 R3/R4/R6（`scores.db` 格式、选中成绩偏移、他人成绩） | 新需求带来的新风险 |
| 决策锁定：Windows 原生重写 + C#/.NET 8 + WPF；Java GUI 路线否决 | 你的指示"稳定为 Windows native 重写" |
| **编码路线锁定 E1（全线照抄 ffmpeg）；.NET 8 SDK 允许安装** | 你的决定；对应 §0 决策表、§4.2、§4.4、M5、R8、§10 的更新 |
| **新增 §4.5"如果必须去掉 GPL：需要什么"** | 应你的提问补充。核心结论：本项目只有 `libx264` 触发 GPL，E1 的滤镜/封装/解码全是 LGPL，**换编码器即可，不必放弃 E1**；并给出四项落地清单与"GPL ≠ 专利"的区分 |
| 新增 M6 里程碑 M2a/M2b 拆分后的工期重估（30–49 天） | 反映新需求 |

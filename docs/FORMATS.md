# osu!stable 数据库与内存读取

本页记录当前 C# 读取器使用的布局，曾以 stable b20260924 核对。游戏更新可能改变布局；旧版本的兼容分支并不代表每个历史版本都已验证。

## 二进制基础类型

所有整数和浮点数均为小端；bool 占 1 字节。legacy string 用 `0x00` 表示空值，或 `0x0B + 无符号 LEB128 字节长度 + UTF-8 数据`。日期为 .NET ticks（每 tick 100 ns），除非字段另有说明。

## scores.db

实现：[ScoreDatabaseReader.cs](../src/ManiaReplayMaster.Game/ScoreDatabaseReader.cs)。
文件为 `int32 版本 + int32 谱面分组数`，每个分组依次包含谱面 MD5 字符串、int32 成绩数、成绩记录。

| # | type | field | notes |
|---|---|---|---|
| 1 | `byte` | `mode` | 0=osu, 1=taiko, 2=catch, 3=mania |
| 2 | `int32` | `version` | client build version (YYYYMMDD) that wrote the score |
| 3 | legacy string | `beatmapMd5` | equals the enclosing group's MD5 |
| 4 | legacy string | `playerName` | may be a 0-length string (valid even when empty) |
| 5 | legacy string | `replayMd5` | replay data hash — see §4 |
| 6 | `uint16` | `count300` | |
| 7 | `uint16` | `count100` | mania: 100s |
| 8 | `uint16` | `count50` | |
| 9 | `uint16` | `countGeki` | mania: MAX / 320s |
| 10 | `uint16` | `countKatu` | mania: 200s |
| 11 | `uint16` | `countMiss` | |
| 12 | `int32` | `totalScore` | |
| 13 | `uint16` | `maxCombo` | |
| 14 | `bool` (1 byte) | `perfect` | 0/1 only, observed |
| 15 | `int32` | `mods` | legacy mod bitmask |
| 16 | legacy string | `hpGraph` | nullable legacy string |
| 17 | `int64` | `dateTicks` | .NET `DateTime` ticks, UTC |
| 18 | `int32` | `replayLength` | byte-array length; usually -1 (external replay) |
| 19 | `int64` | `onlineId` | the score id after upload; 0 if not uploaded |

`replayLength > 0` 时先跳过相应字节的内嵌数据，再读 online ID。记录版本 >= 20140721 时 online ID 为 int64，>= 20121008 且低于该版本时为 int32，更早则无该字段。

本地回放路径通常为：

```text
Data/r/<beatmapMd5>-<dateTicks - 504911232000000000>.osr
```

这里使用谱面 MD5，而非 replay MD5。缺失索引时会扫描回放头进行匹配。数据库可能在游戏保存时被重写；读取器使用 FileShare.ReadWrite，并检查截断、计数及剩余字节，不把部分结果当作完整索引。

## osu!.db

实现：[BeatmapDatabaseReader.cs](../src/ManiaReplayMaster.Game/BeatmapDatabaseReader.cs)。
文件头依次为 int32 版本、int32 文件夹数、bool 账户解锁标志、int64 解锁日期、玩家字符串、int32 谱面数。b20260924 的记录没有前置记录长度，记录结束后文件带一个 int32 权限字段。

```csharp
// version-gated parts are marked; this build has version = 20260924
string  artist;              // 1
string  artistUnicode;       // 2
string  title;               // 3
string  titleUnicode;        // 4
string  creator;             // 5
string  difficultyName;      // 6
string  audioFileName;       // 7   may be null
string  md5;                 // 8   32 lowercase hex == MD5 of the .osu file bytes
string  osuFileName;         // 9   file name inside folderName
byte    rankedStatus;        // 10
ushort  countHitCircles;     // 11
ushort  countSliders;        // 12  mania: hold notes
ushort  countSpinners;       // 13
long    lastModifiedTicks;   // 14  .NET DateTime ticks (UTC)
float   approachRate;        // 15  ┐
float   circleSize;          // 16  │ version >= 20140609: 4x float32
float   hpDrainRate;         // 17  │ older: 4x byte
float   overallDifficulty;   // 18  ┘
double  sliderVelocity;      // 19
[star rating blocks x4]      // 20  version >= 20140609 (see below)
int32   drainTimeSeconds;    // 21
int32   totalTimeMs;         // 22
int32   previewTimeMs;       // 23
int32   timingPointCount;    // 24
timingPoint[timingPointCount]; // 25 see below
int32   beatmapId;           // 26  -1 or 0 when not submitted
int32   beatmapSetId;        // 27  -1 when not submitted
int32   threadId;            // 28  always 0 here
byte    gradeOsu;            // 29  last achieved rank per ruleset
byte    gradeTaiko;          // 30
byte    gradeCatch;          // 31
byte    gradeMania;          // 32
ushort  localBeatmapOffset;  // 33
float   stackLeniency;       // 34  e.g. 0.7
byte    mode;                // 35  0=osu 1=taiko 2=catch 3=mania
string  source;              // 36  e.g. "SOUND VOLTEX VIVID WAVE"
string  tags;                // 37  may be null
ushort  onlineOffset;        // 38
string  titleFont;           // 39  may be null
bool    unplayed;            // 40
long    lastPlayedTicks;     // 41  0 == never played
bool    osz2;                // 42
string  folderName;          // 43  Songs subfolder (may contain '\')
long    lastCheckedTicks;    // 44
bool    ignoreSound;         // 45
bool    ignoreSkin;          // 46
bool    disableStoryboard;   // 47
bool    disableVideo;        // 48
bool    visualOverride;      // 49
// (version < 20140609: ushort — absent in this build)
int32   unknownTail;         // 50  semantics NOT verified 
byte    unknownByte;         // 51  always 0 here 
```

四个星级块分别对应 osu、taiko、catch、mania，每块为 int32 数量及该数量的键值对。键为 `0x08 + int32 mods`，值可以为 `0x0C + float32` 或 `0x0D + float64`；不可固定跳过 14 字节。每个 timing point 为 double beatLength、double offset 和 bool regular，共 17 字节。

谱面文件位置由 Songs 目录、folderName 与 osuFileName 组合，MD5 对照文件原始字节。

## 进程内存

[SignatureTables.cs](../src/ManiaReplayMaster.Game/Memory/SignatureTables.cs) 保存签名和字段布局，[OsuStableReader.cs](../src/ManiaReplayMaster.Game/Memory/OsuStableReader.cs) 负责读取。参考来源与许可证见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

- osu!stable 是 32 位进程，指针宽度为 4 字节。原生指令签名位于 JIT 内存区域，需要扫描进程的可读区域。
- CLR 字符串在对象 +4 处保存 int32 长度，+8 处保存 UTF-16 字符。
- 成绩界面与游玩中的成绩对象链不同；成绩 mods 与菜单 mods 也分别读取。
- 成绩对象日期使用 DateTime.FromBinary 解码，不能直接把包含 Kind 位的值视为 ticks。
- 内存提供成绩身份；实际回放路径仍需本地数据库或回放文件。没有本地回放的在线成绩无法凭内存生成 `.osr`。

使用 `./build.ps1 gamecheck -- memory32` 验证合成 x86 目标，使用 `./build.ps1 gamecheck -- live --poll 3` 在已打开成绩界面的游戏上进行诊断。后者需人工启动游戏，不属于默认测试。

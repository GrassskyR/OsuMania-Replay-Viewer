// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using ManiaReplayMaster.Core.Beatmap;
using ManiaReplayMaster.Core.Judger;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Core.Replay;

namespace ManiaReplayMaster.Core.Replay;

/// <summary>Port of <c>me.replaymaster.replay.OsuReplayReader</c>.</summary>
public sealed class OsuReplayReader : IReplayReader
{
    public string Extension => "osr";

    /// <summary>Port of <c>mapDifficultyRange</c>. The truncation to int is deliberate.</summary>
    private static double MapDifficultyRange(double od, double min, double mid, double max, int mod)
    {
        var difficulty = HasMod(mod, OsuMods.HardRock)
            ? Math.Min(10.0, od * 1.4)
            : HasMod(mod, OsuMods.Easy)
                ? Math.Max(0.0, od / 2)
                : od;

        var result = difficulty > 5
            ? mid + (max - mid) * (difficulty - 5) / 5
            : difficulty < 5
                ? mid - (mid - min) * (5 - difficulty) / 5
                : mid;

        return (int)result;
    }

    private static bool HasMod(int mods, OsuMods mod) => (mods & (int)mod) > 0;

    /// <summary>
    /// Port of <c>getJudgement</c>. The original notes "this algorithm may have an error less
    /// than 1 ms"; the intermediate <c>.toInt()</c> truncations are part of the observable result.
    /// </summary>
    public static double[] GetJudgement(double od, OsuReplay replay, int beatMapType, bool isScoreV2)
    {
        if (beatMapType == BeatMap.TypeMania)
        {
            if (isScoreV2)
            {
                return new[]
                {
                    MapDifficultyRange(od, 22.4, 19.4, 13.9, replay.Mods),
                    MapDifficultyRange(od, 64.0, 49.0, 34.0, replay.Mods),
                    MapDifficultyRange(od, 97.0, 82.0, 67.0, replay.Mods),
                    MapDifficultyRange(od, 127.0, 112.0, 97.0, replay.Mods),
                    MapDifficultyRange(od, 151.0, 136.0, 121.0, replay.Mods),
                    MapDifficultyRange(od, 188.0, 173.0, 158.0, replay.Mods)
                };
            }

            var modeRate = HasMod(replay.Mods, OsuMods.HardRock)
                ? 1.0 / 1.4
                : HasMod(replay.Mods, OsuMods.Easy)
                    ? 1.4
                    : 1.0;

            var windows = new[] { 16.0, 34.0, 67.0, 97.0, 121.0, 158.0 };
            var result = new double[windows.Length];
            for (var index = 0; index < windows.Length; index++)
            {
                var r = windows[index];
                if (index != 0)
                {
                    r += 3 * (10 - od);
                }

                result[index] = (int)(r * modeRate);
            }

            return result;
        }

        return new[]
        {
            MapDifficultyRange(od, 50.0, 35.0, 20.0, replay.Mods),
            MapDifficultyRange(od, 120.0, 80.0, 50.0, replay.Mods),
            MapDifficultyRange(od, 135.0, 95.0, 70.0, replay.Mods)
        };
    }

    public IJudger GetJudger(BeatMap beatMap, ReplayModel replayModel, Config? config = null) => beatMap.Type switch
    {
        BeatMap.TypeMania => new OsuManiaJudger(beatMap, replayModel, config),
        BeatMap.TypeTaiko => new OsuTaikoJudger(beatMap, replayModel, config),
        _ => throw new InvalidOperationException("Unsupported beatmap type for the osu! judger.")
    };

    public string ReadBeatMapMd5(string replayPath)
    {
        var replay = OsuReplayParser.Parse(replayPath);
        return replay.BeatmapHash ?? string.Empty;
    }

    public ReplayModel ReadReplay(string path, BeatMap beatMap, Config? config = null)
    {
        var replay = OsuReplayParser.Parse(path);
        var raw = OsuReplayParser.Decompress(replay);
        var actions = OsuReplayParser.ParseActions(raw);

        if ((replay.Mode == OsuMode.Osu && beatMap.Type != BeatMap.TypeOsuStd)
            || (replay.Mode == OsuMode.Taiko && beatMap.Type != BeatMap.TypeTaiko)
            || (replay.Mode == OsuMode.OsuMania && beatMap.Type != BeatMap.TypeMania))
        {
            throw new ReplayModeNotMatchedException(replay.Mode.ToString(), beatMap.GameMode);
        }

        if (replay.Mode != OsuMode.OsuMania && replay.Mode != OsuMode.Taiko)
        {
            throw new ModeNotSupportedException(replay.Mode.ToString());
        }

        long current = 0;
        var holdBeginTime = new long[beatMap.Key];
        var list = new List<Note>();

        // The original iterates `0 .. actions.size - 2`, i.e. it intentionally drops the
        // final (end-marker) action.
        for (var i = 0; i <= actions.Count - 2; i++)
        {
            var action = actions[i];
            int x;
            switch (beatMap.Type)
            {
                case BeatMap.TypeMania:
                    x = (int)action.X;
                    break;
                case BeatMap.TypeTaiko:
                    x = action.Z;
                    break;
                default:
                    throw new InvalidOperationException("Unsupported beatmap type.");
            }

            current += action.W;

            for (var j = 0; j < beatMap.Key; j++)
            {
                int column;
                switch (beatMap.Type)
                {
                    case BeatMap.TypeMania:
                        column = j;
                        break;
                    case BeatMap.TypeTaiko:
                        column = j == 0 ? 1 : j == 1 ? 0 : j;
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported beatmap type.");
                }

                if ((x & 1) != 0)
                {
                    if (holdBeginTime[column] == 0L)
                    {
                        holdBeginTime[column] = current;
                    }
                }
                else
                {
                    if (holdBeginTime[column] != 0L)
                    {
                        list.Add(new Note(holdBeginTime[column], column, current - holdBeginTime[column]));
                        holdBeginTime[column] = 0L;
                    }
                }

                x /= 2;
            }
        }

        SortStable(list);

        var rate = HasMod(replay.Mods, OsuMods.DoubleTime)
            ? 1.5
            : HasMod(replay.Mods, OsuMods.HalfTime)
                ? 0.75
                : 1.0;

        var scoreV2 = replay.HasScoreV2;
        var judgement = GetJudgement(beatMap.Od, replay, beatMap.Type, scoreV2);
        var mirror = replay.HasMirror;

        var replayModel = new ReplayModel(list, rate, mirror, judgement, scoreV2);
        Utils.Adjust(beatMap, replayModel);

        return replayModel;
    }

    /// <summary>
    /// <c>java.util.List.sort</c> is stable (TimSort), so equal timestamps must keep their
    /// insertion order. <c>List&lt;T&gt;.Sort</c> is not stable, hence this explicit merge sort.
    /// </summary>
    internal static void SortStable(List<Note> notes)
    {
        if (notes.Count < 2)
        {
            return;
        }

        var buffer = new Note[notes.Count];
        MergeSort(notes, buffer, 0, notes.Count - 1);
    }

    private static void MergeSort(List<Note> notes, Note[] buffer, int left, int right)
    {
        if (left >= right)
        {
            return;
        }

        var mid = (left + right) / 2;
        MergeSort(notes, buffer, left, mid);
        MergeSort(notes, buffer, mid + 1, right);

        for (var i = left; i <= right; i++)
        {
            buffer[i] = notes[i];
        }

        var a = left;
        var b = mid + 1;
        for (var i = left; i <= right; i++)
        {
            if (a > mid)
            {
                notes[i] = buffer[b++];
            }
            else if (b > right)
            {
                notes[i] = buffer[a++];
            }
            else if (buffer[b].TimeStamp < buffer[a].TimeStamp)
            {
                notes[i] = buffer[b++];
            }
            else
            {
                notes[i] = buffer[a++];
            }
        }
    }
}

/// <summary>Port of <c>me.replays.Mods</c> (only the flags this project consumes).</summary>
[Flags]
public enum OsuMods
{
    None = 0,
    NoFail = 1 << 0,
    Easy = 1 << 1,
    TouchDevice = 1 << 2,
    Hidden = 1 << 3,
    HardRock = 1 << 4,
    SuddenDeath = 1 << 5,
    DoubleTime = 1 << 6,
    Relax = 1 << 7,
    HalfTime = 1 << 8,
    Nightcore = 1 << 9,
    Flashlight = 1 << 10,
    Autoplay = 1 << 11,
    SpunOut = 1 << 12,
    Relax2 = 1 << 13,
    Perfect = 1 << 14,
    Key4 = 1 << 15,
    Key5 = 1 << 16,
    Key6 = 1 << 17,
    Key7 = 1 << 18,
    Key8 = 1 << 19,
    FadeIn = 1 << 20,
    Random = 1 << 22,
    Cinema = 1 << 23,
    Target = 1 << 24,
    Key9 = 1 << 25,
    KeyCoop = 1 << 26,
    Key1 = 1 << 27,
    Key3 = 1 << 28,

    /// <summary>Bit 29 (also reported as Key2 by some mod enumerations).</summary>
    ScoreV2 = 1 << 29,

    /// <summary>Bit 30.</summary>
    Mirror = 1 << 30
}

// Modified C# port of Mania-Replay-Master (Apache-2.0).
// Copyright (c) 2020-present, project contributors. See NOTICE and licenses/Apache-2.0.txt.
using ManiaReplayMaster.Core.Beatmap;
using ManiaReplayMaster.Core.Judger;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Core.Replay;

namespace ManiaReplayMaster.Core.Pipeline;

/// <summary>
/// Port of the preparation half of <c>Main.prepareJudgements</c>: resolve the replay, locate the
/// beatmap (unpacking archives / scanning directories), read both, apply the 1000 ms pre-roll
/// delay, and run the judgement.
/// </summary>
public static class Pipeline
{
    public const long DefaultDelay = 1000L;

    public sealed class Result
    {
        public required BeatMap BeatMap { get; init; }
        public required ReplayModel ReplayModel { get; init; }
        public required IJudger Judger { get; init; }
        public required long Delay { get; init; }
        public required string BeatmapPath { get; init; }
        public required string ReplayPath { get; init; }
        public required string? BeatmapMd5 { get; init; }
    }

    public static Result Prepare(string beatMapPath, string replayPath, string? tempDirectory = null,
        Action<string, object?[]>? log = null, Config? config = null)
    {
        config ??= Config.Instance;
        var replayReader = ReplayReaderRegistry.Match(replayPath)
                           ?? throw new InvalidReplayException(replayPath, new NotSupportedException("No replay reader."));

        string md5;
        try
        {
            md5 = replayReader.ReadBeatMapMd5(replayPath).ToUpperInvariant();
        }
        catch (Exception t)
        {
            throw new InvalidReplayException(Path.GetFullPath(replayPath), t);
        }

        var (beatMapFile, md5Matched) = ResolveBeatmap(beatMapPath, md5, tempDirectory, log);
        if (!md5Matched)
        {
            log?.Invoke("warning.md5", new object?[] { md5 });
        }

        log?.Invoke("read.beatmap", new object?[] { beatMapFile });
        BeatMap beatMap;
        try
        {
            beatMap = (MapReaderRegistry.Match(beatMapFile) ?? throw new InvalidBeatmapException(beatMapFile, null))
                .ReadMap(beatMapFile);
        }
        catch (Exception t)
        {
            if (t is IBaseException)
            {
                throw;
            }

            throw new InvalidBeatmapException(Path.GetFullPath(beatMapFile), t);
        }

        beatMap.CheckValidation((key, a) => log?.Invoke(key, a));

        log?.Invoke("read.replay", new object?[] { replayPath });
        ReplayModel replayModel;
        try
        {
            replayModel = replayReader.ReadReplay(replayPath, beatMap, config);
        }
        catch (Exception t)
        {
            if (t is IBaseException)
            {
                throw;
            }

            throw new InvalidReplayException(Path.GetFullPath(replayPath), t);
        }

        const long delay = DefaultDelay;
        foreach (var note in beatMap.Notes)
        {
            note.TimeStamp += delay;
        }

        foreach (var note in replayModel.ReplayData)
        {
            note.TimeStamp += delay;
        }

        beatMap.Duration = Math.Max(replayModel.ReplayData[^1].EndTime, beatMap.Notes[^1].EndTime) + 2000L;

        if (config.MaxEnabledDuration > 0 && beatMap.Duration > config.MaxEnabledDuration)
        {
            throw new DurationTooLongException();
        }

        log?.Invoke("judgement.generate", Array.Empty<object?>());
        var judger = replayReader.GetJudger(beatMap, replayModel, config);
        judger.Judge();

        return new Result
        {
            BeatMap = beatMap,
            ReplayModel = replayModel,
            Judger = judger,
            Delay = delay,
            BeatmapPath = Path.GetFullPath(beatMapFile),
            ReplayPath = Path.GetFullPath(replayPath),
            BeatmapMd5 = md5Matched ? md5 : null
        };
    }

    public static bool CheckMd5(string file, string md5)
        => string.Equals(Utils.GetMd5(file)?.ToUpperInvariant(), md5, StringComparison.Ordinal);

    private static (string Path, bool Md5Matched) ResolveBeatmap(string path, string md5,
        string? tempDirectory, Action<string, object?[]>? log)
    {
        if (!Directory.Exists(path) && MapReaderRegistry.IsZipMap(path))
        {
            var temp = tempDirectory ?? Path.Combine(Path.GetTempPath(), "mrm-" + Guid.NewGuid().ToString("N"));
            var unzipPath = Path.Combine(temp, "temp_map");
            Directory.CreateDirectory(unzipPath);
            log?.Invoke("read.beatmap.iscompressed", new object?[] { unzipPath });
            try
            {
                Utils.Unzip(Path.GetFullPath(path), unzipPath);
            }
            catch (Exception ex)
            {
                throw new InvalidBeatmapException(Path.GetFullPath(path), ex);
            }

            path = unzipPath;
        }

        if (Directory.Exists(path))
        {
            log?.Invoke("read.beatmap.dir", new object?[] { Path.GetFullPath(path) });
            var candidates = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(f => MapReaderRegistry.Match(f) is not null).ToList();
            if (candidates.Count != 1)
            {
                var matched = candidates.FirstOrDefault(f => CheckMd5(f, md5))
                    ?? throw new BeatmapNotFoundException(md5);
                return (matched, true);
            }

            path = candidates[0];
        }

        return (path, CheckMd5(path, md5));
    }
}

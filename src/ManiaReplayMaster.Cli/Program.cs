using System.Text;
using System.Text.Json;
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Beatmap;
using ManiaReplayMaster.Core.Judger;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Core.Replay;
using ManiaReplayMaster.Core.Pipeline;
using PipelineRunner = ManiaReplayMaster.Core.Pipeline.Pipeline;

namespace ManiaReplayMaster.Cli;

/// <summary>
/// Headless harness. Primary purpose: differential verification of the C# port against the
/// reference Java implementation, plus a usable command-line front end.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        try
        {
            var command = args[0];
            var rest = args.Skip(1).ToArray();
            return command switch
            {
                "dump-replay" => DumpReplay(rest),
                "trace-osr" => OsrTrace.Run(rest[0]),
                "dump-map" => DumpMap(rest),
                "dump-judge" => DumpJudge(rest),
                "benchmark" => Benchmark(rest),
                "-h" or "--help" or "help" => PrintUsage(),
                _ => Unknown(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ErrorCodes.Parse(ex)}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
            mrm — Mania Replay Master (native) headless harness

              mrm dump-replay  <replay.osr|replay.mr> [--raw <out.bin>]
                                 header fields + decoded action stream
              mrm dump-map     <beatmap.osu|beatmap.mc>
                                 parsed beatmap model
              mrm dump-judge   <beatmap> <replay> [--out <file.json>]
                                 full pipeline: parse -> judge -> per-note JSON snapshot
              mrm benchmark    <fixture-dir> <out-dir>
                                 dump-judge for every beatmap/replay pair found
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        return 2;
    }

    private static int DumpReplay(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("dump-replay: missing replay path");
            return 2;
        }

        var path = args[0];
        var reader = ReplayReaderRegistry.Match(path)
                     ?? throw new InvalidReplayException(path, new NotSupportedException("No reader for extension."));

        Console.WriteLine($"file={Path.GetFileName(path)}");
        Console.WriteLine($"reader={reader.GetType().Name}");

        if (reader is OsuReplayReader osuReader)
        {
            var replay = OsuReplayParser.Parse(path);
            Console.WriteLine($"mode={(int)replay.Mode} ({replay.Mode})");
            Console.WriteLine($"fileVersion={replay.FileVersion}");
            Console.WriteLine($"beatmapMd5={replay.BeatmapHash}");
            Console.WriteLine($"username={replay.Username}");
            Console.WriteLine($"replayMd5={replay.ReplayHash}");
            Console.WriteLine($"hit300={replay.Hit300} hit100={replay.Hit100} hit50={replay.Hit50} " +
                              $"geki={replay.Beat300} katu={replay.Beat100} miss={replay.Misses}");
            Console.WriteLine($"points={replay.Points} maxCombo={replay.MaxCombo} perfect={replay.Perfect} " +
                              $"mods={replay.Mods} (0x{replay.Mods:X})");
            Console.WriteLine($"onlineScoreId={replay.OnlineScoreId}");
            Console.WriteLine($"compressedData={(replay.CompressedData?.Length ?? -1)}");

            var raw = OsuReplayParser.Decompress(replay);
            var actions = OsuReplayParser.ParseActions(raw);
            Console.WriteLine($"uncompressedBytes={raw.Length}");
            Console.WriteLine($"actions={actions.Count}");

            var rawOut = GetOption(args, "--raw");
            if (rawOut is not null)
            {
                File.WriteAllBytes(rawOut, raw);
                Console.WriteLine($"rawWritten={Path.GetFullPath(rawOut)}");
            }

            var head = new List<string>();
            for (var i = 0; i < Math.Min(8, actions.Count); i++)
            {
                var a = actions[i];
                head.Add($"({a.W},{Fmt(a.X)},{Fmt(a.Y)},{a.Z})");
            }

            var tail = new List<string>();
            for (var i = Math.Max(0, actions.Count - 3); i < actions.Count; i++)
            {
                var a = actions[i];
                tail.Add($"({a.W},{Fmt(a.X)},{Fmt(a.Y)},{a.Z})");
            }

            Console.WriteLine("first8=" + string.Join(string.Empty, head));
            Console.WriteLine("last3=" + string.Join(string.Empty, tail));

            long cum = 0;
            long first = -1;
            foreach (var a in actions)
            {
                cum += a.W;
                if (first < 0)
                {
                    first = cum;
                }
            }

            Console.WriteLine($"cumStart={first} cumEnd={cum}");
            _ = osuReader;
        }
        else
        {
            var md5 = reader.ReadBeatMapMd5(path);
            Console.WriteLine($"beatmapMd5={md5}");
        }

        return 0;
    }

    private static string Fmt(float value)
        => value == MathF.Floor(value) && MathF.Abs(value) < 1e15f
            ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static int DumpMap(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("dump-map: missing beatmap path");
            return 2;
        }

        var path = args[0];
        var reader = MapReaderRegistry.Match(path)
                     ?? throw new InvalidBeatmapException(path, new NotSupportedException("No reader for extension."));

        var beatMap = reader.ReadMap(path);
        Console.WriteLine($"file={Path.GetFileName(path)}");
        Console.WriteLine($"reader={reader.GetType().Name}");
        Console.WriteLine($"gameMode={beatMap.GameMode} type={beatMap.Type} key={beatMap.Key} od={beatMap.Od:R}");
        Console.WriteLine($"bgmPath={beatMap.BgmPath}");
        Console.WriteLine($"bgmOffset={beatMap.BgmOffset}");
        Console.WriteLine($"sliderMultiplier={beatMap.SliderMultiplier?.ToString("R") ?? "null"}");
        Console.WriteLine($"notes={beatMap.Notes.Count}");
        if (beatMap.Notes.Count > 0)
        {
            Console.WriteLine($"firstNote={beatMap.Notes[0]}".Replace("\n", " | "));
            Console.WriteLine($"lastNote={beatMap.Notes[^1]}".Replace("\n", " | "));
        }

        return 0;
    }

    private static int DumpJudge(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("dump-judge: need <beatmap> <replay>");
            return 2;
        }

        var beatmapPath = args[0];
        var replayPath = args[1];
        var outPath = GetOption(args, "--out");

        var snapshot = PipelineSnapshot.Create(PipelineRunner.Prepare(beatmapPath, replayPath));
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (outPath is not null)
        {
            File.WriteAllText(outPath, json);
            Console.WriteLine($"written={Path.GetFullPath(outPath)}");
            Console.WriteLine($"notes={snapshot.Notes.Count} replayData={snapshot.ReplayData.Count} " +
                              $"acc={snapshot.Acc:R} counts={string.Join(",", snapshot.JudgementCounts)}");
        }
        else
        {
            Console.WriteLine(json);
        }

        return 0;
    }

    private static int Benchmark(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("benchmark: need <fixture-dir> <out-dir>");
            return 2;
        }

        var fixtureDir = args[0];
        var outDir = args[1];
        Directory.CreateDirectory(outDir);

        var maps = Directory.EnumerateFiles(fixtureDir, "*.osu").ToList();
        var replays = Directory.EnumerateFiles(fixtureDir, "*.osr").ToList();
        var produced = 0;

        foreach (var map in maps)
        {
            var md5 = Utils.GetMd5(map)?.ToUpperInvariant();
            var replay = replays.FirstOrDefault(r =>
                string.Equals(OsuReplayParser.Parse(r).BeatmapHash?.ToUpperInvariant(), md5, StringComparison.Ordinal));
            if (replay is null)
            {
                Console.Error.WriteLine($"no replay matches {Path.GetFileName(map)}");
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(replay) + ".json";
            var outPath = Path.Combine(outDir, name);
            try
            {
                var snapshot = PipelineSnapshot.Create(PipelineRunner.Prepare(map, replay));
                File.WriteAllText(outPath, JsonSerializer.Serialize(snapshot, JsonOptions));
                produced++;
                Console.WriteLine($"ok  {name}  notes={snapshot.Notes.Count} acc={snapshot.Acc:R}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"fail {name}: {ex.Message}");
            }
        }

        Console.WriteLine($"produced={produced}");
        return produced > 0 ? 0 : 1;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}

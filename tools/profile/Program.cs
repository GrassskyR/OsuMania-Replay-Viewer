using System.Diagnostics;
using System.Text.Json;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Core.Pipeline;
using ManiaReplayMaster.Encode;
using ManiaReplayMaster.Encode.Diagnostics;
using ManiaReplayMaster.Render;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: profile <beatmap> <replay> <output-directory> [ffmpeg] [codec=libx264] [runs=1] [render-only]");
    return 1;
}

var directory = Path.GetFullPath(args[2]);
Directory.CreateDirectory(directory);
var renderOnly = args.ElementAtOrDefault(6) == "render-only";
var config = new Config { Codec = args.ElementAtOrDefault(4) ?? "libx264", CodecPreset = "auto" };
var ffmpeg = renderOnly ? "" : args.ElementAtOrDefault(3) ?? FfmpegLocator.Locate();
var runs = int.Parse(args.ElementAtOrDefault(5) ?? "1");
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runs);
var results = new List<object>();
for (var run = 0; run < runs; run++)
{
    var temp = Path.Combine(directory, run.ToString());
    Directory.CreateDirectory(temp);
    using var logFile = new StreamWriter(Path.Combine(temp, "ffmpeg.log"));
    var log = new PortLogger(config, line => logFile.WriteLine(line));
    using var process = Process.GetCurrentProcess();
    var cpu = process.TotalProcessorTime;
    var allocated = GC.GetTotalAllocatedBytes(true);
    var total = Stopwatch.StartNew();
    var phase = Stopwatch.StartNew();
    var preparation = Pipeline.Prepare(args[0], args[1], temp, config: config);
    var prepareMs = phase.Elapsed.TotalMilliseconds;
    phase.Restart();
    RenderResult images;
    using (var renderer = new Renderer(preparation.BeatMap, preparation.ReplayModel,
               Path.Combine(temp, "image.png"), config, log))
        images = renderer.Start();
    var renderMs = phase.Elapsed.TotalMilliseconds;
    phase.Restart();
    EncoderSelection? selected = null;
    if (!renderOnly)
    {
        var encoder = new Encoder(preparation.BeatMap, images.ImagePaths, Path.Combine(temp, "video.mp4"),
            preparation.ReplayModel.Rate, (int)preparation.Delay, images.WindowFrameCount,
            ffmpeg, images.GameWidth, config, log);
        encoder.GenerateVideo(encoderSelected: value => selected = value);
    }
    var result = new
    {
        run, selected, prepareMs, renderMs, encodeMs = phase.Elapsed.TotalMilliseconds,
        totalMs = total.Elapsed.TotalMilliseconds,
        appCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
        allocatedMb = (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576.0,
        images = images.ImageCount, imageBytes = images.ImagePaths.Sum(p => new FileInfo(p).Length)
    };
    results.Add(result);
    Console.WriteLine(JsonSerializer.Serialize(result));
}
File.WriteAllText(Path.Combine(directory, "timings.json"),
    JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
return 0;

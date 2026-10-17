using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Encode;
using ManiaReplayMaster.Encode.Diagnostics;
using ManiaReplayMaster.Render;

// End-to-end smoke test: fixtures -> judge -> render long strips -> ffmpeg -> mp4.
// Usage: e2e [replayFileName] [beatmapFileName] [speed]
var root = @"D:\Projects\CSharp\Replay-Viewer";
var fixtures = Path.Combine(root, "fixtures");

var replayName = args.Length > 0
    ? args[0]
    : "Kuit - Doin - Pine nut [ATTang's Lv.27] (2020-10-03) OsuMania.osr";
var beatmapName = args.Length > 1
    ? args[1]
    : "Doin - Pine nut (Night Bunny 7) [ATTang's Lv.27].osu";
var speed = args.Length > 2 ? int.Parse(args[2]) : 20;

var beatmap = Path.Combine(fixtures, beatmapName);
var replay = Path.Combine(fixtures, replayName);
var outDir = Path.Combine(root, "out");
Directory.CreateDirectory(outDir);
var video = Path.Combine(outDir, Path.GetFileNameWithoutExtension(replayName) + ".mp4");
if (File.Exists(video))
{
    File.Delete(video);
}

var cfg = Config.Instance.Clone();
cfg.OutputDir = outDir;
cfg.Speed = speed;
cfg.Debug = true;

var log = new PortLogger(cfg, message => Console.WriteLine("[LOG] " + message));

Console.WriteLine($"ffmpeg: {FfmpegLocator.Locate(null)}");
Console.WriteLine($"replay: {Path.GetFileName(replay)}");
Console.WriteLine($"speed:  {speed}");

var stopwatch = System.Diagnostics.Stopwatch.StartNew();
var lastStage = string.Empty;
var progress = new Progress<EncodeProgress>(p =>
{
    if (p.Stage != lastStage)
    {
        lastStage = p.Stage;
        Console.WriteLine($"  stage={p.Stage} {p.Percentage:0.0}%");
    }
});

try
{
    var result = RenderExporter.Run(
        beatmap, replay, video,
        tempDirectory: null,
        config: cfg,
        progress: progress,
        ffmpegPath: null,
        log: log);

    stopwatch.Stop();
    var info = new FileInfo(result.VideoPath);
    Console.WriteLine("SUCCESS");
    Console.WriteLine($"  video={result.VideoPath}");
    Console.WriteLine($"  size={(info.Exists ? info.Length : -1)}");
    Console.WriteLine($"  windowFrameCount={result.Render.WindowFrameCount}");
    Console.WriteLine($"  gameWidth={result.Render.GameWidth} gameHeight={result.Render.GameHeight}");
    Console.WriteLine($"  windowSize={result.Render.WindowSize} stepSize={result.Render.StepSize}");
    Console.WriteLine($"  totalHeight={result.Render.TotalHeight} columnWidth={result.Render.ColumnWidth}");
    Console.WriteLine($"  windowCount={result.Render.WindowCount} images={result.Render.ImageCount}");
    Console.WriteLine($"  elapsed={stopwatch.Elapsed}");
    return 0;
}
catch (Exception ex)
{
    stopwatch.Stop();
    Console.WriteLine("FAILED: " + ex.Message);
    Console.WriteLine(ex.ToString());
    return 1;
}

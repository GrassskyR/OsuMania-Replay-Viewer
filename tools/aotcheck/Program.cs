using System.Drawing;
using System.Runtime.CompilerServices;
using ManiaReplayMaster.Core;
using ManiaReplayMaster.Core.Model;
using ManiaReplayMaster.Render;

Console.WriteLine($"Native AOT: {!RuntimeFeature.IsDynamicCodeSupported}");
if (args.Contains("--require-native") && RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("This check must run the published Native AOT executable.");
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
ManiaReplayMaster.TestSupport.RepoPaths.Root = root;
if (args.Contains("--encoders"))
{
    ManiaReplayMaster.AotCheck.IntegrationChecks.Encoders();
    return;
}
if (args.Contains("--suite"))
{
    ManiaReplayMaster.AotCheck.BehaviorChecks.Run();
    return;
}
var output = Path.Combine(root, "out", "aot-check", "native-render");
Directory.CreateDirectory(output);
var config = new Config { Width = 541, Height = 960, Codec = "libx264" };
var map = new BeatMap { Key = 4, Duration = 5000, Type = BeatMap.TypeMania };
map.Notes.Add(new Note(2000, 2, 0, judgementStart: 1));
var model = new ReplayModel(new List<Note>(), 1, false, [16, 34, 67, 97, 121, 158]);
var result = Renderer.RenderToImages(map, model, Path.Combine(output, "mania.png"), config: config);
using (var bitmap = new Bitmap(result.ImagePaths[0]))
{
    if (bitmap.Width != 541 || bitmap.Height != 3960 || bitmap.GetPixel(0, 0).ToArgb() != Color.Black.ToArgb())
        throw new Exception("PNG geometry/channel verification failed");
    Console.WriteLine($"PNG verified: {bitmap.Width}x{bitmap.Height}, slices={result.ImageCount}");
}
var fixtureMap = Path.Combine(root, "fixtures", "ck - Carnation (ck remix) (Gekido-) [LN Master].osu");
var fixtureReplay = Path.Combine(root, "fixtures", "- ck - Carnation (ck remix) [LN Master] (2021-12-06) OsuMania.osr");
config.Width = 540;
var export = RenderExporter.Run(fixtureMap, fixtureReplay, Path.Combine(output, "carnation.mp4"),
    tempDirectory: Path.Combine(output, "temp"), config: config);
Console.WriteLine($"Export verified: {export.VideoPath}, slices={export.Render.ImageCount}");

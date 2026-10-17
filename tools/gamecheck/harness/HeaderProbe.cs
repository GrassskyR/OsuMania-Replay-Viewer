using ManiaReplayMaster.Game;
using ManiaReplayMaster.Game.Memory;

namespace ManiaReplayMaster.GameCheck;

/// <summary>
/// Ad-hoc diagnostics for the replay header index. Prints per-file parse outcomes so a regression
/// in <see cref="OsuReplayHeaderReader"/> shows the actual error instead of just "0 parseable".
///
///   gamecheck probe-headers [directory] [limit]
/// </summary>
internal static class HeaderProbe
{
    public static int Run(string[] args)
    {
        var locator = GameLocator.Resolve();
        Console.WriteLine($"osu root: {locator.Root ?? "(not found)"}");

        var directories = args.Length > 0
            ? new[] { args[0] }
            : new[] { locator.DataRDirectory, locator.ReplaysDirectory }
                .Where(d => !string.IsNullOrEmpty(d))
                .Cast<string>()
                .ToArray();

        var limit = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 30;

        var files = directories
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.osr"))
            .Take(limit)
            .ToList();

        Console.WriteLine($"probing {files.Count} files from: {string.Join(" | ", directories)}");
        Console.WriteLine();

        var ok = 0;
        var failed = 0;
        var messages = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var result = OsuReplayHeaderReader.TryParse(file);
            var name = Path.GetFileName(file);

            if (result.Header is not null)
            {
                ok++;
                Console.WriteLine($"OK   {name}  md5={result.Header.BeatmapHash} player={result.Header.Username} " +
                                  $"score={result.Header.Score} mods=0x{result.Header.Mods:X}");
            }
            else
            {
                failed++;
                var message = result.Error ?? "(no error text)";
                // Normalise away the file path so identical failures group together.
                var key = message.Contains(':') ? message[..Math.Min(message.IndexOf(':') + 1, message.Length)] : message;
                messages[key] = messages.GetValueOrDefault(key) + 1;
                Console.WriteLine($"FAIL {name}  error={message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== {ok} ok, {failed} failed ===");
        foreach (var kv in messages.OrderByDescending(k => k.Value))
        {
            Console.WriteLine($"  {kv.Value,5} x {kv.Key}");
        }

        return failed == 0 ? 0 : 1;
    }
}

using ManiaReplayMaster.Game.Memory;
using ManiaReplayMaster.GameCheck;

switch (args.FirstOrDefault())
{
    case "memory32":
        var helperIndex = Array.IndexOf(args, "--helper");
        if (helperIndex < 0 || helperIndex + 1 >= args.Length)
            throw new ArgumentException("memory32 requires --helper <x86target.exe>");
        await Memory32Checks.Run(args[helperIndex + 1]);
        break;
    case "live":
        using (var watcher = new GameStateWatcher())
            Console.WriteLine(watcher.PollOnce());
        break;
    default:
        Console.WriteLine("gamecheck memory32 --helper <x86target.exe> | live");
        break;
}

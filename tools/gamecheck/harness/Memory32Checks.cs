using System.Diagnostics;
using System.Globalization;
using ManiaReplayMaster.Game.Memory;

namespace ManiaReplayMaster.GameCheck;

internal static class Memory32Checks
{
    public static async Task Run(string helperPath)
    {
        using var helper = Process.Start(new ProcessStartInfo(Path.GetFullPath(helperPath))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Cannot start x86 fixture.");
        try
        {
            var line = await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var arenas = (line ?? throw new InvalidOperationException("x86 fixture exited before ready."))
                .Split(' ').Select(value => ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            using var memory = ProcessMemory.Open(helper.Id, expectedPointerSize: 4);
            StableLayoutChecks.Run(memory, arenas[0], results: false);
            StableLayoutChecks.Run(memory, arenas[1], results: true);
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.StandardInput.Close();
                try { await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
            }
        }
        if (helper.ExitCode != 0)
            throw new InvalidOperationException(await helper.StandardError.ReadToEndAsync());
    }
}

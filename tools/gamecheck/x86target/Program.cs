using System.Runtime.InteropServices;
using ManiaReplayMaster.X86Target;

var gameplay = StableLayoutFixture.Create(results: false);
var results = StableLayoutFixture.Create(results: true);
try
{
    Console.WriteLine($"{unchecked((uint)gameplay.ToInt32()):X} {unchecked((uint)results.ToInt32()):X}");
    Console.Out.Flush();
    Console.ReadLine(); // Keep the memory alive until the parent has read both screens.
}
finally
{
    Marshal.FreeHGlobal(gameplay);
    Marshal.FreeHGlobal(results);
}

using System.Text;

namespace ManiaReplayMaster.Game.Memory;

public static class ManagedStrings
{
    /// <summary>Reads a CLR string object using the target's pointer width, not a NUL-terminated buffer.</summary>
    public static string? TryReadManagedString(this IMemoryReader memory, ulong address, int maxChars = 32768)
    {
        if (address == 0 || !memory.TryReadInt32(address + (ulong)memory.PointerSize, out var length)
            || length < 0 || length > maxChars)
            return null;
        if (length == 0) return string.Empty;
        return memory.TryRead(address + (ulong)memory.PointerSize + 4, length * 2, out var bytes)
            ? Encoding.Unicode.GetString(bytes) : null;
    }
}

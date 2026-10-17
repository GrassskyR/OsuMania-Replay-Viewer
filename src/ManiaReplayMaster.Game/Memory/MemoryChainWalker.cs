namespace ManiaReplayMaster.Game.Memory;

/// <summary>
/// Walks the §3.4.2 pointer chains. One loop, no library (plan §3.4.1 item 5):
/// <c>current = base; for each step: current = readPointer(current + offset)</c>.
///
/// Every read goes through <see cref="IMemoryReader.PointerSize"/>, so a 64-bit read of a 32-bit
/// target is impossible here by construction — that mistake produces plausible-looking addresses and
/// then all-zero values (the classic silent failure the plan calls out).
/// </summary>
public sealed class MemoryChainWalker
{
    public MemoryChainWalker(IMemoryReader reader)
    {
        Reader = reader;
    }

    public IMemoryReader Reader { get; }

    /// <summary>Follows a chain. Reports how many steps succeeded so failures can be explained.</summary>
    public PointerChainResult Walk(PointerChain chain, bool nullTerminates = true)
    {
        var steps = chain.Steps;
        var current = chain.BaseAddress;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var address = current + step.Offset;

            if (!Reader.TryReadPointer(address, out var next))
            {
                return PointerChainResult.Fail(i, $"cannot read a {Reader.PointerSize}-byte pointer at 0x{address:X} ({step})");
            }

            if (next == 0 && nullTerminates)
            {
                return PointerChainResult.Fail(i, $"pointer at 0x{address:X} ({step}) is null");
            }

            // Reject values that cannot be a user-mode address of the target.
            var maxAddress = Reader.PointerSize == 4 ? 0xFFFF_FFFFUL : 0x0000_7FFF_FFFF_FFFFUL;
            if (next > maxAddress)
            {
                return PointerChainResult.Fail(i, $"pointer at 0x{address:X} ({step}) is 0x{next:X}, outside the address space");
            }

            current = next;
        }

        // The final address is never dereferenced again, so nothing downstream would notice that it is
        // not memory at all: a chain step reading a non-pointer word (e.g. 0x7E7E7E7E) used to be
        // reported as "resolved", and the caller only failed later with a confusing "no value at
        // 0x7E7E7E7E". Validate the endpoint once so a wrong chain is reported as a wrong chain.
        if (Reader is IMemoryAddressMap map && !map.IsAddressReadable(current))
        {
            return PointerChainResult.Fail(
                steps.Count,
                $"chain ends at 0x{current:X}, which is not committed readable memory in the target " +
                $"(the last step read a value that is not a usable pointer)");
        }

        return PointerChainResult.Ok(current, steps.Count);
    }

    /// <summary>Resolves a chain to its final address (not the value stored there).</summary>
    public bool TryResolve(PointerChain chain, out ulong address, out string? failure)
    {
        var result = Walk(chain);
        address = result.Address;
        failure = result.Success ? null : $"{result.FailureReason} (after {result.StepsCompleted} of {chain.Steps.Count} steps)";
        return result.Success;
    }

    /// <summary>Dereferences a chain and reads the requested value kind at the final address.</summary>
    public bool TryReadValue(PointerChain chain, MemoryValueKind kind, out MemoryValue value, out string? failure)
    {
        value = default;
        if (!TryResolve(chain, out var address, out failure))
        {
            return false;
        }

        return TryReadAt(address, kind, out value, out failure);
    }

    /// <summary>Reads a value of the given kind at an absolute address.</summary>
    public bool TryReadAt(ulong address, MemoryValueKind kind, out MemoryValue value, out string? failure)
    {
        value = default;
        failure = null;

        switch (kind)
        {
            case MemoryValueKind.Byte:
            {
                if (!Reader.TryReadByte(address, out var b))
                {
                    failure = $"no byte at 0x{address:X}";
                    return false;
                }

                value = MemoryValue.FromInt(b);
                return true;
            }

            case MemoryValueKind.UInt16:
            {
                if (!Reader.TryReadUInt16(address, out var s))
                {
                    failure = $"no u16 at 0x{address:X}";
                    return false;
                }

                value = MemoryValue.FromInt(s);
                return true;
            }

            case MemoryValueKind.Int32:
            {
                if (!Reader.TryReadInt32(address, out var i))
                {
                    failure = $"no i32 at 0x{address:X}";
                    return false;
                }

                value = MemoryValue.FromInt(i);
                return true;
            }

            case MemoryValueKind.Int64:
            case MemoryValueKind.DotNetTicks:
            {
                if (!Reader.TryReadInt64(address, out var l))
                {
                    failure = $"no i64 at 0x{address:X}";
                    return false;
                }

                value = MemoryValue.FromLong(l);
                return true;
            }

            case MemoryValueKind.Float:
            {
                if (!Reader.TryReadInt32(address, out var raw))
                {
                    failure = $"no f32 at 0x{address:X}";
                    return false;
                }

                value = MemoryValue.FromFloat(BitConverter.Int32BitsToSingle(raw));
                return true;
            }

            case MemoryValueKind.PointerToManagedString:
            case MemoryValueKind.PointerToAnsiString:
            case MemoryValueKind.PointerToUtf8String:
            {
                if (!Reader.TryReadPointer(address, out var pointer) || pointer == 0)
                {
                    failure = $"no string pointer at 0x{address:X}";
                    return false;
                }

                var text = kind == MemoryValueKind.PointerToManagedString
                    ? Reader.TryReadManagedString(pointer) : Reader.TryReadString(pointer);
                if (text is null)
                {
                    failure = $"string pointer 0x{pointer:X} is not readable";
                    return false;
                }

                value = MemoryValue.FromString(text);
                return true;
            }

            default:
                failure = $"unsupported value kind {kind}";
                return false;
        }
    }
}

/// <summary>A tagged value read from the target.</summary>
public readonly struct MemoryValue
{
    private MemoryValue(long integer, double real, string? text, MemoryValueKind kind)
    {
        Integer = integer;
        Real = real;
        Text = text;
        Kind = kind;
    }

    public long Integer { get; }

    public double Real { get; }

    public string? Text { get; }

    public MemoryValueKind Kind { get; }

    public bool HasText => Text is not null;

    public static MemoryValue FromInt(long value) => new(value, value, null, MemoryValueKind.Int32);

    public static MemoryValue FromLong(long value) => new(value, value, null, MemoryValueKind.Int64);

    public static MemoryValue FromFloat(double value) => new((long)value, value, null, MemoryValueKind.Float);

    public static MemoryValue FromString(string value) => new(0, 0, value, MemoryValueKind.PointerToUtf8String);

    public static MemoryValue FromBool(bool value) => new(value ? 1 : 0, value ? 1 : 0, null, MemoryValueKind.Byte);

    public override string ToString() => Text ?? (Kind == MemoryValueKind.Float ? Real.ToString("0.###") : Integer.ToString());
}

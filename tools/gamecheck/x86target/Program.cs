using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ManiaReplayMaster.X86Target;

/// <summary>
/// A synthetic 32-bit target process for the memory reader self-test recommended by plan 搂8.1-3.
///
/// It contains exactly the things the reader has to get right and which cannot be tested against
/// osu!stable on this machine:
/// <list type="bullet">
/// <item>a <b>signature</b> with four wildcard bytes that encode the absolute address of a known
/// structure 鈥?the same shape as the real <c>A1 ?? ?? ?? ??</c> patterns;</item>
/// <item>a <b>pointer chain</b>: static structure 鈫?root 鈫?string pointer 鈫?UTF-8 bytes;</item>
/// <item>a <b>large committed allocation</b> (8 MB) with a marker in the middle, so a chunked read has
/// to cross many 4 KB boundaries;</item>
/// <item>a known <c>u32</c> next to the root pointer, so a wrong pointer width (8 bytes instead of 4)
/// produces an obviously wrong value.</item>
/// </list>
///
/// The process prints (and optionally writes) a handshake describing its layout, then parks on a named
/// semaphore until the harness releases it. It never reads or writes anything outside its own address
/// space.
/// </summary>
public static class Program
{
    /// <summary>
    /// Static storage laid out with explicit offsets so the harness can describe pointer chains without
    /// guessing where the compiler put anything. Fixed-size buffers are used (not
    /// <c>ByValArray</c>) so the whole struct is blittable and can be pinned 鈥?which matters, because
    /// the layout deliberately lives on the managed heap rather than in the module image, forcing the
    /// scanner to walk committed regions the way it must for the real game.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct KnownLayout
    {
        /// <summary>Offset 0x00: "A1 &lt;abs address of this struct&gt; DE AD C0 DE 12 34 56 78".</summary>
        public fixed byte Signature[12];

        /// <summary>Offset 0x0C: root pointer.</summary>
        public int Root;

        /// <summary>Offset 0x10: a value whose 4-byte width matters (see the 8-byte read test).</summary>
        public int WidthProbeLow;

        /// <summary>Offset 0x14: the value an incorrect 8-byte pointer read would fold in.</summary>
        public int WidthProbeHigh;

        /// <summary>Offset 0x18: a second wildcard signature with a different prefix.</summary>
        public fixed byte SecondarySignature[8];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct RootObject
    {
        public int RootValue;
        public int Md5Pointer;
        public int FolderPointer;
        public int PlayerPointer;
        public int LargePointer;
        public int LargeSize;
        public int ChainBasePointer;
        public int ChainValue;
        public int SelectedScorePointer;
        public int Status;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ChainMiddle
    {
        public int ValuePointer;
        public int Padding;
    }

    public const string MagicMd5 = "d41d8cd98f00b204e9800998ecf8427e";
    public const string MagicFolder = "1234567 Test Artist - Test Title";
    public const string MagicPlayer = "gamecheck32";
    public const int MagicChainValue = 0x4D52414D; // "MRAM"
    public const int MagicRootValue = 0x11223344;
    public const int MagicWidthLow = unchecked((int)0xCAFEBABE);
    public const int MagicWidthHigh = 0x0BADF00D;
    public const int LargeBlockSize = 8 * 1024 * 1024;
    public const int LargeMarkerOffset = 3_000_003;

    /// <summary>Size of the block used to test window-boundary straddling (two 1 MB windows plus slack).</summary>
    public const int BoundaryBlockSize = 2 * 1024 * 1024 + 64;

    /// <summary>Where the boundary signature sits: four bytes before the 1 MB mark, so it straddles it.</summary>
    public const int BoundarySignatureOffset = (1 * 1024 * 1024) - 4;

    /// <summary>A 32-bit-aligned value that is <b>not</b> a valid user-mode address of a mapped region.</summary>
    public const int NonPointerWord = 0x7E7E7E7E;

    private static readonly byte[] SecondarySignature = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };

    private static KnownLayout[] _known = new KnownLayout[1];

    private static readonly byte[] LargeMarker = Encoding.ASCII.GetBytes("MRM-LARGE-BLOCK-MARKER");

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var handshakePath = GetArg(args, "--handshake");
        var semaphoreName = GetArg(args, "--semaphore") ?? "Global\\mrm-gamecheck-x86target";

        var knownAddress = AddressOfKnown();

        // ---- managed -> unmanaged objects the reader will follow pointers into ----------------
        var md5 = Marshal.StringToHGlobalAnsi(MagicMd5);
        var folder = Marshal.StringToHGlobalAnsi(MagicFolder);
        var player = Marshal.StringToHGlobalAnsi(MagicPlayer);

        var large = Marshal.AllocHGlobal(LargeBlockSize);
        var largeBytes = new byte[LargeBlockSize];
        for (var i = 0; i < largeBytes.Length; i += 64)
        {
            largeBytes[i] = (byte)(i & 0xFF);
        }

        Array.Copy(LargeMarker, 0, largeBytes, LargeMarkerOffset, LargeMarker.Length);
        Marshal.Copy(largeBytes, 0, large, LargeBlockSize);

        var root = Marshal.AllocHGlobal(Marshal.SizeOf<RootObject>());
        var middle = Marshal.AllocHGlobal(Marshal.SizeOf<ChainMiddle>());
        var chainValue = Marshal.StringToHGlobalAnsi("chain-value-ok");
        var selectedScore = Marshal.AllocHGlobal(0x100);

        // A 16-byte scratch block inside the *target* (so its address is 32-bit): two XOR keys, a zero
        // word for the null-pointer chain test, and a spare slot.
        var modsScratch = Marshal.AllocHGlobal(16);
        Marshal.WriteInt32(modsScratch, 0, 0x20000048);  // NF | DT | HD-ish key 1
        Marshal.WriteInt32(modsScratch, 4, 0x20000000);  // key 2 -> XOR == 0x48 (DT | HD)
        Marshal.WriteInt32(modsScratch, 8, 0);
        Marshal.WriteInt32(modsScratch, 12, 0);

        // A zeroed block holding a 13-byte signature four bytes before the 1 MB mark, so any scanner that
        // reads in windows must step back far enough to see a pattern that straddles the boundary. The
        // 13-byte form (with the trailing 56 78) is deliberately different from the 12-byte signature above
        // so it occurs exactly once in the whole target. Filled after the signature bytes are built below.
        var boundaryBlock = Marshal.AllocHGlobal(BoundaryBlockSize);
        unsafe
        {
            new Span<byte>((void*)boundaryBlock, BoundaryBlockSize).Clear();
        }

        for (var i = 0; i < 0x100; i++)
        {
            Marshal.WriteByte(selectedScore, i, 0);
        }

        // The chain is root -> middle -> string, i.e. two dereferences plus an offset.
        Marshal.WriteInt32(middle, 0, (int)chainValue);
        // +4 holds a value that is deliberately NOT a pointer. Walking a second step from here must
        // fail (the address is unmapped) instead of silently yielding a garbage address.
        Marshal.WriteInt32(middle, 4, NonPointerWord);

        var rootValue = new RootObject
        {
            RootValue = MagicRootValue,
            Md5Pointer = (int)md5,
            FolderPointer = (int)folder,
            PlayerPointer = (int)player,
            LargePointer = (int)large,
            LargeSize = LargeBlockSize,
            ChainBasePointer = (int)middle,
            ChainValue = 0,
            SelectedScorePointer = (int)selectedScore,
            Status = 1
        };

        var rootBytes = new byte[Marshal.SizeOf<RootObject>()];
        MemoryMarshal.Write(rootBytes, in rootValue);
        Marshal.Copy(rootBytes, 0, root, rootBytes.Length);

        // Write the signature into the pinned layout: "A1 <abs known address> DE AD C0 DE 12 34 56 78".
        // The leading A1 mirrors the real osu! signature shape (an absolute-address load), so the
        // harness exercises the same SignatureStrategy.AbsoluteDataReference path it will use on the game.
        var signature = new byte[12];
        signature[0] = 0xA1;
        BitConverter.GetBytes(knownAddress).CopyTo(signature, 1);
        signature[5] = 0xDE;
        signature[6] = 0xAD;
        signature[7] = 0xC0;
        signature[8] = 0xDE;
        signature[9] = 0x12;
        signature[10] = 0x34;
        signature[11] = 0x78;

        var layout = new KnownLayout
        {
            Root = (int)root,
            WidthProbeLow = MagicWidthLow,
            WidthProbeHigh = MagicWidthHigh
        };

        unsafe
        {
            for (var i = 0; i < 12; i++)
            {
                layout.Signature[i] = signature[i];
            }

            for (var i = 0; i < 8; i++)
            {
                layout.SecondarySignature[i] = SecondarySignature[i];
            }
        }

        _known[0] = layout;

        // Fill the boundary block now that the 12-byte signature exists: the 13-byte form
        // (A1 <abs> DE AD C0 DE 12 34 56 78) is unique in the target, and it is written four bytes before
        // the 1 MB mark so it straddles a 1 MB scan window.
        var boundarySignature = new byte[13];
        Array.Copy(signature, boundarySignature, 9);   // A1, the 4 address bytes, DE AD C0 DE
        boundarySignature[9] = 0x12;
        boundarySignature[10] = 0x34;
        boundarySignature[11] = 0x56;
        boundarySignature[12] = 0x78;
        var boundarySignatureAddress = boundaryBlock + BoundarySignatureOffset;
        Marshal.Copy(boundarySignature, 0, boundarySignatureAddress, boundarySignature.Length);

        // Touch the array again so the JIT cannot treat it as dead; the bytes must stay mapped.
        var verified = _known[0].Root == (int)root;

        var process = System.Diagnostics.Process.GetCurrentProcess();
        var module = process.MainModule;
        var stableResults = StableLayoutFixture.Create(results: true);
        var stablePlaying = StableLayoutFixture.Create(results: false);

        var handshake = string.Join(
            "|",
            "MRM-X86-TARGET-1",
            $"stableResults=0x{(uint)stableResults.ToInt32():X}",
            $"stablePlaying=0x{(uint)stablePlaying.ToInt32():X}",
            $"pid={Environment.ProcessId}",
            $"ptr={IntPtr.Size}",
            $"moduleBase=0x{(module is null ? 0UL : (ulong)module.BaseAddress.ToInt64()):X}",
            $"moduleSize=0x{(module is null ? 0U : (uint)module.ModuleMemorySize):X}",
            $"moduleName={module?.ModuleName}",
            $"knownAddress=0x{knownAddress:X}",
            $"signatureAddress=0x{SignatureAddress:X}",
            $"root=0x{(ulong)root:X}",
            $"md5=0x{(ulong)md5:X}",
            $"folder=0x{(ulong)folder:X}",
            $"player=0x{(ulong)player:X}",
            $"large=0x{(ulong)large:X}",
            $"largeSize={LargeBlockSize}",
            $"markerOffset={LargeMarkerOffset}",
            $"middle=0x{(ulong)middle:X}",
            $"chainValuePtr=0x{(ulong)chainValue:X}",
            $"selectedScore=0x{(ulong)selectedScore:X}",
            $"modsScratch=0x{(ulong)modsScratch:X}",
            // modsScratch + 8 is a zero dword *inside the 32-bit target*. A null-pointer chain test has
            // to point at it: an address allocated by the (64-bit) harness is unreachable with 4-byte
            // pointers, so the walk would fail for the wrong reason and the test would be vacuous.
            $"zeroScratch=0x{(ulong)(modsScratch + 8):X}",
            // A 13-byte signature placed across a 1 MB window boundary (see BoundarySignatureOffset).
            $"boundarySignature=0x{(ulong)boundarySignatureAddress:X}",
            $"boundarySignatureBytes={Convert.ToHexString(boundarySignature)}",
            // The value stored at middle + 4: deliberately not a pointer, and its value is published so
            // the harness can name it in its failure message instead of hard-coding 0x7E7E7E7E.
            $"nonPointerWord=0x{NonPointerWord:X}",
            $"rootValue=0x{MagicRootValue:X}",
            $"widthLow=0x{unchecked((uint)MagicWidthLow):X}",
            $"widthHigh=0x{unchecked((uint)MagicWidthHigh):X}",
            $"md5Text={MagicMd5}",
            $"folderText={MagicFolder}",
            $"playerText={MagicPlayer}",
            $"signatureVerified={verified}",
            $"signatureBytes={Convert.ToHexString(signature)}",
            $"secondaryBytes={Convert.ToHexString(SecondarySignature)}");

        Console.WriteLine(handshake);
        Console.Out.Flush();

        if (!string.IsNullOrEmpty(handshakePath))
        {
            try
            {
                File.WriteAllText(handshakePath, handshake, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"could not write handshake: {ex.Message}");
            }
        }

        // Park until the harness is done. This keeps every allocation alive and the module image mapped.
        try
        {
            using var semaphore = new Semaphore(0, 1, semaphoreName);
            Console.Error.WriteLine("waiting for release...");
            var released = semaphore.WaitOne(TimeSpan.FromMinutes(3));
            Console.Error.WriteLine(released ? "released" : "timed out");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"semaphore unavailable ({ex.GetType().Name}); sleeping instead");
            Thread.Sleep(TimeSpan.FromMinutes(3));
        }

        GC.KeepAlive(_known);
        Marshal.FreeHGlobal(md5);
        Marshal.FreeHGlobal(folder);
        Marshal.FreeHGlobal(player);
        Marshal.FreeHGlobal(large);
        Marshal.FreeHGlobal(root);
        Marshal.FreeHGlobal(middle);
        Marshal.FreeHGlobal(chainValue);
        Marshal.FreeHGlobal(selectedScore);
        Marshal.FreeHGlobal(modsScratch);
        Marshal.FreeHGlobal(boundaryBlock);
        Marshal.FreeHGlobal(stableResults);
        Marshal.FreeHGlobal(stablePlaying);
        return 0;
    }

    /// <summary>
    /// Returns the address of the pinned layout array. <see cref="KnownLayout"/> is blittable (fixed
    /// buffers, no references), so pinning gives a stable address for the lifetime of the process.
    /// </summary>
    private static uint AddressOfKnown()
    {
        _pinned = GCHandle.Alloc(_known, GCHandleType.Pinned);
        var address = (uint)_pinned.AddrOfPinnedObject().ToInt64();
        SignatureAddress = address;
        return address;
    }

    private static GCHandle _pinned;

    /// <summary>Address of the pinned layout (and therefore of the signature bytes), told to the harness.</summary>
    public static uint SignatureAddress { get; private set; }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}

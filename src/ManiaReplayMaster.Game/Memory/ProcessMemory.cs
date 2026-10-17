using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ManiaReplayMaster.Game.Memory;

/// <summary>A committed, readable memory region of the target process.</summary>
public readonly record struct MemoryRegion(ulong BaseAddress, ulong Size, uint Protect, uint State, uint Type)
{
    public ulong End => BaseAddress + Size;

    public bool IsReadable => (Protect & ProcessMemory.PageGuard) == 0
                              && (Protect & ProcessMemory.PageNoAccess) == 0
                              && (Protect & (ProcessMemory.PageReadOnly | ProcessMemory.PageReadWrite | ProcessMemory.PageWriteCopy
                                             | ProcessMemory.PageExecuteRead | ProcessMemory.PageExecuteReadWrite | ProcessMemory.PageExecuteWriteCopy)) != 0;
}

/// <summary>
/// Read-only access to another process's memory (plan 搂3.4 / 搂3.4.1).
///
/// <b>Non-goals, enforced by the API surface:</b> this class never injects a DLL, never writes to the
/// target, never allocates in the target, never hooks and never suspends it. The only access right it
/// requests is <c>PROCESS_VM_READ | PROCESS_QUERY_INFORMATION</c>. There is deliberately no
/// <c>WriteProcessMemory</c>, <c>VirtualAllocEx</c>, <c>CreateRemoteThread</c>, or APC declaration
/// anywhere in this project.
///
/// The pitfalls of 搂3.4.1 are handled explicitly:
/// <list type="number">
/// <item><see cref="ReadProcessMemory"/> is all-or-nothing, so a read is first validated against the
/// committed/readable region map and then performed in 4 KB chunks.</item>
/// <item>Only <c>MEM_COMMIT</c>, readable, non-<c>PAGE_GUARD</c> regions are walked, stepping by the
/// returned <c>RegionSize</c>.</item>
/// <item><see cref="EnumerateModules"/> uses <c>TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32</c> and retries
/// on <c>ERROR_BAD_LENGTH</c>, which the 64-bit-to-32-bit direction requires.</item>
/// <item><b>Pointer width is 4 bytes</b> for the 32-bit target; it is an explicit constructor argument,
/// asserted against the target's WOW64 status, and every pointer read goes through it.</item>
/// <item>Pointer chains are data (<see cref="PointerChain"/>), walked by one loop.</item>
/// </list>
/// </summary>
public sealed class ProcessMemory : IDisposable, IMemoryReader, IMemoryAddressMap
{
    public const uint ProcessVmRead = 0x0010;
    public const uint ProcessQueryInformation = 0x0400;
    public const uint ProcessQueryLimitedInformation = 0x1000;

    public const uint MemCommit = 0x1000;
    public const uint MemPrivate = 0x20000;
    public const uint MemMapped = 0x40000;
    public const uint MemImage = 0x1000000;

    public const uint PageNoAccess = 0x01;
    public const uint PageReadOnly = 0x02;
    public const uint PageReadWrite = 0x04;
    public const uint PageWriteCopy = 0x08;
    public const uint PageExecute = 0x10;
    public const uint PageExecuteRead = 0x20;
    public const uint PageExecuteReadWrite = 0x40;
    public const uint PageExecuteWriteCopy = 0x80;
    public const uint PageGuard = 0x100;
    public const uint PageNoCache = 0x200;
    public const uint PageWriteCombine = 0x400;

    public const uint Th32CsSnapModule = 0x00000008;
    public const uint Th32CsSnapModule32 = 0x00000010;

    public const int ErrorBadLength = 24;
    public const int ErrorPartialCopy = 299;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorAccessDenied = 5;
    public const int ErrorNoMoreFiles = 18;

    /// <summary>Chunk size for <see cref="ReadProcessMemory"/> calls (plan 搂3.4.1 item 1).</summary>
    public const int ReadChunkSize = 4096;

    private readonly SafeProcessHandle _handle;
    private readonly List<MemoryRegion> _regions;
    private bool _disposed;

    private ProcessMemory(SafeProcessHandle handle, int processId, string processName, int pointerSize, bool isWow64)
    {
        _handle = handle;
        ProcessId = processId;
        ProcessName = processName;
        PointerSize = pointerSize;
        IsWow64 = isWow64;
        // The region list is always cached. Without it every 4 KB chunk of a bulk read would re-walk the
        // whole address space (a real, measured hang: an 8 MB read issued 2,048 full walks).
        _regions = new List<MemoryRegion>();
    }

    public int ProcessId { get; }

    public string ProcessName { get; }

    /// <summary>4 for the 32-bit osu!stable target. Never assume 8 鈥?see 搂3.4.1 item 4.</summary>
    public int PointerSize { get; }

    public bool IsWow64 { get; }

    public bool Is32BitTarget => PointerSize == 4;

    public bool IsDisposed => _disposed;

    /// <summary>How many regions the last <see cref="EnumerateRegions"/> call returned (0 when not cached).</summary>
    public int CachedRegionCount => RegionCount;

    /// <summary>
    /// Opens a process for read-only inspection. <paramref name="expectedPointerSize"/> is asserted
    /// against the target so that a 64-bit process can never be read with 4-byte pointers (or the
    /// reverse), which is the silent-failure trap called out in plan 搂3.4.1.
    /// </summary>
    public static ProcessMemory Open(
        string processName = GameLocator.ProcessName,
        int expectedPointerSize = 4,
        bool cacheRegions = false)
    {
        var processes = Process.GetProcessesByName(processName);
        if (processes.Length == 0)
        {
            throw new InvalidOperationException($"'{processName}' is not running.");
        }

        var process = processes[0];
        for (var i = 1; i < processes.Length; i++)
        {
            processes[i].Dispose();
        }

        try
        {
            return Open(process.Id, processName, expectedPointerSize, cacheRegions);
        }
        finally
        {
            process.Dispose();
        }
    }

    public static ProcessMemory Open(int processId, string? processName = null, int expectedPointerSize = 4, bool cacheRegions = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Process memory reading is Windows-only.");
        }

        var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, bInheritHandle: false, (uint)processId);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                error == ErrorAccessDenied
                    ? $"OpenProcess(PROCESS_VM_READ|PROCESS_QUERY_INFORMATION) denied for pid {processId}. " +
                      "The game may be running elevated, or the process is protected."
                    : $"OpenProcess failed for pid {processId} (Win32 error {error}).");
        }

        var wow64 = QueryWow64(handle, out var isWow64) && isWow64;
        // A 32-bit process is WOW64 when the host is 64-bit; on a 32-bit OS the distinction does not
        // exist and the pointer size is 4 anyway.
        var detected = wow64 || !Environment.Is64BitOperatingSystem ? 4 : 8;

        if (detected != expectedPointerSize)
        {
            handle.Dispose();
            throw new InvalidOperationException(
                $"Pointer width mismatch for pid {processId}: the process appears to be {detected * 8}-bit " +
                $"(WOW64={wow64}) but the caller asserted {expectedPointerSize * 8}-bit pointers. " +
                "Reading with the wrong width produces plausible-looking garbage instead of failing (plan 搂3.4.1 item 4).");
        }

        var memory = new ProcessMemory(
            handle,
            processId,
            processName ?? TryGetName(processId),
            expectedPointerSize,
            wow64);

        if (cacheRegions)
        {
            memory.EnumerateRegions();
        }

        return memory;
    }

    /// <summary>True when a process with the given name is running; never throws.</summary>
    public static bool IsRunning(string processName = GameLocator.ProcessName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            var any = processes.Length > 0;
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return any;
        }
        catch
        {
            return false;
        }
    }

    public static int? FindProcessId(string processName = GameLocator.ProcessName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0)
            {
                return null;
            }

            var id = processes[0].Id;
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return id;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Walks the target's address space and returns every committed, readable region.
    ///
    /// Stepping is base-address based, never "current + 1": <c>VirtualQueryEx</c> returns the whole region
    /// containing the queried address, so within a region the next query point is <c>Base + RegionSize</c>
    /// and in the gaps between regions it is the next allocation-granularity boundary. Stepping by a fixed
    /// 64 KB instead makes a 64-bit walk spend every iteration inside one huge <c>MEM_FREE</c> region and
    /// find almost nothing before hitting the iteration cap.
    ///
    /// The address is passed to <c>VirtualQueryEx</c> untruncated (see
    /// <see cref="VirtualQueryExAt"/>). Before that fix a 64-bit walk only ever queried the low 4 GB and
    /// reported a couple of regions; the "≥ 20 regions" self-test failure it caused was a real bug in
    /// this method, not an over-strict test.
    /// </summary>
    public IReadOnlyList<MemoryRegion> EnumerateRegions(int maxIterations = 200_000)
    {
        ThrowIfDisposed();
        var regions = new List<MemoryRegion>();
        var granularity = 0x10000UL;
        var limit = PointerSize == 4 ? 0x1_0000_0000UL : 0x0000_7FFF_FFFF_0000UL;
        ulong address = 0;

        for (var i = 0; i < maxIterations && address < limit; i++)
        {
            var mbiSize = (nuint)Marshal.SizeOf<MemoryBasicInformation64>();
            var queried = VirtualQueryExAt(address, out var mbi, mbiSize);
            if (queried == 0 || mbi.RegionSize == 0)
            {
                // Nothing mapped here: move on to the next granularity boundary.
                address = (address / granularity + 1) * granularity;
                continue;
            }

            var region = new MemoryRegion(
                (ulong)mbi.BaseAddress,
                (ulong)mbi.RegionSize,
                mbi.Protect,
                mbi.State,
                mbi.Type);

            if (mbi.State == MemCommit && region.IsReadable)
            {
                regions.Add(region);
            }

            var next = region.End > address ? region.End : address + granularity;

            // No candidate filtering beyond the cache size: the whole region list is what the reader and
            // the scanner need, and a 64-bit process has on the order of a few hundred regions.
            address = next;
        }

        lock (_regions)
        {
            _regions.Clear();
            _regions.AddRange(regions);
        }

        return regions;
    }

    /// <summary>
    /// Queries a single region at an address without disturbing the cache. Returns false with the Win32
    /// error code when <c>VirtualQueryEx</c> itself fails, which is what distinguishes "the walk stopped
    /// at the top of the address space" from "the walk was truncated early".
    /// </summary>
    public bool TryQueryRegion(ulong address, out MemoryRegion region, out int win32Error)
    {
        ThrowIfDisposed();
        region = default;

        // A 32-bit target cannot have an address above its 4 GB address space. Reporting "not queryable"
        // is the honest answer; truncating the value with (uint) would silently query a *different*
        // address and make a 64-bit caller look like it worked (see VirtualQueryAt).
        if (!IsRepresentableAddress(address))
        {
            win32Error = ErrorInvalidParameter;
            return false;
        }

        var mbiSize = (nuint)Marshal.SizeOf<MemoryBasicInformation64>();
        var queried = VirtualQueryExAt(address, out var mbi, mbiSize);
        if (queried == 0)
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        win32Error = 0;
        region = new MemoryRegion((ulong)mbi.BaseAddress, (ulong)mbi.RegionSize, mbi.Protect, mbi.State, mbi.Type);
        return true;
    }

    /// <summary>
    /// True when <paramref name="address"/> can exist in the target's address space. A 32-bit target's
    /// space ends at 4 GB; a 64-bit one at the user-mode limit.
    /// </summary>
    public bool IsRepresentableAddress(ulong address)
        => address <= (PointerSize == 4 ? 0xFFFF_FFFFUL : 0x0000_7FFF_FFFF_FFFFUL);

    /// <summary>
    /// True when the address lies inside committed, readable, non-guard memory.
    ///
    /// This asks <c>VirtualQueryEx</c> directly instead of consulting <see cref="IsRangeReadable"/>'s
    /// cached region map, so it stays correct between refreshes (the target allocates and frees while it
    /// runs). It is the check a pointer chain needs after its last step: that final address is never
    /// dereferenced again, so a step that read a non-pointer word would otherwise be reported as a
    /// successful walk producing a bogus address.
    /// </summary>
    public bool IsAddressReadable(ulong address)
    {
        ThrowIfDisposed();
        if (address == 0 || !IsRepresentableAddress(address))
        {
            return false;
        }

        return TryQueryRegion(address, out var region, out _)
               && region.State == MemCommit
               && region.IsReadable;
    }

    /// <summary>
    /// <c>VirtualQueryEx</c> with the address passed through unchanged.
    ///
    /// The earlier form was <c>(nint)(uint)address</c>, which <b>truncates every address above 4 GB to
    /// its low 32 bits</b> on a 64-bit host. Against a 32-bit target that was harmless (no such address
    /// exists), but against a 64-bit process it queried the wrong address — which is why the 64-bit
    /// region walk appeared to work yet only ever saw a couple of low regions.
    /// </summary>
    private nuint VirtualQueryExAt(ulong address, out MemoryBasicInformation64 mbi, nuint size)
        => VirtualQueryEx(_handle, (nint)(long)address, out mbi, size);

    /// <summary>Cached region count without re-walking the address space.</summary>
    public int RegionCount
    {
        get
        {
            lock (_regions)
            {
                return _regions.Count;
            }
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes or throws. The region map is consulted first
    /// because <c>ReadProcessMemory</c> fails the whole call when any part of the range is
    /// inaccessible.
    /// </summary>
    public byte[] Read(ulong address, int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var buffer = new byte[length];
        if (!TryRead(address, buffer, out var read) || read != length)
        {
            throw new InvalidOperationException(
                $"ReadProcessMemory could not read {length} byte(s) at 0x{address:X} (read {read}).");
        }

        return buffer;
    }

    /// <summary>Reads as much as possible; returns false when no byte could be read.</summary>
    public bool TryRead(ulong address, byte[] buffer, out int bytesRead)
    {
        ThrowIfDisposed();
        bytesRead = 0;

        if (buffer.Length == 0)
        {
            return true;
        }

        // Validate against the readable region map so the all-or-nothing semantics of
        // ReadProcessMemory cannot silently zero part of the request.
        if (!IsRangeReadable(address, (ulong)buffer.Length))
        {
            return false;
        }

        var offset = 0;
        while (offset < buffer.Length)
        {
            var chunk = Math.Min(ReadChunkSize, buffer.Length - offset);
            if (!NativeReadPartial(address + (ulong)offset, buffer, offset, chunk, out var got))
            {
                if (offset == 0)
                {
                    return false;
                }

                break;
            }

            offset += got;
            if (got == 0)
            {
                break;
            }
        }

        bytesRead = offset;
        return offset > 0;
    }

    /// <summary>
    /// Reads a single 4 KB chunk. The managed buffer is pinned for the duration of the call only 鈥?    /// nothing is retained and nothing is written back to the target.
    /// </summary>
    private unsafe bool NativeReadPartial(ulong address, byte[] buffer, int offset, int count, out int bytesRead)
    {
        bytesRead = 0;
        fixed (byte* pinned = &buffer[offset])
        {
            var success = ReadProcessMemory(_handle, (nint)(uint)address, pinned, (nuint)count, out var read);
            bytesRead = (int)read;
            return success;
        }
    }

    /// <summary>True when every byte of the range lies inside committed, readable, non-guard memory.</summary>
    public bool IsRangeReadable(ulong address, ulong length)
    {
        if (length == 0)
        {
            return true;
        }

        var end = address + length;
        // Never re-walk the address space here. The old form called EnumerateRegions() on every miss,
        // and because a bulk read validates each 4 KB chunk, an 8 MB read triggered 2,048 full walks.
        // The caller decides when to refresh (EnumerateRegions / RefreshRegions).
        IReadOnlyList<MemoryRegion> map;
        lock (_regions)
        {
            if (_regions.Count == 0)
            {
                EnumerateRegions();
            }

            map = _regions.ToArray();
        }

        foreach (var region in map)
        {
            if (region.BaseAddress <= address && region.End >= end)
            {
                return true;
            }

            if (region.BaseAddress >= end)
            {
                break;
            }
        }

        return false;
    }

    public bool TryRead(ulong address, int length, out byte[] bytes)
    {
        var buffer = new byte[length];
        if (TryRead(address, buffer, out var read) && read == length)
        {
            bytes = buffer;
            return true;
        }

        bytes = Array.Empty<byte>();
        return false;
    }

    public bool TryReadBytes(ulong address, int length, out byte[] bytes) => TryRead(address, length, out bytes);

    /// <summary>
    /// Reads an unsigned pointer using the target's width. The width comes from
    /// <see cref="PointerSize"/>, which was asserted at open time.
    /// </summary>
    public bool TryReadPointer(ulong address, out ulong value)
    {
        value = 0;
        var size = PointerSize;
        if (!TryRead(address, size, out var bytes))
        {
            return false;
        }

        value = size == 4
            ? BitConverter.ToUInt32(bytes, 0)
            : BitConverter.ToUInt64(bytes, 0);
        return true;
    }

    public bool TryReadUInt32(ulong address, out uint value)
    {
        value = 0;
        if (!TryRead(address, 4, out var bytes))
        {
            return false;
        }

        value = BitConverter.ToUInt32(bytes, 0);
        return true;
    }

    public bool TryReadInt32(ulong address, out int value)
    {
        value = 0;
        if (!TryRead(address, 4, out var bytes))
        {
            return false;
        }

        value = BitConverter.ToInt32(bytes, 0);
        return true;
    }

    public bool TryReadInt64(ulong address, out long value)
    {
        value = 0;
        if (!TryRead(address, 8, out var bytes))
        {
            return false;
        }

        value = BitConverter.ToInt64(bytes, 0);
        return true;
    }

    public bool TryReadUInt16(ulong address, out ushort value)
    {
        value = 0;
        if (!TryRead(address, 2, out var bytes))
        {
            return false;
        }

        value = BitConverter.ToUInt16(bytes, 0);
        return true;
    }

    public bool TryReadByte(ulong address, out byte value)
    {
        value = 0;
        if (!TryRead(address, 1, out var bytes))
        {
            return false;
        }

        value = bytes[0];
        return true;
    }

    /// <summary>
    /// Reads a non-ASCII native string (UTF-8 preferred, falling back to the system code page) from
    /// the target. Used for the beatmap hashes/folder names, which are plain ASCII byte arrays in
    /// osu!stable but can contain CJK for the player name.
    /// </summary>
    public string? TryReadString(ulong address, int maxBytes = 512)
    {
        if (address == 0 || maxBytes <= 0)
        {
            return null;
        }

        // Read a page-ish window; the terminator is almost always within the first few dozen bytes.
        var window = Math.Min(maxBytes, ReadChunkSize);
        if (!TryRead(address, window, out var bytes))
        {
            return null;
        }

        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        try
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, length);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Enumerates the target's modules. <c>TH32CS_SNAPMODULE32</c> is required when a 64-bit host
    /// snapshots a 32-bit process, and <c>ERROR_BAD_LENGTH</c> is a documented transient that must be
    /// retried (plan 搂3.4.1 item 3).
    /// </summary>
    public IReadOnlyList<ProcessModuleInfo> EnumerateModules(int maxRetries = 5, int retryDelayMs = 60)
    {
        ThrowIfDisposed();

        for (var attempt = 0; ; attempt++)
        {
            if (TryEnumerateModules(out var modules, out var error))
            {
                return modules;
            }

            if ((error == ErrorBadLength || error == ErrorPartialCopy) && attempt < maxRetries)
            {
                // Documented transient of the 64-bit-host/32-bit-target direction.
                Thread.Sleep(retryDelayMs);
                continue;
            }

            throw new Win32Exception(
                error,
                $"module enumeration failed for pid {ProcessId} (Win32 error {error})" +
                (error == ErrorAccessDenied ? "; the target may be elevated" : string.Empty) +
                $" [TH32CS_SNAPMODULE|TH32CS_SNAPMODULE32, attempt {attempt + 1}].");
        }
    }

    /// <summary>One snapshot attempt. Returns false with the Win32 error so the caller can retry.</summary>
    private bool TryEnumerateModules(out IReadOnlyList<ProcessModuleInfo> modules, out int error)
    {
        modules = Array.Empty<ProcessModuleInfo>();
        error = 0;

        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapModule | Th32CsSnapModule32, (uint)ProcessId);
        if (snapshot.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            snapshot.Dispose();
            return false;
        }

        try
        {
            var list = new List<ProcessModuleInfo>();
            var entry = ModuleEntry32.Create();
            if (!Module32First(snapshot, ref entry))
            {
                error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreFiles)
                {
                    modules = list;
                    return true;
                }

                return false;
            }

            do
            {
                list.Add(new ProcessModuleInfo(
                    entry.GetModuleName(),
                    entry.GetExePath(),
                    (ulong)entry.modBaseAddr,
                    entry.modBaseSize));
                entry.dwSize = (uint)Marshal.SizeOf<ModuleEntry32>();
            }
            while (Module32Next(snapshot, ref entry));

            modules = list;
            return true;
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    public ProcessModuleInfo? FindModule(string name)
    {
        foreach (var module in EnumerateModules())
        {
            if (module.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(module.Path).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return module;
            }
        }

        return null;
    }

    /// <summary>Reads a NUL-terminated ASCII string out of a fixed-size native buffer.</summary>
    internal static string ReadFixedString(byte[] bytes, int offset, int maxLength)
    {
        var end = offset;
        var limit = Math.Min(offset + maxLength, bytes.Length);
        while (end < limit && bytes[end] != 0)
        {
            end++;
        }

        return System.Text.Encoding.ASCII.GetString(bytes, offset, end - offset);
    }

    private static bool QueryWow64(SafeProcessHandle handle, out bool isWow64)
    {
        isWow64 = false;
        if (!Environment.Is64BitOperatingSystem)
        {
            return false;
        }

        if (!NativeIsWow64Process(handle, out var result))
        {
            return false;
        }

        isWow64 = result;
        return true;
    }

    private static string TryGetName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return $"pid-{processId}";
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation64
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ModuleEntry32
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public nint modBaseAddr;
        public uint modBaseSize;
        public nint hModule;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public char[] szModule;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 260)]
        public char[] szExePath;

        public static ModuleEntry32 Create() => new()
        {
            dwSize = (uint)Marshal.SizeOf<ModuleEntry32>(),
            szModule = new char[256],
            szExePath = new char[260]
        };

        public string GetModuleName()
        {
            var end = Array.IndexOf(szModule, '\0');
            return end < 0 ? new string(szModule) : new string(szModule, 0, end);
        }

        public string GetExePath()
        {
            var end = Array.IndexOf(szExePath, '\0');
            return end < 0 ? new string(szExePath) : new string(szExePath, 0, end);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadProcessMemory")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadProcessMemory(SafeProcessHandle hProcess, nint lpBaseAddress, byte* lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(SafeProcessHandle hProcess, nint lpAddress, out MemoryBasicInformation64 lpBuffer, nuint dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeSnapshotHandle CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Module32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32First(SafeSnapshotHandle hSnapshot, ref ModuleEntry32 lpme);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Module32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32Next(SafeSnapshotHandle hSnapshot, ref ModuleEntry32 lpme);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "IsWow64Process")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeIsWow64Process(SafeProcessHandle hProcess, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);
}

/// <summary>Minimal module description (name, path, base address, size in bytes).</summary>
public readonly record struct ProcessModuleInfo(string Name, string Path, ulong BaseAddress, ulong Size)
{
    public ulong End => BaseAddress + Size;

    public override string ToString() => $"{Name} base=0x{BaseAddress:X} size=0x{Size:X}";
}

/// <summary>Safe handle for toolhelp snapshots so a fault cannot leak the kernel handle.</summary>
internal sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeSnapshotHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}

/// <summary>
/// Optional capability of an <see cref="IMemoryReader"/>: "is this address committed, readable memory
/// right now?". Kept separate from <see cref="IMemoryReader"/> so in-memory fakes stay trivial while
/// <see cref="MemoryChainWalker"/> can still validate the address a chain ends at.
/// </summary>
public interface IMemoryAddressMap
{
    bool IsAddressReadable(ulong address);
}

/// <summary>
/// The read surface the scanner and the state reader need, so both can be unit-tested against an
/// in-memory fake without a real process.
/// </summary>
public interface IMemoryReader
{
    int PointerSize { get; }

    bool TryRead(ulong address, byte[] buffer, out int bytesRead);

    bool TryRead(ulong address, int length, out byte[] bytes);

    bool TryReadBytes(ulong address, int length, out byte[] bytes);

    bool TryReadPointer(ulong address, out ulong value);

    bool TryReadInt32(ulong address, out int value);

    bool TryReadUInt32(ulong address, out uint value);

    bool TryReadInt64(ulong address, out long value);

    bool TryReadUInt16(ulong address, out ushort value);

    bool TryReadByte(ulong address, out byte value);

    string? TryReadString(ulong address, int maxBytes = 512);
}

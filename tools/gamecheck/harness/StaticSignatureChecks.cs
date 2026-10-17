using System.Globalization;
using System.Text;
using ManiaReplayMaster.Game.Memory;

namespace ManiaReplayMaster.GameCheck;

/// <summary>
/// Static validation of the osu!stable signature table (plan section 3.4.2) against the <b>on-disk bytes</b>
/// of <c>osu!.exe</c>, with no game running.
///
/// <para><b>The result this check established (2026-10, build b20260924):</b> <c>osu!.exe</c> is a
/// <i>managed .NET assembly</i> - it carries a CLR data directory, imports only <c>mscoree.dll</c>, and its
/// 4.2 MB <c>.text</c> section holds IL. The x86 instruction sequences quoted by the tosu/gosumemory tables
/// therefore do not exist in the mapped image at all (0 of 10 patterns match), because they are
/// JIT-compiled into the CLR's executable heaps while the game runs. tosu's own scanner confirms the model:
/// its native <c>find_pattern</c> walks every committed region with <c>VirtualQueryEx</c>, not the module
/// image.</para>
///
/// <para>Consequences, which this check now asserts instead of pretending the patterns should be present:
/// (a) every real table entry must be marked <see cref="SignatureEntry.SearchWholeProcess"/>, otherwise the
/// scanner silently finds 0 of 10 on a live game; (b) static analysis of the exe cannot validate the table's
/// <i>semantics</i> - that still needs a running game. If a future osu! build ships native code, this check
/// flips to the "must match exactly once" mode automatically.</para>
///
/// <para>The exe is parsed as a PE image rather than scanned as a flat byte array: only the bytes the loader
/// actually maps (<c>min(VirtualSize, SizeOfRawData)</c> per section) are searched, so file padding and
/// cross-section byte runs are never reported as hits.</para>
/// </summary>
internal static class StaticSignatureChecks
{
    private const int MaxMatches = 16;

    public static void Run(CheckRunner runner, string[] args)
    {
        var exePath = Arg(args, "--exe") ?? Path.Combine(@"D:\Game\osu", "osu!.exe");
        var reportPath = Arg(args, "--report");

        runner.Section("Static signature table vs. on-disk osu!.exe");
        if (!File.Exists(exePath))
        {
            runner.Skip("osu!.exe readable", $"not found at '{exePath}'");
            return;
        }

        var fileLength = new FileInfo(exePath).Length;
        var image = PeImage.Load(exePath);
        var table = SignatureTables.Stable;

        runner.Metric("exe", exePath);
        runner.Metric("exe size", $"{fileLength:N0} bytes");
        runner.Metric("PE machine", image.MachineName);
        runner.Metric("PE image base", $"0x{image.ImageBase:X}");
        runner.Metric("PE size of image", $"0x{image.SizeOfImage:X}");
        runner.Metric("PE sections", string.Join(", ", image.Sections.Select(s => $"{s.Name}@RVA 0x{s.VirtualAddress:X}+0x{s.MappedLength:X}")));
        runner.Metric("CLR directory", $"RVA 0x{image.ClrDirectoryRva:X} size 0x{image.ClrDirectorySize:X}");
        runner.Metric("table", $"{table.Name} / {table.GameVersionRange}");
        runner.Metric("entries", table.Entries.Count);

        runner.Check(image.Machine == 0x014C, "the image is 32-bit x86 (the osu!stable target)",
            $"{image.MachineName} (0x{image.Machine:X4})");

        // The decisive structural fact: a managed assembly means the native patterns cannot be in the image.
        runner.Check(image.IsManagedAssembly,
            "osu!.exe is a managed .NET assembly (CLR directory present)",
            image.IsManagedAssembly
                ? $"CLR directory RVA 0x{image.ClrDirectoryRva:X}, size 0x{image.ClrDirectorySize:X}; .text holds IL, so native signatures cannot match here"
                : "no CLR directory: this would be a native image, in which case the patterns must be present");

        var report = new StringBuilder();
        report.AppendLine("# Static signature report - osu!stable");
        report.AppendLine();
        report.AppendLine($"- exe: `{exePath}` ({fileLength:N0} bytes)");
        report.AppendLine($"- PE: {image.MachineName}, image base 0x{image.ImageBase:X}, size of image 0x{image.SizeOfImage:X}");
        report.AppendLine($"- CLR directory: RVA 0x{image.ClrDirectoryRva:X}, size 0x{image.ClrDirectorySize:X} ({image.MachineName})");
        report.AppendLine($"- managed assembly: **{image.IsManagedAssembly}**");
        report.AppendLine($"- table: {table.Name} - {table.GameVersionRange}");
        report.AppendLine();
        report.AppendLine("| signature | pattern | matches in image | process-wide | section @ RVA | note |");
        report.AppendLine("|---|---|---|---|---|---|");

        var matchedInImage = 0;
        var wholeProcessEntries = 0;
        var parseErrors = 0;

        foreach (var entry in table.Entries)
        {
            MemoryPattern pattern;
            try
            {
                pattern = MemoryPattern.Parse(entry.Pattern);
            }
            catch (Exception ex)
            {
                parseErrors++;
                runner.Fail($"signature '{entry.Name}' parses", $"{entry.Pattern}: {ex.Message}");
                report.AppendLine($"| `{entry.Name}` | `{entry.Pattern}` | **parse error** | - | - | {ex.Message} |");
                continue;
            }

            if (entry.SearchWholeProcess)
            {
                wholeProcessEntries++;
            }

            var matches = image.FindAll(pattern, MaxMatches);
            matchedInImage += matches.Count;

            var where = matches.Count == 0
                ? "-"
                : string.Join("<br>", matches.Take(4).Select(m => $"{m.Section.Name}@0x{m.Rva:X}"));

            var note = matches.Count == 0
                ? "absent from the image (expected: JIT-only code)"
                : $"present at {matches.Count} location(s)";

            runner.Metric($"  {entry.Name}",
                $"{matches.Count} match(es) in image; process-wide={entry.SearchWholeProcess}" +
                (matches.Count == 0 ? string.Empty : $"; first {matches[0].Section.Name}@RVA 0x{matches[0].Rva:X}"));

            report.AppendLine(
                $"| `{entry.Name}` | `{entry.Pattern}` | {matches.Count} | {entry.SearchWholeProcess} | {where} | {note} |");
        }

        // Mode A (what this build actually is): managed IL image. The only meaningful static assertions are
        // structural, plus the guard that the table asks for a whole-process scan.
        // Mode B (a native build): the table must resolve uniquely inside the image, as originally intended.
        runner.Section("Static signature summary");
        runner.Metric("signatures in table", table.Entries.Count);
        runner.Metric("signatures matching in the image", matchedInImage);
        runner.Metric("entries marked whole-process", $"{wholeProcessEntries}/{table.Entries.Count}");
        runner.Metric("parse errors", parseErrors);

        if (image.IsManagedAssembly)
        {
            runner.CheckEqual(matchedInImage, 0,
                "no signature matches the managed IL image (proves a module-image scan cannot find them)");
            runner.CheckEqual(wholeProcessEntries, table.Entries.Count,
                "every real table entry is marked SearchWholeProcess (a module-image scan finds 0 of 10 live)");
            report.AppendLine();
            report.AppendLine("## Verdict");
            report.AppendLine();
            report.AppendLine("`osu!.exe` is a managed IL assembly, so the signature table **cannot be validated statically against the exe**. " +
                              "The native patterns exist only as JIT-compiled code in the CLR's executable heaps at runtime. This matches " +
                              "tosu's implementation, whose native `find_pattern` sweeps every committed region with `VirtualQueryEx` " +
                              "instead of the module image. The table entries are therefore all `SearchWholeProcess = true`; " +
                              "whether each resolved address is *semantically* the beatmap/score on screen still requires a running game.");
        }
        else
        {
            runner.CheckEqual(matchedInImage, table.Entries.Count,
                "every signature occurs in the native image");
            report.AppendLine();
            report.AppendLine("Native image detected: the patterns are present, so unique-match and operand checks apply.");
        }

        report.AppendLine();
        report.AppendLine("Interpretation rules:");
        report.AppendLine();
        report.AppendLine("- For a managed image, `matches = 0` is the **expected** result, not a table failure: the patterns are IL-JIT code.");
        report.AppendLine("- For a native image, `matches = 1` is required per entry: `SignatureScanner.Resolve` takes `matches[0]`, so an ambiguous pattern is a coin flip.");
        report.AppendLine($"- Runtime addresses are `module base + RVA`; absolute file bytes would be preferred-image-base VAs (image base 0x{image.ImageBase:X}).");
        report.AppendLine("- Static analysis can never confirm semantics (that the MD5 read from memory is the map on screen). That needs a running game (plan V2-V7).");

        if (!string.IsNullOrEmpty(reportPath))
        {
            var full = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, report.ToString(), new UTF8Encoding(false));
            runner.Metric("report written", full);
        }
    }

    private static string? Arg(string[] args, string name)
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

/// <summary>A mapped PE section: only the bytes the loader actually maps are kept.</summary>
internal sealed record PeSection(
    string Name,
    uint VirtualAddress,
    uint VirtualSize,
    uint PointerToRawData,
    uint SizeOfRawData,
    uint Characteristics,
    byte[] Mapped)
{
    public uint MappedLength => (uint)Mapped.Length;

    public uint RvaEnd => VirtualAddress + MappedLength;

    /// <summary>True for .data-style sections that hold globals (writable, not executable).</summary>
    public bool IsWritableData
        => (Characteristics & 0x8000_0000) != 0        // IMAGE_SCN_MEM_WRITE
           && (Characteristics & 0x2000_0000) == 0;    // not IMAGE_SCN_MEM_EXECUTE

    public override string ToString() => $"{Name}@RVA 0x{VirtualAddress:X}+0x{MappedLength:X}";
}

internal readonly record struct PeMatch(PeSection Section, uint Rva);

/// <summary>
/// Minimal PE32/PE32+ reader: the headers, the section table and the CLR directory, which is all the
/// evidence gathering needs. Deliberately small - this is harness code, not a product dependency.
/// </summary>
internal sealed class PeImage
{
    private readonly byte[] _file;
    private readonly uint _imageBase;

    private PeImage(
        string path,
        byte[] file,
        ushort machine,
        uint imageBase,
        uint sizeOfImage,
        uint clrDirectoryRva,
        uint clrDirectorySize,
        List<PeSection> sections)
    {
        Path = path;
        _file = file;
        Machine = machine;
        _imageBase = imageBase;
        SizeOfImage = sizeOfImage;
        ClrDirectoryRva = clrDirectoryRva;
        ClrDirectorySize = clrDirectorySize;
        Sections = sections;
    }

    public string Path { get; }

    public ushort Machine { get; }

    public uint ImageBase => _imageBase;

    public uint SizeOfImage { get; }

    /// <summary>RVA/size of the CLR (COM descriptor) data directory; non-zero means a managed assembly.</summary>
    public uint ClrDirectoryRva { get; }

    public uint ClrDirectorySize { get; }

    /// <summary>True when the image carries .NET metadata rather than native code.</summary>
    public bool IsManagedAssembly => ClrDirectoryRva != 0 && ClrDirectorySize != 0;

    public IReadOnlyList<PeSection> Sections { get; }

    public string MachineName => Machine switch
    {
        0x014C => "I386 (32-bit)",
        0x8664 => "AMD64 (64-bit)",
        0x01C4 => "ARMNT",
        0xAA64 => "ARM64",
        _ => $"0x{Machine:X4}"
    };

    public static PeImage Load(string path)
    {
        var file = File.ReadAllBytes(path);
        if (file.Length < 0x40 || file[0] != (byte)'M' || file[1] != (byte)'Z')
        {
            throw new InvalidDataException($"'{path}' is not a PE file (no MZ header).");
        }

        var peOffset = (int)ReadUInt32(file, 0x3C);
        if (peOffset < 0 || peOffset + 24 > file.Length
            || file[peOffset] != (byte)'P' || file[peOffset + 1] != (byte)'E'
            || file[peOffset + 2] != 0 || file[peOffset + 3] != 0)
        {
            throw new InvalidDataException($"'{path}' has no PE signature at 0x{peOffset:X}.");
        }

        var coff = peOffset + 4;
        var machine = ReadUInt16(file, coff);
        var sectionCount = ReadUInt16(file, coff + 2);
        var optionalSize = ReadUInt16(file, coff + 16);
        var optional = coff + 20;
        var magic = ReadUInt16(file, optional);
        var imageBase = magic switch
        {
            0x10B => ReadUInt32(file, optional + 28),
            0x20B => (uint)ReadUInt64(file, optional + 24),
            _ => throw new InvalidDataException($"unknown optional-header magic 0x{magic:X}")
        };
        var sizeOfImage = ReadUInt32(file, optional + 56);

        // Data directories: number-of-RVA-and-sizes at optional+92 (PE32) / +108 (PE32+), the array itself
        // 4 bytes later. Directory 14 is the CLR header, which is what identifies a managed assembly.
        var numberOfDirectories = ReadUInt32(file, optional + (magic == 0x10B ? 92 : 108));
        var directories = optional + (magic == 0x10B ? 96 : 112);
        uint clrRva = 0, clrSize = 0;
        if (numberOfDirectories > 14 && directories + (15 * 8) <= file.Length)
        {
            clrRva = ReadUInt32(file, directories + (14 * 8));
            clrSize = ReadUInt32(file, directories + (14 * 8) + 4);
        }

        var sections = new List<PeSection>(sectionCount);
        var sectionTable = optional + optionalSize;
        for (var i = 0; i < sectionCount; i++)
        {
            var offset = sectionTable + (i * 40);
            if (offset + 40 > file.Length)
            {
                break;
            }

            var name = Encoding.ASCII.GetString(file, offset, 8).TrimEnd('\0');
            var virtualSize = ReadUInt32(file, offset + 8);
            var virtualAddress = ReadUInt32(file, offset + 12);
            var rawSize = ReadUInt32(file, offset + 16);
            var rawPointer = ReadUInt32(file, offset + 20);
            var characteristics = ReadUInt32(file, offset + 36);

            // Only min(VirtualSize, SizeOfRawData) is both file-backed and mapped. VirtualSize == 0 means
            // "the section is not zero-terminated" and the raw size is authoritative.
            var mappedLength = virtualSize == 0 ? rawSize : Math.Min(virtualSize, rawSize);
            if (rawPointer >= file.Length || mappedLength == 0)
            {
                sections.Add(new PeSection(name, virtualAddress, virtualSize, rawPointer, rawSize, characteristics, Array.Empty<byte>()));
                continue;
            }

            mappedLength = Math.Min(mappedLength, (uint)(file.Length - (int)rawPointer));
            var mapped = new byte[mappedLength];
            Array.Copy(file, (int)rawPointer, mapped, 0, (int)mappedLength);
            sections.Add(new PeSection(name, virtualAddress, virtualSize, rawPointer, rawSize, characteristics, mapped));
        }

        return new PeImage(path, file, machine, imageBase, sizeOfImage, clrRva, clrSize, sections);
    }

    public PeSection? SectionForRva(ulong rva)
    {
        foreach (var section in Sections)
        {
            if (rva >= section.VirtualAddress && rva < section.RvaEnd)
            {
                return section;
            }
        }

        return null;
    }

    public bool ContainsRva(ulong rva) => SectionForRva(rva) is not null;

    public bool TryReadUInt32(ulong rva, out uint value)
    {
        value = 0;
        var section = SectionForRva(rva);
        if (section is null)
        {
            return false;
        }

        var index = (int)(rva - section.VirtualAddress);
        if (index + 4 > section.Mapped.Length)
        {
            return false;
        }

        value = ReadUInt32(section.Mapped, index);
        return true;
    }

    public bool TryReadInt32(ulong rva, out int value)
    {
        var ok = TryReadUInt32(rva, out var raw);
        value = unchecked((int)raw);
        return ok;
    }

    /// <summary>Finds every occurrence of the pattern in the mapped part of each section.</summary>
    public IReadOnlyList<PeMatch> FindAll(MemoryPattern pattern, int maxMatches)
    {
        var matches = new List<PeMatch>();
        if (pattern.Length == 0)
        {
            return matches;
        }

        foreach (var section in Sections)
        {
            if (section.Mapped.Length < pattern.Length)
            {
                continue;
            }

            var local = new List<ulong>(maxMatches);
            SignatureScanner.FindAll(section.Mapped, pattern, 0, local, maxMatches);
            foreach (var offset in local)
            {
                matches.Add(new PeMatch(section, section.VirtualAddress + (uint)offset));
                if (matches.Count >= maxMatches)
                {
                    return matches;
                }
            }
        }

        return matches;
    }

    public string HexAt(ulong rva, int length)
    {
        var section = SectionForRva(rva);
        if (section is null)
        {
            return "(outside image)";
        }

        var index = (int)(rva - section.VirtualAddress);
        var count = Math.Min(length, section.Mapped.Length - index);
        return count <= 0
            ? "(empty)"
            : Convert.ToHexString(section.Mapped, index, count);
    }

    private static ushort ReadUInt16(byte[] data, int offset)
        => (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint ReadUInt32(byte[] data, int offset)
        => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static ulong ReadUInt64(byte[] data, int offset)
        => ReadUInt32(data, offset) | ((ulong)ReadUInt32(data, offset + 4) << 32);
}

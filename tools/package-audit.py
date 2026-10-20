"""Validate native package contents and create a ZIP using DEFLATE level 6.

Usage: package-audit.py <folder> <zip> [--baseline <WPF-folder>]
Both measurements use the identical ZIP method, including native DLLs and notices.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import zipfile

def require(condition, message):
    if not condition:
        raise ValueError(message)

def managed_pe(path):
    data = path.read_bytes()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    require(data[:2] == b"MZ" and data[pe:pe + 4] == b"PE\0\0", f"Invalid PE: {path}")
    optional = pe + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    require(magic in {0x10B, 0x20B}, f"Invalid PE optional header: {path}")
    directories = optional + (112 if magic == 0x20B else 96)
    return struct.unpack_from("<II", data, directories + 14 * 8) != (0, 0)

def audit(folder, archive, native=True):
    files = sorted(p for p in folder.rglob("*") if p.is_file())
    if native:
        for required in ("ManiaReplayViewer.exe", "ffmpeg.exe", "licenses/Avalonia.txt"):
            require((folder / required).is_file(), f"Missing package file: {required}")
        for path in files:
            require(path.suffix.lower() not in {".mp4", ".pdb"}, f"Unexpected package file: {path}")
            require(not path.name.lower().endswith((".deps.json", ".runtimeconfig.json")), f"Runtime metadata: {path}")
            require(path.name.lower() not in {"ffprobe.exe", "ffplay.exe", "coreclr.dll", "clrjit.dll"}, f"Unexpected runtime: {path}")
            if path.suffix.lower() in {".exe", ".dll"}:
                require(not managed_pe(path), f"Managed PE in native package: {path}")
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as output:
        for path in files:
            output.write(path, path.relative_to(folder).as_posix())
    return {
        "compression": "ZIP DEFLATE level 6",
        "exe_bytes": (folder / ("ManiaReplayViewer.exe" if native else "ManiaReplayMaster.exe")).stat().st_size,
        "total_bytes": sum(p.stat().st_size for p in files),
        "zip_bytes": archive.stat().st_size,
        "files": [{"path": p.relative_to(folder).as_posix(), "bytes": p.stat().st_size,
                   "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for p in files],
    }

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("folder", type=Path)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--baseline", type=Path)
    args = parser.parse_args()
    report = {"aot": audit(args.folder, args.archive)}
    if args.baseline:
        require(hashlib.sha256((args.folder / "ffmpeg.exe").read_bytes()).digest() == hashlib.sha256((args.baseline / "ffmpeg.exe").read_bytes()).digest(), "FFmpeg changed")
        report["baseline"] = audit(args.baseline, args.archive.with_name("baseline-clean.zip"), False)
    args.archive.with_suffix(".measurements.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    for name, values in report.items():
        print(name, {k: v for k, v in values.items() if k != "files"})

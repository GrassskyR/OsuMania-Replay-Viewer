"""Check package provenance and integrity with isolated synthetic native PE files."""
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("package_audit", Path(__file__).resolve().parents[1] / "package-audit.py")
audit_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit_module)


class PackageAuditTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.package = self.root / "package"
        (self.package / "licenses").mkdir(parents=True)
        data = bytearray(512)
        data[:2] = b"MZ"
        struct.pack_into("<I", data, 0x3C, 0x80)
        data[0x80:0x84] = b"PE\0\0"
        struct.pack_into("<H", data, 0x98, 0x20B)
        for name in ("ManiaReplayViewer.exe", "ffmpeg.exe"):
            (self.package / name).write_bytes(data)
        (self.package / "licenses/Avalonia.txt").write_text("fixture license", encoding="utf-8")
        self.info = {
            "version": "3.1.0-rc.1",
            "source_revision": "a" * 40,
            "ffmpeg_sha256": hashlib.sha256(data).hexdigest(),
        }
        self.write_info()
        self.archive = self.root / "package.zip"

    def write_info(self):
        (self.package / "build-info.json").write_text(json.dumps(self.info), encoding="utf-8-sig")

    def test_archive_and_manifest_identify_the_same_files_and_build(self):
        report = audit_module.audit(self.package, self.archive)
        self.assertEqual(report["build"], self.info)
        self.assertEqual(report["zip_sha256"], hashlib.sha256(self.archive.read_bytes()).hexdigest())
        with zipfile.ZipFile(self.archive) as archive:
            self.assertEqual(set(archive.namelist()), {f["path"] for f in report["files"]})
            for file in report["files"]:
                self.assertEqual(file["sha256"], hashlib.sha256(archive.read(file["path"])).hexdigest())

    def test_rejects_missing_build_provenance(self):
        (self.package / "build-info.json").unlink()
        with self.assertRaisesRegex(ValueError, "Missing package file"):
            audit_module.audit(self.package, self.archive)

    def test_rejects_mismatched_encoder_provenance(self):
        self.info["ffmpeg_sha256"] = "0" * 64
        self.write_info()
        with self.assertRaisesRegex(ValueError, "checksum mismatch"):
            audit_module.audit(self.package, self.archive)

    def test_rejects_managed_binary(self):
        path = self.package / "ManiaReplayViewer.exe"
        data = bytearray(path.read_bytes())
        struct.pack_into("<II", data, 0x98 + 112 + 14 * 8, 1, 1)
        path.write_bytes(data)
        with self.assertRaisesRegex(ValueError, "Managed PE"):
            audit_module.audit(self.package, self.archive)


if __name__ == "__main__":
    unittest.main()

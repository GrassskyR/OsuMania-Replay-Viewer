"""Generate small redistributable MR/OSR fixtures without a game installation."""
from pathlib import Path
import hashlib
import lzma
import struct

root = Path(__file__).resolve().parents[2]
fixtures = root / "fixtures"

def mr_string(text):
    data = text.encode("utf-8")
    return struct.pack("<i", len(data)) + data

def osu_string(text):
    data = text.encode("utf-8")
    assert len(data) < 128
    return b"\x0b" + bytes([len(data)]) + data

mc = fixtures / "aot-minimal.mc"
data = mr_string("mr format head") + struct.pack("<i", 1)
for text in [hashlib.md5(mc.read_bytes()).hexdigest(), "AOT", "Minimal", "fixture"]:
    data += mr_string(text)
data += struct.pack("<9i", 1000, 2, 2, 0, 0, 0, 0, 0, 0)
data += mr_string("mr data") + struct.pack("<iiBii", 1, 4, 0, 2000, 0)
for time, press, column in [(1000, 1, 0), (1050, 2, 0), (1500, 1, 1), (2000, 2, 1)]:
    data += struct.pack("<iBB", time, press, column)
(fixtures / "aot-minimal.mr").write_bytes(data)

beatmap = fixtures / "aot-taiko.osu"
beatmap.write_text("""osu file format v14

[General]
AudioFilename: missing-aot-bgm.wav
Mode: 1
[Metadata]
Title: AOT Taiko
Artist: Fixture
Creator: Fixture
Version: Minimal
[Difficulty]
HPDrainRate:5
CircleSize:5
OverallDifficulty:5
ApproachRate:5
SliderMultiplier:1.4
SliderTickRate:1
[TimingPoints]
0,500,4,2,1,100,1,0
[HitObjects]
256,192,1000,1,0,0:0:0:0:
256,192,1500,1,2,0:0:0:0:
256,192,2000,1,4,0:0:0:0:
""", encoding="utf-8")
data = struct.pack("<Bi", 1, 20200101)
for text in [hashlib.md5(beatmap.read_bytes()).hexdigest(), "AOT fixture", "synthetic"]:
    data += osu_string(text)
data += struct.pack("<6HiHB i", 3, 0, 0, 0, 0, 0, 900, 3, 1, 0)
data += osu_string("") + struct.pack("<q", 638000000000000000)
actions = lzma.compress(b"0|256|192|0,1000|256|192|1,1|256|192|0,499|256|192|2,1|256|192|0,499|256|192|1,1|256|192|0,-12345|0|0|0,", format=lzma.FORMAT_ALONE)
data += struct.pack("<i", len(actions)) + actions + struct.pack("<q", 0)
(fixtures / "aot-taiko.osr").write_bytes(data)
print("Generated aot-minimal.mr and aot-taiko.osu/osr")

"""Probe #12 -- use the .osu file as an oracle to locate osu!.db fields.

The .osu file of record 0 is known exactly (its MD5 matched). We know:
  set id from the Songs folder name prefix, timing points, preview time, etc.
Search the record's binary region for those values.
"""

import sys, os, re, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import open_ro, Reader
import songs_index

DB = r"D:\Game\osu\osu!.db"
OSU = r"D:\Game\osu\Songs\983778 #EmoCosine - VIVID DEBUT!\#EmoCosine - VIVID DEBUT! (Reba) [The Beginning].osu"
buf = open_ro(DB)

gen = {}
timing = []
last_time = 0
with open(OSU, "r", encoding="utf-8-sig", errors="replace") as f:
    section = None
    for line in f:
        line = line.rstrip("\r\n")
        if line.startswith("[") and line.endswith("]"):
            section = line.strip("[]")
            continue
        if not line or line.startswith("//"):
            continue
        if section == "General" and ":" in line:
            k, v = line.split(":", 1)
            gen[k.strip()] = v.strip()
        elif section == "TimingPoints" and "," in line:
            parts = line.split(",")
            timing.append((float(parts[0]), float(parts[1]), int(parts[2])))
        elif section == "HitObjects" and "," in line:
            t = int(line.split(",")[2])
            last_time = max(last_time, t)

print("General:", {k: gen.get(k) for k in ("AudioFilename", "PreviewTime", "Mode", "AudioLeadIn")})
print("timing points:", len(timing))
print("first 8 timing points (time, beatLength, tsig):", timing[:8])
print("last hitobject time:", last_time)

# ---- locate values in the record region [243, 446)
start, end = 200, 480
region = buf[start:end]
print("\nregion hex %d..%d:" % (start, end))
for o in range(200, 480, 16):
    print("  %4d: %s" % (o, buf[o:o + 16].hex(" ")))


def find_i32(val):
    return [start + i for i in range(len(region) - 4) if struct.unpack_from("<i", region, i)[0] == val]


def find_i64(val):
    return [start + i for i in range(len(region) - 8) if struct.unpack_from("<q", region, i)[0] == val]


def find_f32(val, tol=1e-4):
    out = []
    for i in range(len(region) - 4):
        v = struct.unpack_from("<f", region, i)[0]
        if abs(v - val) <= tol:
            out.append(start + i)
    return out


def find_f64(val, tol=1e-6):
    out = []
    for i in range(len(region) - 8):
        v = struct.unpack_from("<d", region, i)[0]
        if abs(v - val) <= tol:
            out.append(start + i)
    return out


for key in ("PreviewTime", "AudioLeadIn"):
    if key in gen:
        v = int(gen[key])
        print("\n%s=%d -> i32 offsets: %s" % (key, v, find_i32(v)))

print("\nlast hitobject time %d -> i32 offsets: %s" % (last_time, find_i32(last_time)))
for tp in timing[:6]:
    print("timing t=%d bl=%s -> i32(t): %s  f64(t): %s  f64(bl): %s"
          % (int(tp[0]), tp[1], find_i32(int(tp[0])), find_f64(tp[0]), find_f64(tp[1])))

# beatmap / set ids: search the whole file for the folder-name string and print surroundings
folder = "983778 #EmoCosine - VIVID DEBUT!"
fi = buf.find(folder.encode("utf-8"))
print("\nfolder string found at", fi)
print("  context -24..+40:", buf[fi - 24:fi + 40].hex(" "))
print("  983778 as i32:", find_i32(983778))

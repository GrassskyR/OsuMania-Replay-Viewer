"""Probe #19 -- explain the 10 `audio_file` mismatches and the 1 MD5 mismatch
found by the full-population osu!.db validation.
"""

import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import beatmapdb

db = beatmapdb.parse()
folders = {e.name: e.path for e in os.scandir(r"D:\Game\osu\Songs") if e.is_dir()}

bad = []
for b in db.beatmaps:
    folder = folders.get(b.folder_name)
    if not folder or not b.osu_file:
        continue
    p = os.path.join(folder, b.osu_file)
    if not os.path.isfile(p):
        continue
    info = beatmapdb.read_osu_summary(p)
    if info.get("AudioFilename") is None:
        print("NO AudioFilename in .osu:", p)
        continue
    if str(b.audio_file) != str(info["AudioFilename"]):
        bad.append((b, info["AudioFilename"], p))

print("audio_file mismatches: %d" % len(bad))
for b, disk, p in bad:
    print("  db   = %r" % b.audio_file)
    print("  osu  = %r" % disk)
    print("  file = %s" % p)
    print("  case-insensitive equal: %s" % (str(b.audio_file).lower() == str(disk).lower()))
    print()

"""Probe #15 -- hypothesis tests for the unknown trailing int32."""

import os, sys, zlib, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import beatmapdb

db = beatmapdb.parse()
folders = {e.name: e.path for e in os.scandir(r"D:\Game\osu\Songs") if e.is_dir()}

# compact histogram of the unknown tail
hist = collections.Counter(b.unknown_tail for b in db.beatmaps)
print("distinct unknown_tail values: %d ; zero count: %d" % (len(hist), hist.get(0, 0)))
print("top 15 nonzero:", [(v, c) for v, c in hist.most_common(20) if v != 0][:15])
nz = [b for b in db.beatmaps if b.unknown_tail != 0]
print("nonzero count:", len(nz))

tests = collections.Counter()
checked = 0
for b in db.beatmaps:
    folder = folders.get(b.folder_name)
    if not folder or not b.osu_file:
        continue
    p = os.path.join(folder, b.osu_file)
    if not os.path.isfile(p):
        continue
    checked += 1
    if b.unknown_tail == 0:
        continue
    data = open(p, "rb").read()
    t = b.unknown_tail
    c = zlib.crc32(data) & 0xFFFFFFFF
    if t == c:
        tests["crc32(osu)"] += 1
    if t == len(data):
        tests["len(osu)"] += 1
    hits = data.count(b"\n") + 1
    if t == hits:
        tests["line count(osu)"] += 1
    if t == (c & 0xFFFFF):
        tests["crc32&0xFFFFF"] += 1
    if t == (c & 0xFFFFFF):
        tests["crc32&0xFFFFFF"] += 1
    if t == (c & 0x7FFFFFFF):
        tests["crc32&0x7FFFFFFF"] += 1
    if t == (b.beatmap_id & 0xFFFF):
        tests["beatmap_id&0xFFFF"] += 1
    if t == (b.beatmapset_id & 0xFFFF):
        tests["set_id&0xFFFF"] += 1
    if t == (b.last_modified & 0xFFFFFFFF):
        tests["last_modified&0xFFFFFFFF"] += 1
    if t == (b.last_modified >> 32):
        tests["last_modified>>32"] += 1
    if t == (b.last_checked & 0xFFFFFFFF):
        tests["last_checked&0xFFFFFFFF"] += 1
    if t == (b.last_checked >> 32):
        tests["last_checked>>32"] += 1
    if checked > 4000:
        break

print("checked:", checked)
print("hypothesis matches:", dict(tests) or "{}")

print("\nsample nonzero rows (md5, ids, len(osu), unknown_tail, last_modified, last_checked, folder):")
shown = 0
for b in nz:
    folder = folders.get(b.folder_name)
    if not folder:
        continue
    p = os.path.join(folder, b.osu_file or "")
    if not os.path.isfile(p):
        continue
    print("  %s set=%d id=%d len=%d tail=%d lm=%d lc=%d last_played=%d"
          % (b.md5, b.beatmapset_id, b.beatmap_id, os.path.getsize(p), b.unknown_tail,
             b.last_modified, b.last_checked, b.last_played))
    shown += 1
    if shown >= 12:
        break

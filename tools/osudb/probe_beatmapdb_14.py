"""Probe #14 -- what are the last two fields of an osu!.db beatmap record
(`unknown_tail` int32 + `mania_scroll_speed` byte)?

Hypothesis: the `int32 osu_file_size` that older versions store at the START of the
record was moved to the END in this version, and/or the int/byte order is swapped.
Test against the real .osu file sizes.
"""

import os
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import beatmapdb

db = beatmapdb.parse()
folders = {e.name: e.path for e in os.scandir(r"D:\Game\osu\Songs") if e.is_dir()}

n = 0
exact_a = exact_b = 0
rows = []
for b in db.beatmaps:
    folder = folders.get(b.folder_name)
    if not folder or not b.osu_file:
        continue
    p = os.path.join(folder, b.osu_file)
    if not os.path.isfile(p):
        continue
    size = os.path.getsize(p)
    n += 1
    a = b.unknown_tail
    bshift = (b.unknown_tail >> 8) | (b.mania_scroll_speed << 24)
    if a == size:
        exact_a += 1
    if bshift == size:
        exact_b += 1
    if n <= 12:
        rows.append((b.osu_file[:40], size, a, bshift, b.unknown_tail & 0xFF, b.mania_scroll_speed))
    if n >= 3000:
        break

print("checked %d records against real .osu sizes" % n)
print("layout A (int32 then byte) int32 == file size : %d" % exact_a)
print("layout B (byte then int32) int32 == file size : %d" % exact_b)
print("\nfile | size | tail_int | tail>>8 | tail&0xff | scroll_byte")
for r in rows:
    print("  %-42s size=%-7d tail=%-9d tail>>8=%-9d low=%-4d scrollbyte=%d" % r)

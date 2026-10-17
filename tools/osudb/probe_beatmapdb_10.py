"""Probe #10 -- walk the osu!.db header and the first beatmap record field by field.

Everything is read-only. Prints each field with its absolute offset so the layout
can be verified against the .osu file that is referenced by the record.
"""

import sys, os, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import open_ro, Reader

DB = r"D:\Game\osu\osu!.db"
buf = open_ro(DB)
print("file size", len(buf))
r = Reader(buf)
print("version      @%d = %d" % (0, r.i32()))
print("folders      @%d = %d" % (4, r.i32()))
print("unlocked     @%d = %d" % (8, r.byte()))
print("unlock_ticks @%d = %d" % (9, r.i64()))
p = r.pos
player = r.legacy_string()
print("playerName   @%d = %r" % (p, player))
p = r.pos
n = r.i32()
print("beatmapCount @%d = %d" % (p, n))
print("first record starts at", r.pos)

# --- read consecutive strings until something is not a legacy string
print("\n== strings at the start of record 0 ==")
i = 0
while True:
    pos = r.pos
    if buf[pos] not in (0x00, 0x0B):
        break
    try:
        s = r.legacy_string()
    except Exception as e:  # noqa: BLE001
        r.pos = pos
        break
    if s is not None and len(s) > 512:
        r.pos = pos
        break
    print("  str[%d] @%d len=%s = %r" % (i, pos, len(s) if s is not None else None, s))
    i += 1
    if i > 30:
        break

print("\nnext 48 bytes at", r.pos, ":", buf[r.pos:r.pos + 48].hex(" "))

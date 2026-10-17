"""Probe #18 -- string marker-byte and length statistics for both databases.

Verifies the legacy-string encoding claim exhaustively:
  * every string's marker byte is 0x00 (null) or 0x0B
  * the LEB128 length always exactly covers the following UTF-8 run
  * whether any length needed more than one LEB128 byte
"""

import os, sys, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import open_ro, Reader

markers = collections.Counter()
lengths = collections.Counter()
multi_byte_leb = 0
legacy_multibyte_marker = collections.Counter()
maxlen = 0


def scan_strings(buf, r, n, label):
    global multi_byte_leb, maxlen
    for _ in range(n):
        start = r.pos
        m = r.byte()
        markers[m] += 1
        if m == 0:
            continue
        if m != 0x0B:
            legacy_multibyte_marker[m] += 1
        # LEB128
        v = 0
        shift = 0
        nbytes = 0
        while True:
            b = r.byte()
            nbytes += 1
            v |= (b & 0x7F) << shift
            if (b & 0x80) == 0:
                break
            shift += 7
        if nbytes > 1:
            multi_byte_leb += 1
        lengths[v] += 1
        maxlen = max(maxlen, v)
        r.bytes(v)


# ---- scores.db: 2690 group strings + per record 3 strings
buf = open_ro(r"D:\Game\osu\scores.db")
r = Reader(buf)
r.i32(); groups = r.i32()
for _ in range(groups):
    scan_strings(buf, r, 1, "group")
    n = r.i32()
    for _ in range(n):
        r.byte(); r.i32()             # mode, version
        scan_strings(buf, r, 3, "score")   # beatmapMd5, player, replayMd5
        r.bytes(12 + 4 + 2 + 1 + 4)   # counts, score, combo, perfect, mods
        scan_strings(buf, r, 1, "hpGraph")
        r.bytes(8 + 4 + 8)            # date, replayLength, onlineId
print("scores.db  remaining bytes after scan:", r.remaining)
print("scores.db  marker bytes:", {hex(k): v for k, v in markers.items()})
print("scores.db  non-0x0B non-zero markers:", dict(legacy_multibyte_marker))
print("scores.db  LEB128 lengths needing >1 byte:", multi_byte_leb)
print("scores.db  max string length:", maxlen, " distinct lengths:", len(lengths))

# ---- osu!.db: all strings of the first 3000 records
markers.clear(); lengths.clear(); multi_byte_leb = 0; legacy_multibyte_marker.clear(); maxlen = 0
buf2 = open_ro(r"D:\Game\osu\osu!.db")
r2 = Reader(buf2)
r2.i32(); r2.i32(); r2.byte(); r2.i64()
scan_strings(buf2, r2, 1, "player")
nbm = r2.i32()
for _ in range(3000):
    scan_strings(buf2, r2, 9, "b")     # 9 leading strings
    r2.bytes(1 + 6 + 8 + 16 + 8)       # status, counts, date, 4 floats, SV
    for _ in range(4):
        cnt = r2.i32()
        for _ in range(cnt):
            r2.byte(); r2.i32(); r2.byte()
            t = buf2[r2.pos - 1]
            r2.bytes(4 if t == 0x0C else 8)
    r2.bytes(4 * 3)                    # drain,total,preview
    ntp = r2.i32()
    r2.bytes(ntp * 17)                 # timing points: 2 doubles + bool
    r2.bytes(4 * 3 + 4 + 2 + 4 + 1)    # ids, grades, local offset, stack leniency, mode
    scan_strings(r2 and buf2, r2, 2, "source,tags")
    r2.bytes(2)                        # online offset
    scan_strings(buf2, r2, 1, "font")
    r2.bytes(1 + 8 + 1)                # unplayed, last_played, osz2
    scan_strings(buf2, r2, 1, "folder")
    r2.bytes(8 + 5 + 4 + 1)            # last_checked, 5 bools, int, byte
print("\nosu!.db    remaining bytes after 3000 records:", r2.remaining)
print("osu!.db    marker bytes:", {hex(k): v for k, v in markers.items()})
print("osu!.db    non-0x0B non-zero markers:", dict(legacy_multibyte_marker))
print("osu!.db    LEB128 lengths needing >1 byte:", multi_byte_leb)
print("osu!.db    max string length:", maxlen, " distinct lengths:", len(lengths))

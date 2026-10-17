"""Probe #3 -- locate every score-record start by structural signature.

Signature (empirically justified):
    byte  mode            <= 3
    int32 version         in [19900101, 29991231]
    byte  0x0b            legacy-string marker for the beatmap md5
    leb128 length == 32
    32 hex chars
This is extremely specific: it pins record starts without knowing the tail layout.
Then consecutive deltas give real record lengths -> the tail size falls out.
"""

import sys, os, struct, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import open_ro, ticks_to_iso

DB = r"D:\Game\osu\scores.db"
buf = open_ro(DB)
N = len(buf)

HEX = set(b"0123456789abcdef")

starts = []
for p in range(0, N - 40):
    if buf[p] > 3:
        continue
    ver = struct.unpack_from("<i", buf, p + 1)[0]
    if not (19900101 <= ver <= 29991231):
        continue
    if buf[p + 5] != 0x0B or buf[p + 6] != 0x20:
        continue
    if any(c not in HEX for c in buf[p + 7:p + 39]):
        continue
    starts.append(p)

print("record-start candidates:", len(starts))
print("first 12 offsets:", starts[:12])

# consecutive deltas -- the mode of the deltas is the record size for records
# whose player name has the same length; the minimum plausible delta reveals
# the fixed tail.
deltas = [starts[i + 1] - starts[i] for i in range(len(starts) - 1)]
hist = collections.Counter(deltas)
print("\ndelta histogram (top 12):")
for d, c in hist.most_common(12):
    print("   %5d  x%d" % (d, c))
print("min delta:", min(deltas), " max delta:", max(deltas))

# Is the group header itself also matched? group headers are 'string md5' + i32 count;
# they are preceded by the previous record's tail, so buf[p]<=3 may coincide by luck.
# Check: does d(8 -> 46) appear? i.e. is 8 in starts?
print("offset 8 (group 0 header) in starts:", 8 in starts)

# --- per group: first score starts right after the header (md5 string + i32 count)
# Derive the record length from the first group assuming 3 records:
#   scores at starts[0], starts[1], starts[2]; next group header at ? -> search
# Use group 0: find the offset where group 1 header begins.
def find_group_starts():
    r = 8
    out = []
    # walk groups: header = legacy_string + i32, then n score records; to find n
    # records we need record boundaries -> use the candidate list to align.
    # Instead: detect headers as 'legacy string of 32 hex' immediately followed by
    # i32 count and then either EOF or a valid record-start signature at +38.
    gs = []
    for p in range(0, N - 42):
        if buf[p] != 0x0B or buf[p + 1] != 0x20:
            continue
        if any(c not in HEX for c in buf[p + 2:p + 34]):
            continue
        cnt = struct.unpack_from("<i", buf, p + 34)[0]
        if not (0 < cnt <= 5000):
            continue
        q = p + 38
        if q == N:
            gs.append(p); continue
        # next must be a record start signature
        if buf[q] <= 3:
            ver = struct.unpack_from("<i", buf, q + 1)[0]
            if 19900101 <= ver <= 29991231 and buf[q + 5] == 0x0B:
                gs.append(p)
    return gs


gs = find_group_starts()
print("\ngroup-header candidates:", len(gs))
print("first 8:", gs[:8])
print("deltas of first 8:", [gs[i + 1] - gs[i] for i in range(min(7, len(gs) - 1))])
# For group 0: header at 8 (38 bytes) -> records at 46, 46+L, 46+2L ; next header at ?
if len(gs) >= 2:
    span = gs[1] - 46
    print("group0 span (3 records) = %d -> L = %s" % (span, span / 3.0))
    for L in range(int(span / 3) - 3, int(span / 3) + 4):
        if 46 + 3 * L == gs[1]:
            print("   exact L =", L)

# --- per-record: dump the tail region for the first 4 records of group 0
print("\nfirst records of group 0 (tail bytes from maxCombo):")
for i in range(min(6, len(starts))):
    p = starts[i]
    # walk fields
    r = p
    r += 1
    ver = struct.unpack_from("<i", buf, r)[0]; r += 4
    def lstr(o):
        ln = buf[o + 1]
        return buf[o + 2:o + 2 + ln].decode("utf-8", "replace"), o + 2 + ln
    bmd5, r = lstr(r)
    player, r = lstr(r)
    rmd5, r = lstr(r)
    counts = struct.unpack_from("<6H", buf, r); r += 12
    score = struct.unpack_from("<i", buf, r)[0]; r += 4
    combo = struct.unpack_from("<H", buf, r)[0]; r += 2
    print("  rec@%d ver=%d player=%r rmd5=%s score=%d combo=%d counts=%s"
          % (p, ver, player, rmd5, score, combo, counts))
    print("      tail from %d: %s" % (r, buf[r:r + 24].hex(" ")))
    print("      next cand start: %s" % ([s for s in starts if s > p][:1]))
    for shift in range(5, 9):
        v = struct.unpack_from("<q", buf, r + shift)[0]
        print("        region_len=%d ticks@+%d -> %s" % (shift, shift, ticks_to_iso(v)))

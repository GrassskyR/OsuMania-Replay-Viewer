"""Probe #2 -- pin down the 6-byte region between maxCombo and the int64 date.

Anchored facts (probe #1):
  record start = +46 for group 0's first score
  byte mode, int32 version=20250309, string beatmapMd5, string player, string replayMd5,
  6x ushort counts, int32 score=743889, ushort maxCombo=401  -> ends at offset 146
  then 6 bytes of region (all zero in this record), then int64 (date candidate)
"""

import sys, os, struct, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import Reader, open_ro, ticks_to_iso

DB = r"D:\Game\osu\scores.db"
buf = open_ro(DB)

print("bytes 128..176 with offsets:")
for off in range(128, 176, 8):
    print("  %4d: %s" % (off, buf[off:off + 8].hex(" ")))

# --- candidate int64 windows starting at 146..155 for record0
print("\nint64 candidates starting after maxCombo (offset 146):")
for shift in range(0, 9):
    o = 146 + shift
    v = struct.unpack_from("<q", buf, o)[0]
    print("  +%d (off %d): raw=%d  iso=%s" % (shift, o, v, ticks_to_iso(v)))

# --- region bytes for the whole file, under the assumption record size is fixed
#     within one player-name length; instead, do a full greedy parse with a
#     configurable region size and check exact consumption.
MD5CHARS = set(b"0123456789abcdef")


def parse_score(r, region_len):
    """returns dict; region bytes stored raw; ticks read right after region."""
    mode = r.byte()
    ver = r.i32()
    bmd5 = r.legacy_string()
    player = r.legacy_string()
    rmd5 = r.legacy_string()
    counts = [r.u16() for _ in range(6)]
    score = r.i32()
    combo = r.u16()
    region = r.bytes(region_len)
    ticks = r.i64()
    return dict(mode=mode, ver=ver, bmd5=bmd5, player=player, rmd5=rmd5,
                counts=counts, score=score, combo=combo, region=region, ticks=ticks)


def full_parse(region_len, sanity):
    r = Reader(buf)
    ver = r.i32(); groups = r.i32()
    md5s = []
    total = 0
    bad = 0
    regions = collections.Counter()
    ticks_bad = 0
    while r.remaining > 0 and len(md5s) < groups:
        gmd5 = r.legacy_string()
        if gmd5 is None or len(gmd5) != 32:
            return None
        md5s.append(gmd5)
        n = r.i32()
        if n < 0 or n > 10000:
            return None
        for _ in range(n):
            s = parse_score(r, region_len)
            total += 1
            regions[s["region"]] += 1
            if not sanity(s, gmd5):
                bad += 1
    return dict(groups=len(md5s), total=total, remaining=r.remaining, bad=bad,
                regions=regions)


def sanity(s, gmd5):
    if s["mode"] > 3:
        return False
    if s["bmd5"] != gmd5:
        return False
    if s["rmd5"] is None or len(s["rmd5"]) != 32:
        return False
    if not (19900101 <= s["ver"] <= 29991231):
        return False
    return True


for rl in range(4, 10):
    res = full_parse(rl, sanity)
    if res is None:
        print("region_len=%d: parse aborted (bad structure)" % rl)
        continue
    # tick sanity: ticks must map to a year in [2000, 2100]
    r2 = Reader(buf); r2.i32(); g = r2.i32()
    good_ticks = 0; nticks = 0
    for _ in range(g):
        gmd5 = r2.legacy_string(); n = r2.i32()
        for _ in range(n):
            s = parse_score(r2, rl)
            nticks += 1
            v = s["ticks"]
            if 631139040000000000 <= v <= 662688000000000000:
                good_ticks += 1
    print("region_len=%d groups=%d scores=%d remaining=%d sanity_bad=%d ticks_plausible=%d/%d distinct_regions=%d"
          % (rl, res["groups"], res["total"], res["remaining"], res["bad"],
             good_ticks, nticks, len(res["regions"])))
    if res["remaining"] == 0 and res["bad"] == 0:
        print("   region value histogram (top 8):",
              [(bytes(k).hex(), v) for k, v in res["regions"].most_common(8)])

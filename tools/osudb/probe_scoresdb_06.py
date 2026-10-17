"""Probe #6 -- what are the last 8 bytes of a scores.db record?

Known: after `int64 dateTicks` (+ replayLength int32 == -1) there are 8 more bytes.
1408 / 5420 records have a non-zero value there; the values correlate with the date.
Test: is it a 32-bit truncation of the FILETIME (or of the .NET ticks)?
"""

import sys, os, collections, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import ticks_to_iso, open_ro
import scoresdb

DB = r"D:\Game\osu\scores.db"
OFS = 504911232000000000

db = scoresdb.parse(DB)
recs = [s for g in db.groups for s in g.scores]
nz = [s for s in recs if s.tail != 0]
print("records=%d tail!=0=%d" % (len(recs), len(nz)))

hits = collections.Counter()
for s in nz:
    t = s.tail
    ft = s.date_ticks - OFS
    for name, val in (
        ("dateTicks & 0xFFFFFFFF", s.date_ticks & 0xFFFFFFFF),
        ("FILETIME & 0xFFFFFFFF", ft & 0xFFFFFFFF),
        ("dateTicks >> 32", (s.date_ticks >> 32) & 0xFFFFFFFF),
        ("FILETIME >> 32", (ft >> 32) & 0xFFFFFFFF),
        ("dateTicks & 0x7FFFFFFF", s.date_ticks & 0x7FFFFFFF),
        ("(dateTicks-OFS)&0x7FFFFFFF", ft & 0x7FFFFFFF),
    ):
        if t == val:
            hits[name] += 1
print("matches:", dict(hits))

print("\nsample rows (date, date_ticks, tail, tail hex, FILETIME, FILETIME&0xFFFFFFFF):")
for s in nz[:10]:
    ft = s.date_ticks - OFS
    print("  %s ticks=%d tail=%d 0x%08x  ft&0xffffffff=0x%08x  ticks&0xffffffff=0x%08x"
          % (ticks_to_iso(s.date_ticks), s.date_ticks, s.tail, s.tail, ft & 0xFFFFFFFF,
             s.date_ticks & 0xFFFFFFFF))

# zero-tail distribution by year
print("\ntail==0 vs !=0 by year:")
byyear = collections.defaultdict(lambda: [0, 0])
for s in recs:
    y = ticks_to_iso(s.date_ticks)[:4]
    byyear[y][0 if s.tail == 0 else 1] += 1
for y in sorted(byyear):
    z, n = byyear[y]
    print("  %s  zero=%d nonzero=%d" % (y, z, n))

# Are the nonzero tails monotonic with date (i.e. a timestamp)?
pairs = sorted((s.date_ticks, s.tail) for s in nz)
mono = sum(1 for i in range(len(pairs) - 1) if pairs[i][1] <= pairs[i + 1][1])
print("\nmonotonic vs date: %d / %d" % (mono, len(pairs) - 1))

# distinct tail values
print("distinct tails: %d of %d" % (len(set(s.tail for s in nz)), len(nz)))

# maybe it is the low 32 bits of a *different* timestamp: file mtime of the .osr?
# compare with the .osr file's own size/mtime for a few
RD = r"D:\Game\osu\Data\r"
import datetime
print("\ntail vs .osr file mtime (first 6 nonzero tails):")
shown = 0
for s in nz:
    if shown >= 6:
        break
    name = None
    want = s.date_ticks - OFS
    pfx = s.beatmap_md5
    for n in os.listdir(RD) if False else []:
        pass
    # direct construction
    cand = os.path.join(RD, "%s-%d.osr" % (pfx, want))
    if os.path.exists(cand):
        st = os.stat(cand)
        mtime_ticks = int((st.st_mtime + 11644473600) * 10_000_000)
        print("  %s tail=%d (0x%x) mtime_ticks&0xffffffff=0x%08x  ctime=%d"
              % (os.path.basename(cand), s.tail, s.tail, mtime_ticks & 0xFFFFFFFF, st.st_ctime_ns))
        shown += 1

"""Probe #4 -- (a) what are the real Data\\r filename keys? (b) what is in the
last 8 bytes of a scores.db score record? (c) nail down perfect/mods positions.
"""

import sys, os, struct, collections, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import open_ro, ticks_to_iso
import scoresdb

DB = r"D:\Game\osu\scores.db"
RD = r"D:\Game\osu\Data\r"

files = os.listdir(RD)
prefixes = collections.Counter(n.split("-", 1)[0].lower() for n in files)
print("Data\\r files=%d distinct prefixes=%d" % (len(files), len(prefixes)))
print("files per prefix histogram:", collections.Counter(prefixes.values()))

db = scoresdb.parse(DB)
group_md5 = set(g.beatmap_md5 for g in db.groups)
rec_md5 = set(s.replay_md5 for g in db.groups for s in g.scores)
pkeys = set(prefixes)

print("\ngroup(beatmap) md5 in Data\\r prefixes : %d / %d" % (len(group_md5 & pkeys), len(group_md5)))
print("record replay_md5 in Data\\r prefixes   : %d / %d" % (len(rec_md5 & pkeys), len(rec_md5)))
print("group md5 NOT in prefixes              : %d" % len(group_md5 - pkeys))
print("prefixes not matching any group md5    : %d" % len(pkeys - group_md5))
print("prefixes not matching any record md5   : %d" % len(pkeys - rec_md5))

# For a group whose md5 IS a prefix, list its files
sample_group = next(iter(group_md5 & pkeys), None)
print("\nsample matching group md5:", sample_group)
if sample_group:
    print("  files:", [n for n in files if n.lower().startswith(sample_group)][:5])

# ---- tail analysis
tails = collections.Counter()
perf = collections.Counter()
mods = collections.Counter()
last8_nonzero = []
for g in db.groups:
    for s in g.scores:
        perf[s.perfect] += 1
        mods[s.mods] += 1
        if s.tail != 0:
            last8_nonzero.append(s)

print("\nperfect values:", dict(perf))
print("mods histogram (top 15):", mods.most_common(15))
print("records with tail != 0: %d" % len(last8_nonzero))
for s in last8_nonzero[:10]:
    print("   player=%r rmd5=%s date=%s tail=%d (0x%x) replay_len=%d score=%d"
          % (s.player, s.replay_md5, ticks_to_iso(s.date_ticks), s.tail, s.tail & 0xFFFFFFFFFFFFFFFF,
             s.replay_length, s.score))

# raw tail bytes of a few records (to search for a different field split)
def raw_tail(s):
    buf = open_ro(DB)
    return buf[s.offset:s.offset + 200]

print("\nraw byte windows for a few tail-nonzero records:")
buf = open_ro(DB)
shown = 0
for g in db.groups:
    for s in g.scores:
        if s.tail != 0 and shown < 3:
            shown += 1
            # find the record length by looking at the next record offset
            o = s.offset
            print("  rec@%d player=%r tail=%d" % (o, s.player, s.tail))

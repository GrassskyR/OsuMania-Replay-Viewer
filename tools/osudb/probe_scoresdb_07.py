"""Probe #7 -- is the trailing int64 of a scores.db record the same field as the
int64 that follows the LZMA payload in the matching .osr file?

.osr layout (lazer LegacyScoreDecoder):
    ... int64 date, int32 replayLength, <replayLength bytes of LZMA>, int64 onlineId
scores.db record layout hypothesis:
    ... int64 date, int32 replayLength(=-1), int64 onlineId
If both hold, the two int64s must be equal.
"""

import sys, os, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import open_ro, ticks_to_iso
import scoresdb

DB = r"D:\Game\osu\scores.db"
RD = r"D:\Game\osu\Data\r"
OFS = 504911232000000000

db = scoresdb.parse(DB)
recs = [s for g in db.groups for s in g.scores]
nz = [s for s in recs if s.tail != 0]
zero = [s for s in recs if s.tail == 0]
print("records=%d nonzero tail=%d zero tail=%d" % (len(recs), len(nz), len(zero)))

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import StreamReader


def osr_trailing_id(path):
    """Read the .osr header, skip the replay payload, return the trailing int64."""
    with open(path, "rb") as f:
        r = StreamReader(f)
        mode = r.byte(); ver = r.i32()
        bmd5 = r.legacy_string(); player = r.legacy_string(); rmd5 = r.legacy_string()
        for _ in range(6):
            r.u16()
        score = r.i32(); combo = r.u16(); perfect = r.bool(); mods = r.i32()
        hp = r.legacy_string()
        date = r.i64()
        rlen = r.i32()
        payload_start = f.tell()
        f.seek(rlen, 1)
        tail = struct.unpack("<q", f.read(8))[0]
        f.seek(0, 2)
        return dict(mode=mode, version=ver, bmd5=bmd5, player=player, rmd5=rmd5, score=score,
                    combo=combo, perfect=perfect, mods=mods, hp_len=len(hp) if hp else 0,
                    date=date, replay_len=rlen, payload_start=payload_start,
                    file_size=f.tell(), tail=tail)


def osr_path(s):
    return os.path.join(RD, "%s-%d.osr" % (s.beatmap_md5, s.date_ticks - OFS))


print("\n== nonzero-tail records: scores.db tail vs .osr trailing int64 ==")
ok = bad = missing = 0
for i, s in enumerate(nz[:25]):
    p = osr_path(s)
    if not os.path.exists(p):
        missing += 1
        print("  [%2d] MISSING %s" % (i, os.path.basename(p)))
        continue
    o = osr_trailing_id(p)
    same = (o["tail"] == s.tail)
    ok += same
    bad += (not same)
    print("  [%2d] %s  scores.db.tail=%-12d osr.trailing=%-12d same=%s (replay_len=%d file=%d)"
          % (i, os.path.basename(p)[:44], s.tail, o["tail"], same, o["replay_len"], o["file_size"]))
print("nonzero sample: same=%d diff=%d missing=%d" % (ok, bad, missing))

print("\n== zero-tail records: does the .osr also carry 0? ==")
ok = bad = 0
for i, s in enumerate(zero[:10]):
    p = osr_path(s)
    if not os.path.exists(p):
        print("  [%2d] MISSING" % i)
        continue
    o = osr_trailing_id(p)
    ok += (o["tail"] == 0)
    bad += (o["tail"] != 0)
    print("  [%2d] osr.trailing=%d  (replay_len=%d)" % (i, o["tail"], o["replay_len"]))
print("zero sample: same=%d diff=%d" % (ok, bad))

# full-field .osr comparison for 30 records
print("\n== full header comparison (30 records) ==")
mism = 0
checked = 0
for s in recs[:30]:
    p = osr_path(s)
    if not os.path.exists(p):
        continue
    o = osr_trailing_id(p)
    checked += 1
    diffs = []
    for k, v in (("mode", s.mode), ("version", s.version), ("bmd5", s.beatmap_md5),
                 ("player", s.player), ("rmd5", s.replay_md5), ("score", s.score),
                 ("combo", s.max_combo), ("perfect", s.perfect), ("mods", s.mods),
                 ("date", s.date_ticks)):
        if o[k] != v:
            diffs.append((k, o[k], v))
    if diffs:
        mism += 1
        print("  MISMATCH", os.path.basename(p), diffs)
print("checked=%d mismatch=%d" % (checked, mism))

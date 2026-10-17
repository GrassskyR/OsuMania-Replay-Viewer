"""Probe #5 -- establish the true scores.db record -> Data\\r file mapping.

Data\\r file names look like  <beatmapMd5>-<18-digit decimal>.osr
We test:
  * does the .osr header of files sharing the record's beatmap md5 match the record?
  * what is the 18-digit suffix (FILETIME?) relative to the record's date?
  * what is the scores.db 3rd string ("replay hash") vs the .osr header's 3rd string?
"""

import sys, os, struct, datetime, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import open_ro, ticks_to_iso
import scoresdb

DB = r"D:\Game\osu\scores.db"
RD = r"D:\Game\osu\Data\r"
FILETIME_OFS = 504911232000000000  # .NET ticks at 1601-01-01 -> FILETIME offset


def filetime_to_iso(ft):
    ticks = ft + FILETIME_OFS
    return ticks_to_iso(ticks)


def iso_to_filetime(s):
    return int(s) - FILETIME_OFS


db = scoresdb.parse(DB)

# index Data\r by prefix
by_prefix = collections.defaultdict(list)
for n in os.listdir(RD):
    if n.lower().endswith(".osr"):
        by_prefix[n.split("-", 1)[0].lower()].append(n)

print("== first 5 records: candidate .osr files and header comparison ==")
shown = 0
for g in db.groups:
    for s in g.scores:
        if shown >= 5:
            break
        cands = sorted(by_prefix.get(g.beatmap_md5, []))
        print("\nrecord: player=%r beatmap=%s score=%d combo=%d mods=%d counts=%s date=%s"
              % (s.player, g.beatmap_md5, s.score, s.max_combo, s.mods,
                 (s.count300, s.count100, s.count50, s.geki, s.katu, s.miss),
                 ticks_to_iso(s.date_ticks)))
        print("  scores.db replay-hash field = %s  tail=%d" % (s.replay_md5, s.tail))
        print("  candidate files for this beatmap: %d" % len(cands))
        for n in cands[:6]:
            suffix = n.split("-", 1)[1].split(".")[0]
            h = scoresdb.parse_osr_header(os.path.join(RD, n))
            match = (h["player"] == s.player and h["score"] == s.score and h["max_combo"] == s.max_combo
                     and h["mods"] == s.mods and h["count300"] == s.count300)
            print("    %s" % n)
            print("        suffix=%s -> as FILETIME %s" % (suffix, filetime_to_iso(int(suffix))))
            print("        osr: player=%r score=%d combo=%d mods=%d counts=%s date=%s replayhash=%s perfect=%s ver=%d mode=%d"
                  % (h["player"], h["score"], h["max_combo"], h["mods"],
                     (h["count300"], h["count100"], h["count50"], h["geki"], h["katu"], h["miss"]),
                     ticks_to_iso(h["date_ticks"]), h["replay_md5"], h["perfect"], h["version"], h["mode"]))
            print("        -> MATCHES RECORD: %s" % match)
        shown += 1

# ---- global test: for every record, does a file exist whose FILETIME suffix
#      equals the record date, and whose .osr header matches?
print("\n== global mapping test (records -> exact-suffix file) ==")
n_rec = n_exact = n_osr_match = n_prefix = 0
delta_hist = collections.Counter()
for g in db.groups:
    cands = by_prefix.get(g.beatmap_md5, [])
    for s in g.scores:
        n_rec += 1
        if not cands:
            continue
        n_prefix += 1
        want = s.date_ticks - FILETIME_OFS
        hit = None
        for n in cands:
            try:
                suffix = int(n.split("-", 1)[1].split(".")[0])
            except ValueError:
                continue
            if suffix == want:
                hit = n
                break
        if hit:
            n_exact += 1
            h = scoresdb.parse_osr_header(os.path.join(RD, hit))
            if (h["score"] == s.score and h["max_combo"] == s.max_combo and h["mods"] == s.mods
                    and h["count300"] == s.count300 and h["player"] == s.player):
                n_osr_match += 1
print("records                                   : %d" % n_rec)
print("records whose beatmap has >=1 Data\\r file : %d" % n_prefix)
print("records with an exact date==suffix file   : %d" % n_exact)
print("   of those, full .osr header field match : %d" % n_osr_match)

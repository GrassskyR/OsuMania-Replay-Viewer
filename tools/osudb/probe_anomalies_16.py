"""Probe #16 -- investigate the anomalies surfaced by validation:
  * osu!.db records whose Songs folder is missing on disk
  * records whose beatmapset_id != numeric prefix of the folder name
  * scores.db records with an empty/null player name
"""

import os, sys, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import beatmapdb
import scoresdb

db = beatmapdb.parse()
folders = {e.name: e.path for e in os.scandir(r"D:\Game\osu\Songs") if e.is_dir()}

missing = [b for b in db.beatmaps if b.folder_name not in folders]
print("beatmaps with folder missing on disk: %d" % len(missing))
for b in missing[:8]:
    print("   folder=%r osu=%r md5=%s set=%d id=%d status=%d"
          % (b.folder_name, b.osu_file, b.md5, b.beatmapset_id, b.beatmap_id, b.ranked_status))
print("   missing folder name histogram (how many are None/empty):",
      collections.Counter(("None" if b.folder_name is None else
                           ("empty" if b.folder_name == "" else "named")) for b in missing))

mismatch = []
for b in db.beatmaps:
    lead = (b.folder_name or "").split(" ")[0]
    if lead.isdigit() and int(lead) != b.beatmapset_id:
        mismatch.append(b)
print("\nset-id vs folder-prefix mismatch: %d" % len(mismatch))
for b in mismatch[:8]:
    print("   folder=%r  parsed set_id=%d beatmap_id=%d" % (b.folder_name, b.beatmapset_id, b.beatmap_id))

# what do mismatching folders look like? maybe suffix like "[no video]"
print("\nmismatch examples where folder prefix is NOT the set id (first 3 raw):")
for b in mismatch[:3]:
    print("   folder=%r" % b.folder_name)

# ------------------------------------------------------------------ scores.db
sdb = scoresdb.parse()
empty = [s for g in sdb.groups for s in g.scores if not s.player]
print("\nscores.db records with empty player: %d" % len(empty))
for s in empty[:8]:
    print("   bmd5=%s rmd5=%s score=%d date=%s mode=%d ver=%d online_id=%d"
          % (s.beatmap_md5, s.replay_md5, s.score,
             __import__("osu_ser").ticks_to_iso(s.date_ticks), s.mode, s.version, s.online_id))

# Do those empty-player records' .osr files also have an empty player?
RD = r"D:\Game\osu\Data\r"
ok = 0
for s in empty[:15]:
    p = os.path.join(RD, scoresdb.replay_file_name(s.beatmap_md5, s.date_ticks))
    if os.path.isfile(p):
        h = scoresdb.parse_osr_header(p)
        ok += (not h["player"])
        print("   osr player=%r score=%d (db score=%d) match=%s"
              % (h["player"], h["score"], s.score, h["score"] == s.score))
print("empty-player .osr files also empty:", ok)

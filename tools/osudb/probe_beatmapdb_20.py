"""Probe #20 -- verify two claims made in docs/BEATMAPDB_FORMAT.md:
  (a) star-rating block occupancy correlates with the beatmap's mode
  (b) every record whose folder prefix != beatmapSetId has beatmapSetId == -1
"""

import sys, os, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import beatmapdb

db = beatmapdb.parse()

# (a) which block indexes are non-empty, per mode
per_mode = collections.defaultdict(lambda: collections.Counter())
for b in db.beatmaps:
    nonempty = tuple(i for i, (_, pairs) in enumerate(b.star_ratings) if pairs)
    per_mode[b.mode][nonempty] += 1
for mode in sorted(per_mode):
    print("mode=%d (%s): non-empty block patterns -> %s"
          % (mode, beatmapdb.MODE_NAMES[mode], dict(per_mode[mode].most_common(5))))

# how many blocks are non-empty at all
pat = collections.Counter(tuple(i for i, (_, p) in enumerate(b.star_ratings) if p) for b in db.beatmaps)
print("\nALL non-empty block patterns:", dict(pat.most_common(10)))

# (b) folder-prefix mismatch vs beatmapSetId
mismatch = []
for b in db.beatmaps:
    lead = (b.folder_name or "").split(" ")[0]
    if lead.isdigit() and int(lead) != b.beatmapset_id:
        mismatch.append(b)
minus_one = sum(1 for b in mismatch if b.beatmapset_id == -1)
zero = sum(1 for b in mismatch if b.beatmapset_id == 0)
other = [b for b in mismatch if b.beatmapset_id not in (-1, 0)]
print("\nfolder-prefix != beatmapSetId: %d" % len(mismatch))
print("   of those, beatmapSetId == -1 : %d" % minus_one)
print("   of those, beatmapSetId == 0  : %d" % zero)
print("   of those, other              : %d" % len(other))
for b in other[:5]:
    print("      folder=%r set=%d" % (b.folder_name, b.beatmapset_id))

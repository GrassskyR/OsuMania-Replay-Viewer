"""Emit a small, committed JSON sample of both indexes so the C# implementation can
be diffed field-by-field against this Python reference.

Deliberately compact: the bulky per-record arrays (timing points, star-rating pairs)
are reduced to counts, because the *full* walk (including those arrays) is already
covered exhaustively by tools/osudb/speccheck.py.

Output: tools/osudb/index_sample.json
"""

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import ticks_to_iso  # noqa: E402
import beatmapdb
import scoresdb

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "index_sample.json")

BEATMAP_SAMPLE = 200
GROUP_SAMPLE = 10


def main():
    bdb = beatmapdb.parse()
    sdb = scoresdb.parse()

    beatmaps = []
    # spread the sample across the file rather than taking a prefix
    step = max(1, len(bdb.beatmaps) // BEATMAP_SAMPLE)
    for b in bdb.beatmaps[::step][:BEATMAP_SAMPLE]:
        beatmaps.append({
            "md5": b.md5,
            "folder_name": b.folder_name,
            "osu_file": b.osu_file,
            "beatmap_id": b.beatmap_id,
            "beatmapset_id": b.beatmapset_id,
            "mode": b.mode,
            "ranked_status": b.ranked_status,
            "hitcircles": b.hitcircles,
            "sliders": b.sliders,
            "spinners": b.spinners,
            "ar": b.ar, "cs": b.cs, "hp": b.hp, "od": b.od,
            "slider_velocity": b.slider_velocity,
            "drain_time_sec": b.drain_time_sec,
            "total_time_ms": b.total_time_ms,
            "preview_time_ms": b.preview_time_ms,
            "timing_point_count": len(b.timing_points),
            "star_rating_pair_count": sum(len(p) for _, p in b.star_ratings),
            "last_modified_utc": ticks_to_iso(b.last_modified),
            "unknown_tail": b.unknown_tail,
            "unknown_byte": b.unknown_byte,
        })

    groups = []
    gstep = max(1, len(sdb.groups) // GROUP_SAMPLE)
    for g in sdb.groups[::gstep][:GROUP_SAMPLE]:
        groups.append({
            "beatmap_md5": g.beatmap_md5,
            "scores": [{k: v for k, v in s.as_dict().items()} for s in g.scores],
        })

    payload = {
        "purpose": ("reference output for diffing the C# readers; NOT a shipped index. "
                    "The full index is rebuilt from scores.db / osu!.db at startup. "
                    "timing points and star-rating pairs are reduced to counts here "
                    "because tools/osudb/speccheck.py covers the complete walk."),
        "sources": {
            "beatmapdb": beatmapdb.DEFAULT_DB,
            "beatmapdb_version": bdb.version,
            "beatmapdb_records": len(bdb.beatmaps),
            "scoresdb": scoresdb.DEFAULT_DB,
            "scoresdb_version": sdb.version,
            "scoresdb_groups": len(sdb.groups),
            "scoresdb_records": sdb.total_scores,
        },
        "beatmap_sample_count": len(beatmaps),
        "beatmaps": beatmaps,
        "score_group_sample_count": len(groups),
        "score_groups": groups,
    }
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=1)
    print("wrote %s (%.1f KB): %d beatmaps, %d score groups"
          % (OUT, os.path.getsize(OUT) / 1024.0, len(beatmaps), len(groups)))


if __name__ == "__main__":
    main()

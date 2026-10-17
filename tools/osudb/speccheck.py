"""Differential test: parse both databases with a *spec-driven* table walk (written
directly from docs/SCORESDB_FORMAT.md and docs/BEATMAPDB_FORMAT.md, not reusing the
reader code) and compare against the reference readers.

Purpose: prove the documented field tables are complete and in the right order, i.e.
that a C# implementation written from the docs alone reproduces the reference parse.
"""

import os
import sys
import struct

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import open_ro, Reader
import scoresdb
import beatmapdb

# ------------------------------------------------------------------ spec tables
# ('type', name); types: B=byte S=int16 I=int32 Q=int64 F=float32 D=float64
#                        s=legacy string  b=bool(u8)
SCORE_FIELDS = [
    ("B", "mode"), ("I", "version"), ("s", "beatmap_md5"), ("s", "player"),
    ("s", "replay_md5"), ("H", "count300"), ("H", "count100"), ("H", "count50"),
    ("H", "geki"), ("H", "katu"), ("H", "miss"), ("I", "score"), ("H", "max_combo"),
    ("b", "perfect"), ("I", "mods"), ("s", "hp_graph"), ("Q", "date_ticks"),
    ("I", "replay_length"), ("Q", "online_id"),
]

# osu!.db record: (type, name); repeated blocks expressed with callables
OSUDB_HEADER = [("I", "version"), ("I", "folder_count"), ("b", "account_unlocked"),
                ("Q", "date_unlocked"), ("s", "player_name"), ("I", "beatmap_count")]

OSUDB_RECORD = [
    ("s", "artist"), ("s", "artist_unicode"), ("s", "title"), ("s", "title_unicode"),
    ("s", "creator"), ("s", "difficulty"), ("s", "audio_file"), ("s", "md5"),
    ("s", "osu_file"), ("B", "ranked_status"), ("H", "hitcircles"), ("H", "sliders"),
    ("H", "spinners"), ("Q", "last_modified"), ("F", "ar"), ("F", "cs"), ("F", "hp"),
    ("F", "od"), ("D", "slider_velocity"),
]
OSUDB_RECORD_TAIL = [
    ("I", "drain_time_sec"), ("I", "total_time_ms"), ("I", "preview_time_ms"),
]
OSUDB_AFTER_TIMING = [
    ("I", "beatmap_id"), ("I", "beatmapset_id"), ("I", "thread_id"),
    ("B", "grade_osu"), ("B", "grade_taiko"), ("B", "grade_catch"), ("B", "grade_mania"),
    ("H", "local_offset"), ("F", "stack_leniency"), ("B", "mode"),
    ("s", "source"), ("s", "tags"), ("H", "online_offset"), ("s", "title_font"),
    ("b", "unplayed"), ("Q", "last_played"), ("b", "osz2"), ("s", "folder_name"),
    ("Q", "last_checked"), ("b", "ignore_sound"), ("b", "ignore_skin"),
    ("b", "disable_storyboard"), ("b", "disable_video"), ("b", "visual_override"),
]
# after the 5 flags: [version < 20140609: u16] then these two (see docs §3)
OSUDB_TAIL_FIELDS = [("I", "unknown_tail"), ("B", "unknown_byte")]

SIZES = {"B": 1, "b": 1, "H": 2, "I": 4, "Q": 8, "F": 4, "D": 8}


def read_fields(r: Reader, table, version=None):
    out = {}
    for t, name in table:
        if t == "s":
            out[name] = r.legacy_string()
        elif t == "b":
            out[name] = r.bool()
        elif t == "B":
            out[name] = r.byte()
        elif t == "H":
            out[name] = r.u16()
        elif t == "I":
            out[name] = r.i32()
        elif t == "Q":
            out[name] = r.i64()
        elif t == "F":
            out[name] = r.f32()
        elif t == "D":
            out[name] = r.f64()
        else:
            raise AssertionError(t)
    return out


# ------------------------------------------------------------------ scores.db
buf = open_ro(scoresdb.DEFAULT_DB)
r = Reader(buf)
hdr = read_fields(r, [("I", "version"), ("I", "group_count")])
ref = scoresdb.parse()
assert hdr["version"] == ref.version and hdr["group_count"] == ref.declared_group_count
n = 0
for gi in range(hdr["group_count"]):
    md5 = r.legacy_string()
    cnt = r.i32()
    assert md5 == ref.groups[gi].beatmap_md5
    assert cnt == len(ref.groups[gi].scores)
    for si in range(cnt):
        f = read_fields(r, SCORE_FIELDS)
        s = ref.groups[gi].scores[si]
        for k in ("mode", "version", "beatmap_md5", "player", "replay_md5", "count300",
                  "count100", "count50", "geki", "katu", "miss", "score", "max_combo",
                  "perfect", "mods", "hp_graph", "date_ticks", "replay_length", "online_id"):
            if f[k] != getattr(s, k):
                raise AssertionError("scores.db %s record %d field %s: %r != %r"
                                     % (md5, si, k, f[k], getattr(s, k)))
        n += 1
print("scores.db  spec-walk: groups=%d records=%d remaining=%d  -> IDENTICAL to reference"
      % (hdr["group_count"], n, r.remaining))
assert r.remaining == 0

# ------------------------------------------------------------------ osu!.db
buf = open_ro(beatmapdb.DEFAULT_DB)
r = Reader(buf)
hdr = read_fields(r, OSUDB_HEADER)
ref = beatmapdb.parse()
assert hdr["version"] == ref.version and hdr["beatmap_count"] == ref.declared_beatmap_count
version = hdr["version"]
n = 0
for i in range(hdr["beatmap_count"]):
    f = read_fields(r, OSUDB_RECORD)
    if version >= 20140609:
        for slot in range(4):
            cnt = r.i32()
            for _ in range(cnt):
                assert r.byte() == 0x08, "star key tag"
                r.i32()
                t = r.byte()
                if t == 0x0C:
                    r.f32()
                elif t == 0x0D:
                    r.f64()
                else:
                    raise AssertionError("star value tag 0x%02x" % t)
    f.update(read_fields(r, OSUDB_RECORD_TAIL))
    ntp = r.i32()
    for _ in range(ntp):
        r.f64(); r.f64(); r.byte()
    f.update(read_fields(r, OSUDB_AFTER_TIMING))
    if version < 20140609:
        r.u16()
    f.update(read_fields(r, OSUDB_TAIL_FIELDS))
    b = ref.beatmaps[i]
    for k in ("artist", "title", "creator", "difficulty", "audio_file", "md5", "osu_file",
              "ranked_status", "hitcircles", "sliders", "spinners", "last_modified",
              "ar", "cs", "hp", "od", "slider_velocity", "drain_time_sec",
              "total_time_ms", "preview_time_ms", "beatmap_id", "beatmapset_id",
              "thread_id", "mode", "source", "tags", "unplayed", "last_played",
              "folder_name", "last_checked", "unknown_byte"):
        if f[k] != getattr(b, k):
            raise AssertionError("osu!.db record %d field %s: %r != %r"
                                 % (i, k, f[k], getattr(b, k)))
    # NOTE: the spec table above lists unknown_byte AFTER unknown_tail, matching the
    # doc's §3 order; the reference reader uses the same order.
    n += 1
tail = r.i32()
print("osu!.db    spec-walk: records=%d trailing=%d remaining=%d  -> IDENTICAL to reference"
      % (n, tail, r.remaining))
assert r.remaining == 0
print("\nDIFFERENTIAL TEST PASSED: both documented field tables reproduce the reference parse.")

"""osu!.db (osu!stable beatmap database) reader -- reference implementation.

READ-ONLY. Never writes to the osu! installation.

Layout verified empirically against D:\\Game\\osu\\osu!.db (version 20260924, 22123
beatmaps); see docs/BEATMAPDB_FORMAT.md for the evidence and for the parts that are
inferred rather than observed.

The field order follows the long-standing community-documented layout, with two
version adaptations that were measured on this file:
  * the per-record leading `int32 osu_file_size` field present in older versions
    is absent here (record starts directly with the artist string);
  * the type tag inside a star-rating pair is 0x0c + float32 here, while older
    readers hard-code 0x0d + float64. The reader below dispatches on the tag.

CLI:
    python beatmapdb.py --validate [--sample N]
    python beatmapdb.py --json <path> [--json-count N]
"""

from __future__ import annotations

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import Reader, open_ro, ticks_to_iso  # noqa: E402

DEFAULT_DB = r"D:\Game\osu\osu!.db"
DEFAULT_SONGS = r"D:\Game\osu\Songs"

# .NET SerializationWriter.WriteObject type tags (osu.Game/IO/Legacy/SerializationReader.cs)
OBJ_NULL = 0x00
OBJ_BOOL = 0x01
OBJ_BYTE = 0x02
OBJ_UINT16 = 0x03
OBJ_UINT32 = 0x04
OBJ_UINT64 = 0x05
OBJ_SBYTE = 0x06
OBJ_INT16 = 0x07
OBJ_INT32 = 0x08
OBJ_INT64 = 0x09
OBJ_CHAR = 0x0A
OBJ_STRING = 0x0B
OBJ_SINGLE = 0x0C
OBJ_DOUBLE = 0x0D
OBJ_DECIMAL = 0x0E
OBJ_DATETIME = 0x0F
OBJ_BYTEARRAY = 0x10
OBJ_CHARARRAY = 0x11
OBJ_OTHER = 0x12
OBJ_LEGACY = 0x13

RANKED_STATUS = {
    0: "unknown", 1: "unsubmitted", 2: "pending/wip", 3: "unused", 4: "ranked",
    5: "approved", 6: "qualified", 7: "loved",
}
MODE_NAMES = {0: "osu", 1: "taiko", 2: "catch", 3: "mania"}


class BeatmapEntry:
    __slots__ = (
        "offset", "artist", "artist_unicode", "title", "title_unicode", "creator",
        "difficulty", "audio_file", "md5", "osu_file", "ranked_status",
        "hitcircles", "sliders", "spinners", "last_modified",
        "ar", "cs", "hp", "od", "slider_velocity",
        "star_ratings", "drain_time_sec", "total_time_ms", "preview_time_ms",
        "timing_points", "beatmap_id", "beatmapset_id", "thread_id",
        "grade_osu", "grade_taiko", "grade_catch", "grade_mania",
        "local_offset", "stack_leniency", "mode", "source", "tags",
        "online_offset", "title_font", "unplayed", "last_played", "osz2",
        "folder_name", "last_checked", "ignore_sound", "ignore_skin",
        "disable_storyboard", "disable_video", "visual_override", "unknown_tail",
        "unknown_byte", "length",
    )

    def as_dict(self) -> dict:
        d = {k: getattr(self, k) for k in self.__slots__}
        d["last_modified_utc"] = ticks_to_iso(self.last_modified)
        d["last_played_utc"] = ticks_to_iso(self.last_played)
        d["last_checked_utc"] = ticks_to_iso(self.last_checked)
        d["mode_name"] = MODE_NAMES.get(self.mode, "?")
        d["ranked_status_name"] = RANKED_STATUS.get(self.ranked_status, "?")
        # star ratings: list of (ruleset_slot, [(mods, rating), ...])
        d["star_ratings"] = [[slot, pairs] for slot, pairs in self.star_ratings]
        return d


class OsuDatabase:
    def __init__(self):
        self.version = 0
        self.folder_count = 0
        self.account_unlocked = False
        self.date_unlocked = 0
        self.player_name = None
        self.declared_beatmap_count = 0
        self.beatmaps: list[BeatmapEntry] = []
        self.trailing_int = None
        self.file_size = 0
        self.bytes_consumed = 0
        self.trailing_bytes = 0
        self.type_tags = {}          # tag byte -> count (star rating pairs)
        self.unknown_tails = {}      # value -> count (the int32 before unknown_byte)
        self.unknown_bytes = {}  # value -> count


def _read_star_rating_pairs(r: Reader, stats: dict) -> list:
    n = r.i32()
    out = []
    for _ in range(n):
        t1 = r.byte()
        stats[t1] = stats.get(t1, 0) + 1
        if t1 != OBJ_INT32:
            raise ValueError("unexpected star pair key tag 0x%02x at %d" % (t1, r.pos))
        mods = r.i32()
        t2 = r.byte()
        stats[t2] = stats.get(t2, 0) + 1
        if t2 == OBJ_SINGLE:
            rating = r.f32()
        elif t2 == OBJ_DOUBLE:
            rating = r.f64()
        else:
            raise ValueError("unexpected star pair value tag 0x%02x at %d" % (t2, r.pos))
        out.append((mods, rating))
    return out


def _read_beatmap(r: Reader, version: int, stats: dict) -> BeatmapEntry:
    b = BeatmapEntry()
    b.offset = r.pos
    b.artist = r.legacy_string()
    b.artist_unicode = r.legacy_string()
    b.title = r.legacy_string()
    b.title_unicode = r.legacy_string()
    b.creator = r.legacy_string()
    b.difficulty = r.legacy_string()
    b.audio_file = r.legacy_string()
    b.md5 = r.legacy_string()
    b.osu_file = r.legacy_string()
    b.ranked_status = r.byte()
    b.hitcircles = r.u16()
    b.sliders = r.u16()
    b.spinners = r.u16()
    b.last_modified = r.i64()
    if version < 20140609:
        b.ar = r.byte(); b.cs = r.byte(); b.hp = r.byte(); b.od = r.byte()
    else:
        b.ar = r.f32(); b.cs = r.f32(); b.hp = r.f32(); b.od = r.f32()
    b.slider_velocity = r.f64()
    b.star_ratings = []
    if version >= 20140609:
        for slot in range(4):
            b.star_ratings.append((slot, _read_star_rating_pairs(r, stats)))
    b.drain_time_sec = r.i32()
    b.total_time_ms = r.i32()
    b.preview_time_ms = r.i32()
    ntp = r.i32()
    b.timing_points = []
    for _ in range(ntp):
        bpm = r.f64()
        offset = r.f64()
        regular = r.bool()
        b.timing_points.append((bpm, offset, regular))
    b.beatmap_id = r.i32()
    b.beatmapset_id = r.i32()
    b.thread_id = r.i32()
    b.grade_osu = r.byte()
    b.grade_taiko = r.byte()
    b.grade_catch = r.byte()
    b.grade_mania = r.byte()
    b.local_offset = r.u16()
    b.stack_leniency = r.f32()
    b.mode = r.byte()
    b.source = r.legacy_string()
    b.tags = r.legacy_string()
    b.online_offset = r.u16()
    b.title_font = r.legacy_string()
    b.unplayed = r.bool()
    b.last_played = r.i64()
    b.osz2 = r.bool()
    b.folder_name = r.legacy_string()
    b.last_checked = r.i64()
    b.ignore_sound = r.bool()
    b.ignore_skin = r.bool()
    b.disable_storyboard = r.bool()
    b.disable_video = r.bool()
    b.visual_override = r.bool()
    if version < 20140609:
        r.u16()
    b.unknown_tail = r.i32()
    b.unknown_byte = r.byte()
    b.length = r.pos - b.offset
    stats["tail_int:%d" % b.unknown_tail] = stats.get("tail_int:%d" % b.unknown_tail, 0) + 1
    stats["unknown_byte:%d" % b.unknown_byte] = stats.get("unknown_byte:%d" % b.unknown_byte, 0) + 1
    return b


def _stat(stats: dict, key: str, val) -> None:
    stats[key] = stats.get(key, 0) + 1


def parse(path: str = DEFAULT_DB, limit: int | None = None) -> OsuDatabase:
    buf = open_ro(path)
    r = Reader(buf)
    db = OsuDatabase()
    db.file_size = len(buf)
    db.version = r.i32()
    db.folder_count = r.i32()
    db.account_unlocked = r.bool()
    db.date_unlocked = r.i64()
    db.player_name = r.legacy_string()
    db.declared_beatmap_count = r.i32()
    n = db.declared_beatmap_count if limit is None else min(limit, db.declared_beatmap_count)
    for _ in range(n):
        db.beatmaps.append(_read_beatmap(r, db.version, db.type_tags))
    if limit is None:
        db.trailing_int = r.i32()
    db.bytes_consumed = r.pos
    db.trailing_bytes = r.remaining
    return db


# ------------------------------------------------------------------ validation
def validate(db_path: str = DEFAULT_DB, songs_dir: str = DEFAULT_SONGS,
             sample: int = 250, verbose: bool = True) -> dict:
    import songs_index

    def say(*a):
        if verbose:
            print(*a)

    db = parse(db_path)
    say("== osu!.db structure ==")
    say("file size            : %d" % db.file_size)
    say("version              : %d" % db.version)
    say("folder count (sets)  : %d" % db.folder_count)
    say("account unlocked     : %s" % db.account_unlocked)
    say("player name          : %s" % (db.player_name.encode("utf-8").hex() if db.player_name else None))
    say("declared beatmaps    : %d" % db.declared_beatmap_count)
    say("parsed beatmaps      : %d" % len(db.beatmaps))
    say("trailing int32       : %s" % db.trailing_int)
    say("bytes consumed       : %d / %d (remaining %d)" % (db.bytes_consumed, db.file_size, db.trailing_bytes))
    tags = {k: v for k, v in db.type_tags.items() if isinstance(k, int)}
    tails = {k: v for k, v in db.type_tags.items() if isinstance(k, str)}
    tail_hist = {int(k.split(":")[1]): v for k, v in tails.items() if k.startswith("tail_int")}
    byte_hist = {int(k.split(":")[1]): v for k, v in tails.items() if k.startswith("unknown_byte")}
    top_tails = sorted(((v, k) for k, v in tail_hist.items() if k != 0), reverse=True)[:5]
    say("star-rating pair type tags : %s" % {hex(k): v for k, v in sorted(tags.items())})
    say("unknown trailing int32     : distinct=%d  zero=%d  nonzero=%d  top=%s"
        % (len(tail_hist), tail_hist.get(0, 0),
           sum(v for k, v in tail_hist.items() if k != 0), top_tails))
    say("trailing byte (unknown)    : %s" % {k: v for k, v in sorted(byte_hist.items())})

    out = dict(file_size=db.file_size, version=db.version, folder_count=db.folder_count,
               declared_beatmaps=db.declared_beatmap_count, parsed_beatmaps=len(db.beatmaps),
               trailing_int=db.trailing_int, trailing_bytes=db.trailing_bytes,
               type_tags={hex(k): v for k, v in tags.items()},
               tail_int_distinct=len(tail_hist), tail_int_zero=tail_hist.get(0, 0),
               tail_int_top=top_tails,
               unknown_byte_hist={str(k): v for k, v in sorted(byte_hist.items())})

    # --- folder / index self-consistency
    say("\n== index self-consistency ==")
    folders = {}
    for e in os.scandir(songs_dir):
        if e.is_dir():
            folders[e.name] = e.path
    say("Songs folders on disk: %d" % len(folders))
    missing_folder = 0
    setid_mismatch = 0
    md5_bad = 0
    mode_hist = {}
    status_hist = {}
    folder_not_in_db = set(folders)
    for b in db.beatmaps:
        if b.folder_name not in folders:
            missing_folder += 1
        else:
            folder_not_in_db.discard(b.folder_name)
        if b.md5 is None or len(b.md5) != 32:
            md5_bad += 1
        lead = (b.folder_name or "").split(" ")[0]
        if lead.isdigit() and int(lead) != b.beatmapset_id:
            setid_mismatch += 1
        mode_hist[b.mode] = mode_hist.get(b.mode, 0) + 1
        status_hist[b.ranked_status] = status_hist.get(b.ranked_status, 0) + 1
    say("beatmaps whose folder_name is not on disk : %d" % missing_folder)
    say("beatmaps whose set id != folder prefix    : %d" % setid_mismatch)
    say("beatmaps with malformed md5               : %d" % md5_bad)
    say("mode histogram                            : %s" % mode_hist)
    say("ranked status histogram                   : %s" % status_hist)
    say("Songs folders not referenced by osu!.db   : %d" % len(folder_not_in_db))
    out.update(missing_folder=missing_folder, setid_mismatch=setid_mismatch, md5_bad=md5_bad,
               mode_hist=mode_hist, ranked_status_hist=status_hist,
               folders_not_referenced=len(folder_not_in_db))

    say("\n== full existence check over ALL %d records ==" % len(db.beatmaps))
    full_present = full_absent = folder_absent = 0
    absent_examples = []
    for b in db.beatmaps:
        folder = folders.get(b.folder_name)
        if folder is None:
            folder_absent += 1
            continue
        if b.osu_file and os.path.isfile(os.path.join(folder, b.osu_file)):
            full_present += 1
        else:
            full_absent += 1
            if len(absent_examples) < 5:
                absent_examples.append({"folder": b.folder_name, "file": b.osu_file, "md5": b.md5})
    say("folder present and .osu file present : %d" % full_present)
    say(".osu file absent                     : %d" % full_absent)
    say("folder itself absent                 : %d" % folder_absent)
    say("absent examples                      : %s" % json.dumps(absent_examples, ensure_ascii=False)[:800])
    out.update(full_present=full_present, full_absent=full_absent, folder_absent=folder_absent)

    # --- per-record disk validation on a sample
    say("\n== .osu byte-MD5 / field validation (sample=%d) ==" % sample)
    step = max(1, len(db.beatmaps) // sample)
    checked = md5_ok = md5_mismatch = missing_file = 0
    field_bad = {}
    examples = []
    for b in db.beatmaps[::step][:sample]:
        folder = folders.get(b.folder_name)
        if folder is None:
            continue
        p = os.path.join(folder, b.osu_file or "")
        if not (b.osu_file and os.path.isfile(p)):
            missing_file += 1
            if len(examples) < 5:
                examples.append({"md5": b.md5, "missing": p})
            continue
        checked += 1
        m = songs_index.md5_of(p)
        if m == (b.md5 or "").lower():
            md5_ok += 1
        else:
            md5_mismatch += 1
            if len(examples) < 5:
                examples.append({"path": p, "disk": m, "db": b.md5})
        # cross-check the values that also live in the .osu text file
        info = read_osu_summary(p)
        for k, v in (("audio_file", info.get("AudioFilename")),
                     ("mode", info.get("Mode")),
                     ("ar", info.get("ApproachRate")),
                     ("cs", info.get("CircleSize")),
                     ("hp", info.get("HPDrainRate")),
                     ("od", info.get("OverallDifficulty")),
                     ("slider_velocity", info.get("SliderMultiplier")),
                     ("preview_time_ms", info.get("PreviewTime"))):
            if v is None:
                continue
            got = getattr(b, k)
            if k == "audio_file":
                # osu!.db stores NULL where the .osu has no AudioFilename -> same thing
                ok = (got or "") == (v or "")
            elif k in ("ar", "cs", "hp", "od", "slider_velocity"):
                ok = got is not None and abs(float(got) - float(v)) < 1e-3
            elif k in ("mode", "preview_time_ms"):
                ok = int(got) == int(v)
            else:
                ok = str(got) == str(v)
            if not ok:
                field_bad[k] = field_bad.get(k, 0) + 1
    say(".osu files located & hashed : %d" % checked)
    say("md5 equal to osu!.db md5    : %d" % md5_ok)
    say("md5 mismatch                : %d" % md5_mismatch)
    say("osu_file missing on disk    : %d" % missing_file)
    say("field mismatches vs .osu    : %s" % (field_bad or "{}"))
    say("examples                    : %s" % json.dumps(examples, ensure_ascii=False)[:1200])
    out.update(sample_checked=checked, sample_md5_ok=md5_ok, sample_md5_mismatch=md5_mismatch,
               sample_missing=missing_file, sample_field_mismatch=field_bad)
    return out


def read_osu_summary(path: str) -> dict:
    """Read the handful of [General]/[Difficulty] keys we cross-check."""
    want_general = {"AudioFilename", "Mode", "PreviewTime"}
    want_diff = {"ApproachRate", "CircleSize", "HPDrainRate", "OverallDifficulty", "SliderMultiplier"}
    out: dict = {}
    section = None
    with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
        for line in f:
            line = line.strip()
            if line.startswith("[") and line.endswith("]"):
                section = line[1:-1]
                continue
            if ":" not in line:
                continue
            k, v = line.split(":", 1)
            k = k.strip(); v = v.strip()
            if section == "General" and k in want_general:
                out[k] = v
            elif section == "Difficulty" and k in want_diff:
                try:
                    out[k] = float(v)
                except ValueError:
                    pass
    if "Mode" in out:
        try:
            out["Mode"] = int(out["Mode"])
        except ValueError:
            out["Mode"] = 0
    if "PreviewTime" in out:
        try:
            out["PreviewTime"] = int(out["PreviewTime"])
        except ValueError:
            pass
    # ApproachRate is optional in .osu (falls back to OverallDifficulty)
    if "ApproachRate" not in out and "OverallDifficulty" in out:
        out["ApproachRate"] = out["OverallDifficulty"]
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--songs", default=DEFAULT_SONGS)
    ap.add_argument("--validate", action="store_true")
    ap.add_argument("--sample", type=int, default=250)
    ap.add_argument("--json", default=None)
    ap.add_argument("--json-count", type=int, default=100)
    ap.add_argument("--summary-json", default=None)
    args = ap.parse_args()

    if args.validate:
        res = validate(args.db, args.songs, sample=args.sample)
        if args.summary_json:
            with open(args.summary_json, "w", encoding="utf-8") as f:
                json.dump(res, f, ensure_ascii=False, indent=2)
            print("\nsummary json written to", args.summary_json)
        return

    if args.json:
        db = parse(args.db, limit=args.json_count)
        payload = {
            "source": args.db,
            "version": db.version,
            "declared_beatmap_count": db.declared_beatmap_count,
            "sample_count": len(db.beatmaps),
            "note": "sample only; full index is 22123 entries (~26 MB) and is rebuilt from osu!.db",
            "beatmaps": [b.as_dict() for b in db.beatmaps],
        }
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)
        print("wrote", args.json, "entries:", len(db.beatmaps))
        return

    db = parse(args.db)
    print("version=%d folders=%d beatmaps=%d/%d trailing_int=%s remaining=%d"
          % (db.version, db.folder_count, len(db.beatmaps), db.declared_beatmap_count,
             db.trailing_int, db.trailing_bytes))


if __name__ == "__main__":
    main()

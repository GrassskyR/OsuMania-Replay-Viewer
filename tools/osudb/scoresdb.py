"""scores.db (osu!stable local score database) reader -- reference implementation.

READ-ONLY. This module never writes to the osu! installation directory.

Layout (empirically verified on v20260924, 2690 groups / 5420 records; see
docs/SCORESDB_FORMAT.md for the evidence):

    int32   version                       file format version (YYYYMMDD)
    int32   beatmapGroupCount
    repeat beatmapGroupCount:
        legacy string  beatmapMd5
        int32          scoreCount
        repeat scoreCount:
            byte    mode                  0=osu 1=taiko 2=catch 3=mania
            int32   version               client build version (YYYYMMDD)
            legacy string beatmapMd5
            legacy string playerName
            legacy string replayMd5       replay data hash (== 3rd string in the .osr)
            ushort  count300
            ushort  count100
            ushort  count50
            ushort  countGeki
            ushort  countKatu
            ushort  countMiss
            int32   totalScore
            ushort  maxCombo
            bool    perfect
            int32   mods
            legacy string hpGraph         normally null (single 0x00 byte)
            int64   dateTicks             .NET DateTime ticks, UTC
            bytes   replay                int32 length; -1 == null (always -1 here)
            int64   onlineId              == the int64 after the LZMA payload in the .osr

'legacy string' == SerializationReader.ReadString(): leading marker byte
(0x00 => null, otherwise the value is ignored) followed by BinaryReader
.ReadString() (7-bit encoded byte length + UTF-8).

IMPORTANT -- how to find the replay FILE for a score:
    the file name is NOT the record's replayMd5. It is

        Data\\r\\<beatmapMd5>-<FILETIME>.osr

    where FILETIME = dateTicks - 504911232000000000 (i.e. the score date expressed
    as 100-ns ticks since 1601-01-01). Verified on 5420/5420 records.

CLI:
    python scoresdb.py --validate [--osr-sample N]
    python scoresdb.py --sample 3
    python scoresdb.py --json <path> [--json-groups N]
"""

from __future__ import annotations

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import Reader, open_ro, ticks_to_iso  # noqa: E402

DEFAULT_DB = r"D:\Game\osu\scores.db"
DEFAULT_REPLAY_DIR = r"D:\Game\osu\Data\r"

# .NET ticks at 1601-01-01 (FILETIME epoch) -- standard constant.
FILETIME_EPOCH_OFS = 504911232000000000

MODE_NAMES = {0: "osu", 1: "taiko", 2: "catch", 3: "mania"}

LEGACY_MOD_NAMES = {
    1 << 0: "NF", 1 << 1: "EZ", 1 << 2: "TD", 1 << 3: "HD", 1 << 4: "HR",
    1 << 5: "SD", 1 << 6: "DT", 1 << 7: "RX", 1 << 8: "HT", 1 << 9: "NC",
    1 << 10: "FL", 1 << 11: "AT", 1 << 12: "SO", 1 << 13: "AP", 1 << 14: "PF",
    1 << 15: "4K", 1 << 16: "5K", 1 << 17: "6K", 1 << 18: "7K", 1 << 19: "8K",
    1 << 20: "FI", 1 << 21: "RD", 1 << 22: "CN", 1 << 23: "TG", 1 << 24: "9K",
    1 << 25: "10K", 1 << 26: "1K", 1 << 27: "3K", 1 << 28: "2K", 1 << 29: "V2",
    1 << 30: "MR",
}


def mods_to_string(mods: int) -> str:
    if mods == 0:
        return "NM"
    out = []
    known = 0
    for bit in sorted(LEGACY_MOD_NAMES):
        known |= bit
        if mods & bit:
            out.append(LEGACY_MOD_NAMES[bit])
    if mods & ~known:
        out.append("UNKNOWN(0x%x)" % (mods & ~known))
    return "".join(out)


def replay_file_name(beatmap_md5: str, date_ticks: int) -> str:
    """Data\\r file name that osu!stable uses for this score."""
    return "%s-%d.osr" % (beatmap_md5, date_ticks - FILETIME_EPOCH_OFS)


class Score:
    __slots__ = ("mode", "version", "beatmap_md5", "player", "replay_md5",
                 "count300", "count100", "count50", "geki", "katu", "miss",
                 "score", "max_combo", "perfect", "mods", "hp_graph",
                 "date_ticks", "replay_length", "online_id", "offset")

    def as_dict(self) -> dict:
        return {
            "offset": self.offset,
            "mode": self.mode, "mode_name": MODE_NAMES.get(self.mode, "?"),
            "version": self.version,
            "beatmap_md5": self.beatmap_md5,
            "player": self.player,
            "replay_md5": self.replay_md5,
            "count300": self.count300, "count100": self.count100, "count50": self.count50,
            "geki": self.geki, "katu": self.katu, "miss": self.miss,
            "score": self.score, "max_combo": self.max_combo,
            "perfect": self.perfect, "mods": self.mods, "mods_string": mods_to_string(self.mods),
            "hp_graph": self.hp_graph,
            "date_ticks": self.date_ticks, "date_utc": ticks_to_iso(self.date_ticks),
            "replay_length": self.replay_length, "online_id": self.online_id,
            "replay_file": replay_file_name(self.beatmap_md5, self.date_ticks),
        }

    @property
    def total_hits(self) -> int:
        return self.count300 + self.count100 + self.count50 + self.geki + self.katu + self.miss


class BeatmapScores:
    __slots__ = ("beatmap_md5", "scores")

    def __init__(self, md5: str):
        self.beatmap_md5 = md5
        self.scores: list[Score] = []


class ScoresDatabase:
    def __init__(self):
        self.version = 0
        self.declared_group_count = 0
        self.groups: list[BeatmapScores] = []
        self.bytes_consumed = 0
        self.file_size = 0
        self.trailing_bytes = 0
        self.empty_groups = 0

    @property
    def total_scores(self) -> int:
        return sum(len(g.scores) for g in self.groups)


def _read_score(r: Reader) -> Score:
    s = Score()
    s.offset = r.pos
    s.mode = r.byte()
    s.version = r.i32()
    s.beatmap_md5 = r.legacy_string()
    s.player = r.legacy_string()
    s.replay_md5 = r.legacy_string()
    s.count300 = r.u16()
    s.count100 = r.u16()
    s.count50 = r.u16()
    s.geki = r.u16()
    s.katu = r.u16()
    s.miss = r.u16()
    s.score = r.i32()
    s.max_combo = r.u16()
    s.perfect = r.bool()
    s.mods = r.i32()
    s.hp_graph = r.legacy_string()
    s.date_ticks = r.i64()
    s.replay_length = r.i32()
    if s.replay_length > 0:
        r.bytes(s.replay_length)          # never observed in the wild
    s.online_id = r.i64()
    return s


def parse(path: str = DEFAULT_DB) -> ScoresDatabase:
    buf = open_ro(path)
    r = Reader(buf)
    db = ScoresDatabase()
    db.file_size = len(buf)
    db.version = r.i32()
    db.declared_group_count = r.i32()
    for _ in range(db.declared_group_count):
        if r.remaining <= 0:
            raise EOFError("file ended after %d/%d groups"
                           % (len(db.groups), db.declared_group_count))
        md5 = r.legacy_string()
        group = BeatmapScores(md5)
        n = r.i32()
        if n < 0:
            raise ValueError("negative score count %d for %s" % (n, md5))
        for _ in range(n):
            group.scores.append(_read_score(r))
        if n == 0:
            db.empty_groups += 1
        db.groups.append(group)
    db.bytes_consumed = r.pos
    db.trailing_bytes = r.remaining
    return db


# --------------------------------------------------------------- .osr header
def parse_osr_header(path: str) -> dict:
    """Parse only the .osr header (no LZMA decompression). Read-only.

    osu.Game/Scoring/Legacy/LegacyScoreDecoder.cs:
        byte   mode ; int32 version ; string beatmapMd5 ; string playerName ;
        string replayMd5 ; ushort x6 ; int32 totalScore ; ushort maxCombo ;
        bool perfect ; int32 mods ; string hpGraph ; int64 dateTicks ;
        bytes replay (int32 length + LZMA) ; [version>=20140721] int64 onlineId
    """
    from osu_ser import StreamReader
    with open(path, "rb") as f:
        r = StreamReader(f)
        out = {
            "mode": r.byte(), "version": r.i32(),
            "beatmap_md5": r.legacy_string(), "player": r.legacy_string(),
            "replay_md5": r.legacy_string(),
        }
        out["count300"] = r.u16(); out["count100"] = r.u16(); out["count50"] = r.u16()
        out["geki"] = r.u16(); out["katu"] = r.u16(); out["miss"] = r.u16()
        out["score"] = r.i32(); out["max_combo"] = r.u16()
        out["perfect"] = r.bool(); out["mods"] = r.i32()
        hp = r.legacy_string()
        out["hp_graph_length"] = len(hp) if hp else 0
        out["date_ticks"] = r.i64()
        out["replay_length"] = r.i32()
        payload_start = f.tell()
        f.seek(0, 2)
        out["file_size"] = f.tell()
        out["online_id"] = out_online_id(f, out["replay_length"], payload_start)
    return out


def out_online_id(f, replay_length: int, payload_start: int):
    if replay_length < 0:
        return None
    f.seek(payload_start + replay_length)
    b = f.read(8)
    if len(b) != 8:
        return None
    import struct
    return struct.unpack("<q", b)[0]


# ---------------------------------------------------------------- validation
def validate(db_path: str = DEFAULT_DB, replay_dir: str = DEFAULT_REPLAY_DIR,
             osr_sample: int = 300, verbose: bool = True) -> dict:
    out: dict = {}

    def say(*a):
        if verbose:
            print(*a)

    db = parse(db_path)
    say("== scores.db structure ==")
    say("file                : %s" % db_path)
    say("file size           : %d bytes" % db.file_size)
    say("version             : %d" % db.version)
    say("declared groups     : %d" % db.declared_group_count)
    say("parsed groups       : %d" % len(db.groups))
    say("total score records : %d" % db.total_scores)
    say("empty groups        : %d" % db.empty_groups)
    say("bytes consumed      : %d / %d  (remaining %d)"
        % (db.bytes_consumed, db.file_size, db.trailing_bytes))
    out.update(file_size=db.file_size, version=db.version,
               declared_groups=db.declared_group_count, parsed_groups=len(db.groups),
               total_scores=db.total_scores, empty_groups=db.empty_groups,
               trailing_bytes=db.trailing_bytes)

    # ---- internal consistency
    say("\n== internal consistency (all %d records) ==" % db.total_scores)
    bad = {k: 0 for k in ("inner_md5", "replay_md5", "mode", "version", "date",
                          "hp_graph", "replay_len", "player")}
    dup_groups = 0
    seen = set()
    versions = set()
    modes: dict = {}
    online_nonzero = 0
    for g in db.groups:
        if g.beatmap_md5 in seen:
            dup_groups += 1
        seen.add(g.beatmap_md5)
        if not g.beatmap_md5 or len(g.beatmap_md5) != 32:
            bad["inner_md5"] += 1
        for s in g.scores:
            if s.beatmap_md5 != g.beatmap_md5:
                bad["inner_md5"] += 1
            if not s.replay_md5 or len(s.replay_md5) != 32:
                bad["replay_md5"] += 1
            if s.mode not in MODE_NAMES:
                bad["mode"] += 1
            if not (19900101 <= s.version <= 29991231):
                bad["version"] += 1
            if not (621355968000000000 <= s.date_ticks <= 660000000000000000):
                bad["date"] += 1
            if s.hp_graph is not None:
                bad["hp_graph"] += 1
            if s.replay_length != -1:
                bad["replay_len"] += 1
            if s.player is None or s.player == "":
                bad["player"] += 1
            if s.online_id != 0:
                online_nonzero += 1
            versions.add(s.version)
            modes[s.mode] = modes.get(s.mode, 0) + 1
    say("duplicate group md5               : %d" % dup_groups)
    say("record-version range              : %d .. %d" % (min(versions), max(versions)))
    say("mode histogram                    : %s" % dict(sorted(modes.items())))
    say("records with onlineId != 0        : %d" % online_nonzero)
    for k, v in bad.items():
        say("records failing %-12s check    : %d" % (k, v))
    out.update(dup_groups=dup_groups, checks_failed=bad,
               version_min=min(versions), version_max=max(versions),
               mode_hist=dict(sorted(modes.items())), online_id_nonzero=online_nonzero,
               distinct_player_count=len({s.player for g in db.groups for s in g.scores}))

    # ---- replay file existence (exact name derived from beatmapMd5 + date)
    say("\n== replay file resolution (Data\\r) ==")
    files = set()
    with os.scandir(replay_dir) as it:
        for e in it:
            if e.is_file():
                files.add(e.name)
    say("files in Data\\r                    : %d" % len(files))
    distinct_prefix = {n.split("-", 1)[0].lower() for n in files}
    group_md5 = {g.beatmap_md5 for g in db.groups}
    say("distinct Data\\r name prefixes      : %d" % len(distinct_prefix))
    say("group md5 present as a name prefix : %d / %d" % (len(group_md5 & distinct_prefix), len(group_md5)))
    hits = 0
    miss = []
    total = 0
    for g in db.groups:
        for s in g.scores:
            total += 1
            if replay_file_name(g.beatmap_md5, s.date_ticks) in files:
                hits += 1
            elif len(miss) < 5:
                miss.append(replay_file_name(g.beatmap_md5, s.date_ticks))
    say("records with the exact expected .osr: %d / %d (%.4f%%)"
        % (hits, total, 100.0 * hits / max(1, total)))
    say("missing examples                    : %s" % miss)
    say("Data\\r prefixes with no scores.db group: %d" % len(distinct_prefix - group_md5))
    out.update(replay_files=len(files), prefixes=len(distinct_prefix),
               group_md5_with_prefix=len(group_md5 & distinct_prefix),
               records_resolved=hits, records_total=total,
               resolve_rate=round(100.0 * hits / max(1, total), 4),
               prefixes_without_group=len(distinct_prefix - group_md5))

    # ---- .osr header cross-check (a sample; every field must agree)
    say("\n== .osr header cross-check (sample=%d) ==" % osr_sample)
    checked = mismatch = missing = 0
    field_mismatch: dict = {}
    expected_diff: dict = {}
    examples = []
    for g in db.groups:
        for s in g.scores:
            if checked + missing >= osr_sample:
                break
            name = replay_file_name(g.beatmap_md5, s.date_ticks)
            p = os.path.join(replay_dir, name)
            if name not in files:
                missing += 1
                continue
            h = parse_osr_header(p)
            checked += 1
            diffs = []
            for k, v in (("mode", s.mode), ("version", s.version),
                         ("beatmap_md5", s.beatmap_md5), ("player", s.player),
                         ("replay_md5", s.replay_md5),
                         ("count300", s.count300), ("count100", s.count100),
                         ("count50", s.count50), ("geki", s.geki), ("katu", s.katu),
                         ("miss", s.miss), ("score", s.score), ("max_combo", s.max_combo),
                         ("perfect", s.perfect), ("mods", s.mods),
                         ("date_ticks", s.date_ticks), ("online_id", s.online_id)):
                if h.get(k) != v:
                    diffs.append((k, h.get(k), v))
                    field_mismatch[k] = field_mismatch.get(k, 0) + 1
            # replay_length differs BY DESIGN: scores.db stores -1 (payload not kept),
            # the .osr stores the real payload length. Counted separately.
            if h.get("replay_length") != s.replay_length:
                expected_diff["replay_length"] = expected_diff.get("replay_length", 0) + 1
                if not (s.replay_length == -1 and h.get("replay_length", 0) > 0):
                    diffs.append(("replay_length", h.get("replay_length"), s.replay_length))
                    field_mismatch["replay_length_unexpected"] = \
                        field_mismatch.get("replay_length_unexpected", 0) + 1
            if diffs:
                mismatch += 1
                if len(examples) < 5:
                    examples.append({"osr": name, "diffs": diffs})
        if checked + missing >= osr_sample:
            break
    say(".osr files compared                : %d (missing %d)" % (checked, missing))
    say("records with any field mismatch     : %d" % mismatch)
    say("mismatch count per field            : %s" % (field_mismatch or "{}"))
    say("by-design differences               : %s (scores.db stores Replay=-1)"
        % (expected_diff or "{}"))
    say("examples                            : %s" % json.dumps(examples, ensure_ascii=False)[:1500])
    out.update(osr_compared=checked, osr_missing=missing, osr_mismatch=mismatch,
               osr_field_mismatch=field_mismatch, osr_by_design=expected_diff,
               osr_examples=examples)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--replay-dir", default=DEFAULT_REPLAY_DIR)
    ap.add_argument("--validate", action="store_true")
    ap.add_argument("--sample", type=int, default=0)
    ap.add_argument("--json", default=None)
    ap.add_argument("--json-groups", type=int, default=25)
    ap.add_argument("--osr-sample", type=int, default=300)
    args = ap.parse_args()

    if args.validate:
        res = validate(args.db, args.replay_dir, osr_sample=args.osr_sample)
        if args.json:
            with open(args.json, "w", encoding="utf-8") as f:
                json.dump(res, f, ensure_ascii=False, indent=2)
            print("\nsummary json written to", args.json)
        return

    if args.json:
        db = parse(args.db)
        payload = {
            "source": args.db, "version": db.version,
            "declared_group_count": db.declared_group_count,
            "parsed_group_count": len(db.groups),
            "total_scores": db.total_scores,
            "sample_group_count": args.json_groups,
            "note": "sample only; the full index is rebuilt from scores.db",
            "groups": [{"beatmap_md5": g.beatmap_md5, "scores": [s.as_dict() for s in g.scores]}
                       for g in db.groups[:args.json_groups]],
        }
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)
        print("wrote", args.json)
        return

    db = parse(args.db)
    print("version=%d groups=%d/%d scores=%d remaining=%d"
          % (db.version, len(db.groups), db.declared_group_count,
             db.total_scores, db.trailing_bytes))
    for g in db.groups[:args.sample]:
        print("beatmap %s (%d scores)" % (g.beatmap_md5, len(g.scores)))
        for s in g.scores:
            print("   ", json.dumps(s.as_dict(), ensure_ascii=False))


if __name__ == "__main__":
    main()

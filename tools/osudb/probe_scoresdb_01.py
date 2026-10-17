"""Probe #1 -- discover the exact scores.db record layout.

Read-only against D:\\Game\\osu\\scores.db (never writes there).

Strategy: hypothesise the fixed part of a score record, then verify by
  (a) the next record starting with a plausible `mode` byte + version int32 + 0x0b string,
  (b) the per-score beatmap-md5 string equalling the group key,
  (c) the whole file consuming exactly (0 bytes remaining) for all groups.
"""

import sys
import os
import json

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import Reader, open_ro, ticks_to_iso  # noqa: E402

DB = r"D:\Game\osu\scores.db"


def dump_head(n=200):
    b = open_ro(DB)[:n]
    return b


def parse_score(r, trailer_ints, trailer_bytes=0):
    """One scores.db score record. Returns dict. Caller guarantees pos reset on failure."""
    mode = r.byte()
    version = r.i32()
    beatmap_md5 = r.legacy_string()
    player = r.legacy_string()
    replay_md5 = r.legacy_string()
    c300 = r.u16()
    c100 = r.u16()
    c50 = r.u16()
    cgeki = r.u16()
    ckatu = r.u16()
    cmiss = r.u16()
    score = r.i32()
    max_combo = r.u16()
    perfect = r.bool()
    mods = r.i32()
    ticks = r.i64()
    extra = []
    for _ in range(trailer_ints):
        extra.append(r.i32())
    tbytes = r.bytes(trailer_bytes) if trailer_bytes else b""
    return {
        "mode": mode, "version": version, "beatmap_md5": beatmap_md5,
        "player": player, "replay_md5": replay_md5,
        "count300": c300, "count100": c100, "count50": c50,
        "geki": cgeki, "katu": ckatu, "miss": cmiss,
        "score": score, "max_combo": max_combo, "perfect": perfect,
        "mods": mods, "ticks": ticks, "extra": extra, "tbytes": tbytes,
    }


def looks_like_record_start(r, pos):
    """Heuristic: byte in 0..3, plausible version int32, then a legacy string marker."""
    if pos + 6 > r.len:
        return False
    mode = r.buf[pos]
    if mode > 3:
        return False
    import struct
    ver = struct.unpack_from("<i", r.buf, pos + 1)[0]
    if not (19900101 <= ver <= 29991231 or ver == 0 or 30000001 <= ver <= 39999999):
        return False
    if r.buf[pos + 5] not in (0x00, 0x0B):
        return False
    return True


def looks_like_group_start(r, pos):
    if pos + 2 > r.len:
        return False
    return r.buf[pos] == 0x0B


def main():
    buf = open_ro(DB)
    print("file size:", len(buf))
    r = Reader(buf)
    version = r.i32()
    group_count = r.i32()
    print("version=%d group_count=%d" % (version, group_count))
    print("head hex:", buf[:160].hex(" "))

    # --- experiment: first group, first score, brute forward trailer sizes
    print("\n== trying trailer hypotheses on group 0 ==")
    for trailer_ints in range(0, 4):
        for trailer_bytes in (0, 1, 2):
            rr = Reader(buf)
            rr.i32(); rr.i32()
            gmd5 = rr.legacy_string()
            n = rr.i32()
            p0 = rr.pos
            try:
                s0 = parse_score(rr, trailer_ints, trailer_bytes)
            except (EOFError, ValueError) as e:
                print("  trailer(ints=%d,bytes=%d): parse error %s" % (trailer_ints, trailer_bytes, e))
                continue
            end0 = rr.pos
            ok_next = looks_like_record_start(buf and rr, end0)
            print("  trailer(ints=%d,bytes=%d): rec0 len=%d end=%d next_ok=%s" %
                  (trailer_ints, trailer_bytes, end0 - p0, end0, ok_next))
            if ok_next:
                print("     rec0:", json.dumps({k: v for k, v in s0.items() if k != 'tbytes'},
                                               ensure_ascii=False))
                print("     ticks iso:", ticks_to_iso(s0["ticks"]))
                # try to consume remaining scores of the group and check md5 equality
                try:
                    for i in range(1, n):
                        s = parse_score(rr, trailer_ints, trailer_bytes)
                        if s["beatmap_md5"] != gmd5:
                            print("     score%d beatmap_md5 mismatch: %r" % (i, s["beatmap_md5"]))
                            break
                    else:
                        print("     all %d scores of group0 parsed; group md5 reproduced each time" % n)
                        print("     pos after group0 =", rr.pos)
                        print("     next group starts with 0x0b:", buf[rr.pos] == 0x0B)
                except (EOFError, ValueError) as e:
                    print("     later score parse failed:", e)


if __name__ == "__main__":
    main()

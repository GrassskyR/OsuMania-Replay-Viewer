"""Probe #11 -- walk a full osu!.db beatmap record and compare with the real .osu file."""

import sys, os, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import open_ro, Reader
import songs_index

DB = r"D:\Game\osu\osu!.db"
buf = open_ro(DB)

SONGS = songs_index.build()
print("songs index: %d distinct .osu names, %d files"
      % (len(SONGS), sum(len(v) for v in SONGS.values())))

r = Reader(buf)
version = r.i32(); folders = r.i32(); unlocked = r.byte(); unlock_ticks = r.i64()
player = r.legacy_string(); nbm = r.i32()
print("header: version=%d folders=%d unlocked=%d unlock=0x%x player=%r beatmaps=%d pos=%d"
      % (version, folders, unlocked, unlock_ticks, player, nbm, r.pos))

STR_FIELDS = ["artist", "artist_unicode", "title", "title_unicode", "creator",
              "difficulty", "audio_file", "md5", "osu_file"]
rec = {"offset": r.pos}
for name in STR_FIELDS:
    off = r.pos
    rec[name] = r.legacy_string()
    print("  %-14s @%-7d = %r" % (name, off, rec[name]))

off = r.pos
rec["ranked_status"] = r.byte()
print("  %-14s @%-7d = %d" % ("ranked_status", off, rec["ranked_status"]))
off = r.pos
rec["hitcircles"] = r.u16(); rec["sliders"] = r.u16(); rec["spinners"] = r.u16()
print("  counts         @%-7d = circles=%d sliders=%d spinners=%d"
      % (off, rec["hitcircles"], rec["sliders"], rec["spinners"]))
off = r.pos
rec["last_modified"] = r.i64()
print("  last_modified  @%-7d = %d" % (off, rec["last_modified"]))
off = r.pos
rec["ar"] = r.f32(); rec["cs"] = r.f32(); rec["hp"] = r.f32(); rec["od"] = r.f32()
print("  AR/CS/HP/OD    @%-7d = %s %s %s %s" % (off, rec["ar"], rec["cs"], rec["hp"], rec["od"]))
off = r.pos
rec["slider_velocity"] = r.f64()
print("  slider_vel     @%-7d = %s" % (off, rec["slider_velocity"]))

# --- star ratings section
off = r.pos
nsr = r.i32()
print("  star_count     @%-7d = %d" % (off, nsr))
rec["star_entries"] = []
for i in range(nsr):
    if r.remaining <= 0:
        print("     star entry %d: EOF" % i)
        break
    e = {}
    e["mode"] = r.byte()
    e["mods"] = r.i32()
    e["rating"] = r.f64()
    e["mod_count"] = r.byte()
    e["mods_extra"] = [r.i32() for _ in range(e["mod_count"])]
    rec["star_entries"].append(e)
    print("     star[%d] mode=%d mods=%d rating=%.6f mod_count=%d extra=%s"
          % (i, e["mode"], e["mods"], e["rating"], e["mod_count"], e["mods_extra"]))

# --- remaining fields, as the canonical layout suggests
off = r.pos
print("\n  remaining bytes from %d: %s" % (off, buf[off:off + 64].hex(" ")))
for name, fmt in (("drain_time", "<i"), ("total_time", "<i"), ("preview_time", "<i")):
    o = r.pos
    v = struct.unpack_from(fmt, buf, r.pos)[0]; r.pos += 4
    rec[name] = v
    print("  %-14s @%-7d = %d" % (name, o, v))

off = r.pos
ntp = r.i32()
rec["timing_point_count"] = ntp
print("  timing_points  @%-7d = %d" % (off, ntp))
tps = []
for i in range(ntp):
    o = r.pos
    bpm = r.f64(); offset_ms = r.f64(); tsig = r.i32()
    tps.append((bpm, offset_ms, tsig))
rec["timing_points"] = tps
print("     first timing points: %s" % (tps[:3],))
off = r.pos
rec["beatmap_id"] = r.i32(); rec["beatmapset_id"] = r.i32(); rec["thread_id"] = r.i32()
print("  ids            @%-7d = beatmap_id=%d set_id=%d thread_id=%d"
      % (off, rec["beatmap_id"], rec["beatmapset_id"], rec["thread_id"]))
off = r.pos
print("  next 32 bytes  @%-7d = %s" % (off, buf[off:off + 32].hex(" ")))

# --- validate against disk
name = (rec["osu_file"] or "").lower()
paths = SONGS.get(name, [])
print("\n== disk validation ==")
print("osu_file=%r -> %d match(es)" % (rec["osu_file"], len(paths)))
if paths:
    p = paths[0]
    print("   path   :", p)
    m = songs_index.md5_of(p)
    print("   md5 on disk: %s   parsed md5: %s   equal=%s" % (m, rec["md5"], m == rec["md5"].lower()))
    with open(p, "r", encoding="utf-8-sig", errors="replace") as f:
        for line in f:
            if line.startswith(("Mode:", "CircleSize:", "ApproachRate:", "OverallDifficulty:",
                                "HPDrainRate:", "SliderMultiplier:", "AudioFilename:")):
                print("   .osu", line.rstrip())
        f.seek(0)

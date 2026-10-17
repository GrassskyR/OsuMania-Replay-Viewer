"""Probe #13 -- split osu!.db into records using an independent anchor:
each beatmap record ENDS with the legacy string holding its Songs folder name.

Then dump the region between the fixed numeric header and that folder-name string
for several records, to derive the star-rating / timing-point section.
"""

import sys, os, struct, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osu_ser import open_ro, Reader

DB = r"D:\Game\osu\osu!.db"
SONGS = r"D:\Game\osu\Songs"
buf = open_ro(DB)

folders = set()
for e in os.scandir(SONGS):
    if e.is_dir():
        folders.add(e.name)
print("Songs folders: %d" % len(folders))
folder_bytes = set(f.encode("utf-8") for f in folders)

r = Reader(buf)
version = r.i32(); nfolders = r.i32(); unlocked = r.byte(); unlock = r.i64()
player = r.legacy_string(); nbm = r.i32()
print("version=%d folders=%d beatmaps=%d" % (version, nfolders, nbm))

STR_FIELDS = ["artist", "artist_unicode", "title", "title_unicode", "creator",
              "difficulty", "audio_file", "md5", "osu_file"]


def read_record(p):
    """Parse one record; returns (dict, end_offset) where end is after the folder string."""
    r = Reader(buf, p)
    rec = {"offset": p}
    for name in STR_FIELDS:
        rec[name] = r.legacy_string()
    rec["ranked_status"] = r.byte()
    rec["hitcircles"] = r.u16(); rec["sliders"] = r.u16(); rec["spinners"] = r.u16()
    rec["last_modified"] = r.i64()
    rec["ar"] = r.f32(); rec["cs"] = r.f32(); rec["hp"] = r.f32(); rec["od"] = r.f32()
    rec["sv"] = r.f64()
    rec["num_section_start"] = r.pos
    # find the folder-name string: scan for 0x0b + sane length + exact folder bytes
    q = r.pos
    while q < len(buf) - 3:
        if buf[q] == 0x0B:
            ln = buf[q + 1]
            if ln and ln < 0x80:
                s = buf[q + 2:q + 2 + ln]
                if s in folder_bytes:
                    rec["folder"] = s.decode("utf-8")
                    rec["folder_offset"] = q
                    rec["num_section_len"] = q - r.pos
                    return rec, q + 2 + ln
        q += 1
    raise ValueError("no folder string after %d" % p)


recs = []
p = 32
for i in range(6):
    rec, p = read_record(p)
    recs.append(rec)
    print("\nrecord %d @%d  folder=%r" % (i, rec["offset"], rec.get("folder")))
    print("   md5=%s osu_file=%r" % (rec["md5"], rec["osu_file"]))
    print("   status=%d counts=(%d,%d,%d) last_mod=%d ar=%s cs=%s hp=%s od=%s sv=%s"
          % (rec["ranked_status"], rec["hitcircles"], rec["sliders"], rec["spinners"],
             rec["last_modified"], rec["ar"], rec["cs"], rec["hp"], rec["od"], rec["sv"]))
    print("   numeric section @%d len=%d" % (rec["num_section_start"], rec["num_section_len"]))
    s0 = rec["num_section_start"]
    s1 = rec["folder_offset"]
    for o in range(s0, s1, 16):
        print("     %6d: %s" % (o, buf[o:min(o + 16, s1)].hex(" ")))

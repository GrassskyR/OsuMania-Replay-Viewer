"""Probe #17 -- string-encoding facts for osu!.db (important for the C# reader).

Writes a UTF-8 report to tools/osudb/out/encoding_report.txt because this console
is GBK and would mangle the output.
"""

import os, sys, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import beatmapdb

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out", "encoding_report_17.txt")
os.makedirs(os.path.dirname(OUT), exist_ok=True)

db = beatmapdb.parse()

lines = []
lines.append("osu!.db header player_name (raw utf-8 bytes) = %s"
             % (db.player_name.encode("utf-8").hex() if db.player_name else None))
lines.append("osu!.db header player_name (decoded)         = %r" % db.player_name)

bs = sum(1 for b in db.beatmaps if b.folder_name and "\\" in b.folder_name)
lines.append("folder_name values containing a backslash   = %d" % bs)
for b in db.beatmaps:
    if b.folder_name and "\\" in b.folder_name:
        lines.append("   raw utf-8 bytes = %s" % b.folder_name.encode("utf-8", "surrogatepass").hex())
        lines.append("   decoded         = %r" % b.folder_name)
        break

# are there strings whose raw bytes are NOT valid UTF-8? (legacy_string replaces)
repl = collections.Counter()
samples = []
for b in db.beatmaps:
    for name in ("artist", "artist_unicode", "title", "title_unicode", "creator",
                 "difficulty", "audio_file", "osu_file", "source", "tags", "folder_name",
                 "title_font"):
        v = getattr(b, name)
        if v and "\ufffd" in v:
            repl[name] += 1
            if len(samples) < 5:
                samples.append((name, repr(v)))
lines.append("\nfields whose decoded text contains U+FFFD (invalid UTF-8): %s" % dict(repl))
for s in samples:
    lines.append("   %s = %s" % s)

# show a few real values for the doc
lines.append("\nsample records (first 3):")
for b in db.beatmaps[:3]:
    lines.append("   folder_name=%r" % b.folder_name)
    lines.append("     artist=%r title=%r diff=%r md5=%s mode=%d set=%d id=%d"
                 % (b.artist, b.title, b.difficulty, b.md5, b.mode, b.beatmapset_id, b.beatmap_id))

with open(OUT, "w", encoding="utf-8") as f:
    f.write("\n".join(lines) + "\n")

# also print a safe ASCII view
print("report written to", OUT)
print("player_name bytes:", db.player_name.encode("utf-8").hex() if db.player_name else None)
print("folder names with backslash:", bs)
print("U+FFFD counts:", dict(repl))

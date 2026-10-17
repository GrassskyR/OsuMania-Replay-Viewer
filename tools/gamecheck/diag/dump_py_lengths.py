import sys
sys.path.insert(0, r"D:\Projects\CSharp\Replay-Viewer\tools\osudb")
import beatmapdb

db = beatmapdb.parse(r"D:\Game\osu\osu!.db")
out = r"D:\Projects\CSharp\Replay-Viewer\tools\gamecheck\out\py_record_lengths.txt"
with open(out, "w", encoding="utf-8") as f:
    for i, b in enumerate(db.beatmaps):
        f.write("%d %d %d\n" % (i, b.offset, b.length))
print("wrote", out, "records", len(db.beatmaps), "sum", sum(b.length for b in db.beatmaps))
print("first 5:", [(b.offset, b.length) for b in db.beatmaps[:5]])
print("last 3:", [(b.offset, b.length) for b in db.beatmaps[-3:]])

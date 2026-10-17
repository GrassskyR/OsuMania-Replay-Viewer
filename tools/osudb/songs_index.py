"""Helper: index %osu!\\Songs\\**\\*.osu by file name (lower-case).

Read-only. Used to validate osu!.db records against the real .osu files.
"""

from __future__ import annotations

import hashlib
import os
import sys

DEFAULT_SONGS = r"D:\Game\osu\Songs"


def build(songs_dir: str = DEFAULT_SONGS) -> dict[str, list[str]]:
    idx: dict[str, list[str]] = {}
    for folder in os.scandir(songs_dir):
        if not folder.is_dir():
            continue
        try:
            entries = list(os.scandir(folder.path))
        except OSError:
            continue
        for e in entries:
            if e.is_file() and e.name.lower().endswith(".osu"):
                idx.setdefault(e.name.lower(), []).append(e.path)
    return idx


def md5_of(path: str, chunk: int = 1 << 20) -> str:
    h = hashlib.md5()
    with open(path, "rb") as f:
        while True:
            b = f.read(chunk)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def main():
    idx = build()
    print("distinct .osu file names: %d" % len(idx))
    print("total .osu files        : %d" % sum(len(v) for v in idx.values()))
    dupes = {k: v for k, v in idx.items() if len(v) > 1}
    print("names occurring in >1 folder: %d" % len(dupes))
    for k, v in list(dupes.items())[:3]:
        print("   %s -> %s" % (k, v))


if __name__ == "__main__":
    main()

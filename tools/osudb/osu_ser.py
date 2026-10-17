"""Minimal .NET/osu!-stable binary reading primitives.

All file access is strictly read-only.

Two string conventions appear in the files we care about:

1. osu! "legacy" string (used by osu!.db, scores.db, .osr, collection.db):
       byte marker            -- 0x00 => null, 0x0B => string follows
       [7-bit encoded length] -- .NET BinaryReader 7-bit encoded int (LEB128)
       [UTF-8 bytes]
   Reference: osu.Game/IO/Legacy/SerializationReader.cs (MIT)
       public override string ReadString()
       {
           if (ReadByte() == 0) return null;
           return base.ReadString();   // BinaryReader.ReadString() == LEB128 + UTF-8
       }

2. raw .NET BinaryReader string (LEB128 length + UTF-8), no marker byte.

NOTE: this console is GBK; never print raw bytes, use repr()/hex()/ascii-safe output.
"""

from __future__ import annotations

import struct


class Reader:
    """Cursor over an in-memory buffer, or over a read-only file mapping."""

    __slots__ = ("buf", "pos", "len")

    def __init__(self, buf: bytes | bytearray | memoryview, pos: int = 0):
        self.buf = buf
        self.pos = pos
        self.len = len(buf)

    @property
    def remaining(self) -> int:
        return self.len - self.pos

    # ---- primitives -------------------------------------------------
    def byte(self) -> int:
        v = self.buf[self.pos]
        self.pos += 1
        return v

    def bytes(self, n: int) -> bytes:
        if n < 0:
            raise ValueError("negative length %d" % n)
        if self.pos + n > self.len:
            raise EOFError("want %d bytes at %d, only %d left" % (n, self.pos, self.remaining))
        v = bytes(self.buf[self.pos:self.pos + n])
        self.pos += n
        return v

    def i8(self) -> int:
        v = self.buf[self.pos]
        self.pos += 1
        return v - 256 if v >= 128 else v

    def u16(self) -> int:
        v = struct.unpack_from("<H", self.buf, self.pos)[0]
        self.pos += 2
        return v

    def i16(self) -> int:
        v = struct.unpack_from("<h", self.buf, self.pos)[0]
        self.pos += 2
        return v

    def u32(self) -> int:
        v = struct.unpack_from("<I", self.buf, self.pos)[0]
        self.pos += 4
        return v

    def i32(self) -> int:
        v = struct.unpack_from("<i", self.buf, self.pos)[0]
        self.pos += 4
        return v

    def u64(self) -> int:
        v = struct.unpack_from("<Q", self.buf, self.pos)[0]
        self.pos += 8
        return v

    def i64(self) -> int:
        v = struct.unpack_from("<q", self.buf, self.pos)[0]
        self.pos += 8
        return v

    def f32(self) -> float:
        v = struct.unpack_from("<f", self.buf, self.pos)[0]
        self.pos += 4
        return v

    def f64(self) -> float:
        v = struct.unpack_from("<d", self.buf, self.pos)[0]
        self.pos += 8
        return v

    def bool(self) -> bool:
        return self.byte() != 0

    # ---- strings ----------------------------------------------------
    def leb128(self) -> int:
        """7-bit encoded int, as written by BinaryWriter.Write(string)."""
        result = 0
        shift = 0
        while True:
            b = self.byte()
            result |= (b & 0x7F) << shift
            if (b & 0x80) == 0:
                return result
            shift += 7
            if shift > 35:
                raise ValueError("malformed 7-bit encoded int at %d" % self.pos)

    def net_string(self) -> str:
        """BinaryReader.ReadString(): LEB128 byte-length + UTF-8."""
        n = self.leb128()
        if n == 0:
            return ""
        raw = self.bytes(n)
        return raw.decode("utf-8", errors="replace")

    def legacy_string(self) -> str | None:
        """osu! stable string: 0x00 => null, otherwise byte + ReadString()."""
        if self.byte() == 0:
            return None
        return self.net_string()

    def legacy_byte_array(self) -> bytes | None:
        n = self.i32()
        if n > 0:
            return self.bytes(n)
        if n < 0:
            return None
        return b""

    def read_date_ticks(self) -> int:
        return self.i64()


class StreamReader:
    """Same primitives as Reader but backed by a seekable file object.

    Needed for .osr headers: the HP-graph string can be tens of KB, so we
    skip it with a seek instead of loading the whole replay.
    """

    __slots__ = ("f",)

    def __init__(self, f):
        self.f = f

    def read(self, n: int) -> bytes:
        b = self.f.read(n)
        if len(b) != n:
            raise EOFError("want %d bytes, got %d" % (n, len(b)))
        return b

    def skip(self, n: int) -> None:
        self.f.seek(n, 1)

    def byte(self) -> int:
        return self.read(1)[0]

    def u16(self) -> int:
        return struct.unpack("<H", self.read(2))[0]

    def u32(self) -> int:
        return struct.unpack("<I", self.read(4))[0]

    def i32(self) -> int:
        return struct.unpack("<i", self.read(4))[0]

    def i64(self) -> int:
        return struct.unpack("<q", self.read(8))[0]

    def bool(self) -> bool:
        return self.byte() != 0

    def leb128(self) -> int:
        result = 0
        shift = 0
        while True:
            b = self.byte()
            result |= (b & 0x7F) << shift
            if (b & 0x80) == 0:
                return result
            shift += 7

    def net_string(self) -> str:
        n = self.leb128()
        return self.read(n).decode("utf-8", "replace") if n else ""

    def legacy_string(self) -> str | None:
        if self.byte() == 0:
            return None
        return self.net_string()


def open_ro(path: str, read_all: bool = True):
    """Open a file read-only and (optionally) slurp it."""
    f = open(path, "rb", buffering=0)
    if read_all:
        try:
            return f.read()
        finally:
            f.close()
    return f


# .NET DateTime ticks -> unix epoch offset
TICKS_PER_SECOND = 10_000_000
TICKS_1970 = 621355968000000000


def ticks_to_iso(ticks: int) -> str:
    import datetime
    if ticks <= 0:
        return "0"
    try:
        return (datetime.datetime(1970, 1, 1) +
                datetime.timedelta(seconds=(ticks - TICKS_1970) / TICKS_PER_SECOND)).isoformat()
    except (OverflowError, OSError):
        return "overflow:%d" % ticks

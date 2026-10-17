#!/usr/bin/env python3
"""Generates build/icon.png (128x128), the NuGet package icon: a "QF" monogram. Standard library only.

    python3 build/make-icon.py
"""
import math
import struct
import zlib
from pathlib import Path

SIZE, SS = 128, 4            # output size, supersampling factor per axis
BG, FG, ACCENT = (11, 37, 69), (255, 255, 255), (46, 196, 182)


def inside(x, y):
    """Returns the colour at (x, y) in 0..128 space, or None for transparent."""
    # Rounded square background.
    r, m = 22.0, 0.0
    cx, cy = min(max(x, r + m), SIZE - r - m), min(max(y, r + m), SIZE - r - m)
    if math.hypot(x - cx, y - cy) > r:
        return None
    # Q: ring centred (46, 62), outer radius 27, inner 17, with a tail towards the lower right.
    d = math.hypot(x - 46, y - 62)
    if 17 <= d <= 27:
        return FG
    t = (x - 52) * 0.7071 + (y - 68) * 0.7071      # along the 45-degree tail
    n = -(x - 52) * 0.7071 + (y - 68) * 0.7071     # across it
    if 0 <= t <= 26 and abs(n) <= 4.5:
        return ACCENT
    # F: stem, top bar, middle bar.
    if 82 <= x <= 92 and 35 <= y <= 89:
        return FG
    if 82 <= x <= 116 and 35 <= y <= 45:
        return FG
    if 82 <= x <= 108 and 56 <= y <= 66:
        return FG
    return BG


rows = []
for py in range(SIZE):
    row = bytearray([0])
    for px in range(SIZE):
        acc, a = [0, 0, 0], 0
        for sy in range(SS):
            for sx in range(SS):
                c = inside(px + (sx + 0.5) / SS, py + (sy + 0.5) / SS)
                if c:
                    a += 1
                    for k in range(3):
                        acc[k] += c[k]
        n = SS * SS
        row += bytes([acc[0] // a, acc[1] // a, acc[2] // a, 255 * a // n]) if a else bytes(4)
    rows.append(bytes(row))


def chunk(tag, data):
    body = tag + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


png = (b"\x89PNG\r\n\x1a\n"
       + chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
       + chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
       + chunk(b"IEND", b""))
Path(__file__).with_name("icon.png").write_bytes(png)

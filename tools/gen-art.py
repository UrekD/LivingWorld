#!/usr/bin/env python3
"""Generate Living World's icon textures as PNGs, stdlib only.

Usage: tools/gen-art.py      writes into Mods/LivingWorld/Textures/LivingWorld/
Icons are white glyphs with a dark outline on transparent 64x64 canvases; the game tints them with the faction
colour. Each glyph is a list of shapes in unit coordinates (0..1, y down), rendered with 4x4 supersampling.
"""
import math
import os
import struct
import zlib

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Mods", "LivingWorld", "Textures", "LivingWorld")
SIZE = 64
SS = 4


def poly(*pts):
    return ("poly", pts)


def circle(cx, cy, r):
    return ("circle", (cx, cy, r))


def line(x0, y0, x1, y1, w):
    """A thick segment as a polygon."""
    dx, dy = x1 - x0, y1 - y0
    n = math.hypot(dx, dy) or 1
    ox, oy = -dy / n * w / 2, dx / n * w / 2
    return poly((x0 + ox, y0 + oy), (x1 + ox, y1 + oy), (x1 - ox, y1 - oy), (x0 - ox, y0 - oy))


def inside(shape, x, y):
    kind, d = shape
    if kind == "circle":
        cx, cy, r = d
        return (x - cx) ** 2 + (y - cy) ** 2 <= r * r
    pts = d
    hit = False
    j = len(pts) - 1
    for i in range(len(pts)):
        xi, yi = pts[i]
        xj, yj = pts[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            hit = not hit
        j = i
    return hit


GLYPHS = {
    # Crossed swords
    "WarBand": [line(0.2, 0.2, 0.8, 0.8, 0.09), line(0.8, 0.2, 0.2, 0.8, 0.09),
                line(0.6, 0.72, 0.72, 0.6, 0.07), line(0.4, 0.72, 0.28, 0.6, 0.07),
                circle(0.16, 0.84, 0.07), circle(0.84, 0.84, 0.07)],
    # Sack of goods
    "Merchants": [circle(0.5, 0.62, 0.28), poly((0.38, 0.38), (0.62, 0.38), (0.56, 0.3), (0.44, 0.3)),
                  poly((0.36, 0.22), (0.64, 0.22), (0.58, 0.32), (0.42, 0.32))],
    # Tent
    "Settlers": [poly((0.5, 0.15), (0.88, 0.82), (0.12, 0.82)), line(0.5, 0.08, 0.5, 0.2, 0.05)],
    # Flag
    "Envoy": [line(0.28, 0.1, 0.28, 0.9, 0.07), poly((0.3, 0.14), (0.82, 0.24), (0.3, 0.5))],
    # Three heads
    "Visitors": [circle(0.3, 0.42, 0.13), circle(0.7, 0.42, 0.13), circle(0.5, 0.36, 0.15),
                 poly((0.12, 0.85), (0.88, 0.85), (0.78, 0.58), (0.22, 0.58))],
    # Bundle on a stick
    "Refugees": [line(0.2, 0.85, 0.75, 0.2, 0.06), circle(0.72, 0.3, 0.18)],
}

# UI icons (written to Textures/LivingWorld/UI/)
UI_GLYPHS = {
    # Open book
    "Chronicle": [poly((0.08, 0.22), (0.48, 0.3), (0.48, 0.86), (0.08, 0.78)),
                  poly((0.52, 0.3), (0.92, 0.22), (0.92, 0.78), (0.52, 0.86))],
}


def render(shapes):
    rows = []
    for py in range(SIZE):
        row = bytearray()
        for px in range(SIZE):
            cover = 0
            for sy in range(SS):
                for sx in range(SS):
                    x = (px + (sx + 0.5) / SS) / SIZE
                    y = (py + (sy + 0.5) / SS) / SIZE
                    if any(inside(s, x, y) for s in shapes):
                        cover += 1
            row += bytes((255, 255, 255, int(255 * cover / (SS * SS))))
        rows.append(row)
    # Dark outline: transparent pixels next to opaque ones.
    out = [bytearray(r) for r in rows]
    for py in range(SIZE):
        for px in range(SIZE):
            if rows[py][px * 4 + 3] > 0:
                continue
            near = 0
            for dy in (-2, -1, 0, 1, 2):
                for dx in (-2, -1, 0, 1, 2):
                    if dx * dx + dy * dy > 5:
                        continue
                    qx, qy = px + dx, py + dy
                    if 0 <= qx < SIZE and 0 <= qy < SIZE:
                        near = max(near, rows[qy][qx * 4 + 3])
            if near:
                out[py][px * 4:px * 4 + 4] = bytes((20, 20, 20, int(near * 0.85)))
    return out


def write_png(path, rows):
    raw = b"".join(b"\x00" + bytes(r) for r in rows)

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def main():
    out_dir = os.path.join(ROOT, "World")
    os.makedirs(out_dir, exist_ok=True)
    for name, shapes in GLYPHS.items():
        write_png(os.path.join(out_dir, name + ".png"), render(shapes))
        print("wrote", name)
    ui_dir = os.path.join(ROOT, "UI")
    os.makedirs(ui_dir, exist_ok=True)
    for name, shapes in UI_GLYPHS.items():
        write_png(os.path.join(ui_dir, name + ".png"), render(shapes))
        print("wrote UI", name)


if __name__ == "__main__":
    main()

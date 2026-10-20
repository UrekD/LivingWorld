#!/usr/bin/env python3
"""Original, faction-tintable Living World art; Python standard library only.

Run python3 tools/gen-art.py to regenerate all seven transparent RGBA textures.
Add --preview to also write /tmp/lw-art-preview.png, including faction-multiplied
24/32 px samples and 64/128 px views on a map-like background. All drawing is
specified here in 128 px art coordinates and rasterized at 4x resolution. Curved,
slightly asymmetric contours and broad painted facets stay legible when reduced.
"""
import argparse
import math
from pathlib import Path
import struct
import zlib

ROOT = Path(__file__).resolve().parents[1] / 'Mods/LivingWorld/Textures/LivingWorld'
SIZE = 128
SS = 4
INK = (48, 48, 48, 218)
PALE = 246
BODY = 224
SHADE = 171
CREASE = 112


def rgba(value):
    return (value, value, value, 255) if isinstance(value, int) else value


def curve(start, *segments):
    """Flatten a hand-drawn contour: points are lines, six-tuples are cubics."""
    points = [start]
    for segment in segments:
        if len(segment) == 2:
            points.append(segment)
            continue
        x0, y0 = points[-1]
        x1, y1, x2, y2, x3, y3 = segment
        length = (math.hypot(x1-x0, y1-y0) + math.hypot(x2-x1, y2-y1)
                  + math.hypot(x3-x2, y3-y2))
        steps = max(4, math.ceil(length / 2))
        for i in range(1, steps + 1):
            t = i / steps
            u = 1 - t
            points.append((u**3*x0 + 3*u*u*t*x1 + 3*u*t*t*x2 + t**3*x3,
                           u**3*y0 + 3*u*u*t*y1 + 3*u*t*t*y2 + t**3*y3))
    return points


class Canvas:
    def __init__(self, width, height, scale=1):
        self.width, self.height, self.scale = width*scale, height*scale, scale
        self.data = bytearray(self.width * self.height * 4)

    def pixel(self, x, y, color):
        if not (0 <= x < self.width and 0 <= y < self.height):
            return
        i = (y*self.width+x)*4
        r, g, b, a = color
        if a == 255:
            self.data[i:i+4] = bytes(color)
        elif a:
            old_a = self.data[i+3]
            out_a = a + old_a * (255-a) / 255
            for k, v in enumerate((r, g, b)):
                self.data[i+k] = round((v*a + self.data[i+k]*old_a*(255-a)/255) / out_a)
            self.data[i+3] = round(out_a)

    def polygon(self, points, color):
        color = rgba(color)
        pts = [(x*self.scale, y*self.scale) for x, y in points]
        for y in range(max(0, math.ceil(min(p[1] for p in pts)-0.5)),
                       min(self.height, math.ceil(max(p[1] for p in pts)-0.5))):
            crossings = []
            for (x0, y0), (x1, y1) in zip(pts, pts[1:]+pts[:1]):
                if (y0 <= y+0.5 < y1) or (y1 <= y+0.5 < y0):
                    crossings.append(x0 + (y+0.5-y0)*(x1-x0)/(y1-y0))
            crossings.sort()
            for left, right in zip(crossings[::2], crossings[1::2]):
                lo, hi = max(0, math.ceil(left-0.5)), min(self.width, math.ceil(right-0.5))
                if hi <= lo:
                    continue
                if color[3] == 255:
                    i = (y*self.width+lo)*4
                    self.data[i:i+(hi-lo)*4] = bytes(color)*(hi-lo)
                else:
                    for x in range(lo, hi):
                        self.pixel(x, y, color)

    def disk(self, x, y, radius, color):
        steps = max(16, math.ceil(radius*4))
        self.polygon([(x+radius*math.cos(i*math.tau/steps),
                       y+radius*math.sin(i*math.tau/steps)) for i in range(steps)], color)

    def stroke(self, points, color=INK, width=2.5, closed=False):
        pts = list(points)
        if closed:
            pts.append(pts[0])
        # Build a continuous ribbon, so translucent ink doesn't
        # accumulate at each flattened Bezier segment.
        left, right = [], []
        for i, (x, y) in enumerate(pts):
            before = pts[i-1] if i else (pts[-2] if closed else pts[0])
            after = pts[i+1] if i+1 < len(pts) else (pts[1] if closed else pts[-1])
            dx, dy = after[0]-before[0], after[1]-before[1]
            length = math.hypot(dx, dy) or 1
            ox, oy = -dy/length*width/2, dx/length*width/2
            left.append((x+ox, y+oy))
            right.append((x-ox, y-oy))
        self.polygon(left+right[::-1], color)
        if not closed:
            self.disk(*pts[0], width/2, color)
            self.disk(*pts[-1], width/2, color)

    def shape(self, points, color=BODY, width=3):
        # A faint outer brush edge softens the contour without a black halo.
        self.stroke(points, (48, 48, 48, 48), width+1.4, closed=True)
        self.polygon(points, color)
        self.stroke(points, INK, width, closed=True)

    def finish(self):
        return resize(self.data, self.width, self.height, self.width//self.scale,
                      self.height//self.scale)


def resize(data, width, height, out_w, out_h):
    """Area reduction in premultiplied alpha; transparent edges keep their tint."""
    out = bytearray(out_w*out_h*4)
    for y in range(out_h):
        y0, y1 = y*height/out_h, (y+1)*height/out_h
        for x in range(out_w):
            x0, x1 = x*width/out_w, (x+1)*width/out_w
            total = [0.0]*4
            for sy in range(int(y0), math.ceil(y1)):
                wy = min(y1, sy+1)-max(y0, sy)
                for sx in range(int(x0), math.ceil(x1)):
                    weight = wy*(min(x1, sx+1)-max(x0, sx))
                    i = (sy*width+sx)*4
                    alpha = data[i+3]*weight
                    for k in range(3):
                        total[k] += data[i+k]*alpha
                    total[3] += alpha
            i = (y*out_w+x)*4
            if total[3]:
                out[i:i+3] = bytes(round(v/total[3]) for v in total[:3])
                out[i+3] = round(total[3]/((x1-x0)*(y1-y0)))
    return out


def war_band(c):
    def sword(angle, back=False):
        sn, cs = math.sin(angle), math.cos(angle)
        def tr(points):
            return [(64+x*cs-y*sn, 64+x*sn+y*cs) for x, y in points]
        def shape(points, tone):
            c.shape(tr(points), tone)
        def fill(points, tone):
            c.polygon(tr(points), tone)
        def stroke(points, tone, width=2):
            c.stroke(tr(points), tone, width)
        shape([(0,-53), (8,-39), (7,13), (-7,13), (-8,-37)], 215 if back else BODY)
        fill([(0,-49), (6,-38), (5,12), (0,11)], SHADE)
        fill([(-1,-47), (-5,-36), (-4,11), (-1,10)], PALE)
        stroke([(0,-35), (0,9)], (250,250,250,200), 1.5)
        shape([(-4,21), (5,21), (4,39), (-4,40)], 181)
        stroke([(-3,27), (3,29)], CREASE)
        stroke([(-3,33), (3,35)], CREASE)
        shape(curve((-17,14), (-9,17,8,17,17,13), (17,20),
                    (7,23,-7,23,-17,20)), BODY)
        stroke([(-13,16), (1,19), (13,16)], PALE, 1.8)
        shape(curve((-5,39), (-8,44,-3,49,2,47), (8,46,7,40,4,38)), BODY)
    sword(-0.73, True)
    sword(0.73)


def merchants(c):
    c.shape(curve((47,42), (38,32,40,27,36,20), (49,22,52,18,59,21),
                  (67,24,75,18,88,20), (84,29,77,38,78,43)), BODY)
    c.polygon(curve((63,23), (69,27,68,35,68,41), (78,43),
                    (80,34,84,26,86,22), (74,24,69,21,63,23)), SHADE)
    c.stroke(curve((48,25), (52,29,52,34,55,38)), PALE, 3)
    c.shape(curve((48,42), (44,51,27,61,26,83), (23,104,37,113,62,112),
                  (89,114,104,103,101,85), (99,63,83,52,77,42),
                  (69,39,56,40,48,42)), BODY)
    c.polygon(curve((75,46), (81,61,93,72,91,88), (89,105,70,109,47,108),
                    (74,115,103,110,101,89), (101,70,87,56,75,46)), SHADE)
    c.polygon(curve((44,57), (34,67,31,79,33,87), (37,91,39,84,39,79),
                    (40,68,48,58,51,52)), PALE)
    c.stroke(curve((54,47), (51,53,51,56,49,60)), CREASE, 2.3)
    c.stroke(curve((72,46), (71,52,77,58,78,64)), CREASE, 2.3)
    c.stroke(curve((38,95), (41,101,47,102,52,102)), (128,128,128,220), 2)
    # One broad, visibly sewn cloth patch, rather than a field of tiny marks.
    c.shape([(69,77), (83,79), (81,94), (67,92)], 201, 1.7)
    c.stroke([(69,81), (73,80)], CREASE, 1.6)
    c.stroke([(77,91), (81,90)], CREASE, 1.6)
    c.shape(curve((46,39), (56,42,69,38,79,39), (81,44),
                  (70,46,54,47,46,44)), 180, 2)
    c.stroke(curve((48,40), (57,42,68,40,77,40)), PALE, 1.8)
    c.stroke(curve((76,44), (84,45,88,49,87,55)), INK, 2.5)
    c.stroke(curve((77,44), (80,52,83,57,85,59)), BODY, 2.4)


def settlers(c):
    # A three-quarter canvas tent: light front, darker receding side, open flap.
    c.shape([(17,104), (49,27), (81,20), (115,99), (79,108)], 190)
    c.polygon([(49,28), (81,22), (112,98), (78,105)], SHADE)
    c.polygon([(52,29), (78,25), (84,40), (65,38)], BODY)
    c.shape(curve((17,104), (28,79,37,49,49,27), (62,52,70,80,79,108),
                  (55,106,37,108,17,104)), BODY)
    c.polygon([(49,32), (50,98), (23,101)], PALE)
    c.shape([(49,53), (64,103), (36,104)], 89, 2.3)
    c.shape(curve((49,52), (47,70,40,87,33,99), (43,96),
                  (50,76,50,61,49,52)), 211, 1.8)
    c.polygon([(50,57), (63,102), (56,99)], 154)
    c.stroke([(62,42), (84,97)], (118,118,118,230), 2)
    c.stroke([(64,43), (86,96)], (215,215,215,230), 1.6)
    c.stroke([(48,19), (50,30)], INK, 4.5)
    c.stroke([(48,19), (50,28)], BODY, 2.3)
    c.stroke([(81,14), (81,23)], INK, 4)
    c.stroke([(81,14), (81,22)], BODY, 1.8)
    c.stroke([(28,78), (13,106)], (193,193,193,240), 1.8)
    c.stroke([(99,76), (117,103)], (193,193,193,240), 1.8)
    c.stroke([(12,102), (13,109)], INK, 3)
    c.stroke([(117,100), (117,107)], INK, 3)


def envoy(c):
    c.shape(curve((36,26), (58,17,75,43,105,29), (102,48,97,58,105,72),
                  (75,83,61,57,36,65)), BODY)
    c.polygon(curve((39,48), (62,38,74,70,102,59), (102,65,103,69,104,71),
                    (78,82,61,59,39,65)), SHADE)
    c.polygon(curve((41,27), (58,24,65,39,81,38), (78,45),
                    (61,43,56,30,41,33)), PALE)
    c.stroke(curve((70,36), (73,43,72,49,72,54)), (132,132,132,230), 2.3)
    c.stroke(curve((93,33), (90,38,92,43,90,46)), (250,250,250,210), 2.2)
    c.shape(curve((30,22), (35,20), (37,109), (31,111)), 193)
    c.stroke([(32,25), (33,105)], PALE, 1.8)
    c.shape(curve((28,17), (27,8,39,8,40,16), (40,23,29,25,28,17)), BODY, 2.6)
    c.stroke(curve((30,15), (31,12,35,12,36,14)), PALE, 2)
    c.shape([(27,108), (40,107), (40,113), (27,113)], 186, 2)


def visitors(c):
    def person(cx, cy, radius, left, right, bottom, tone):
        c.shape(curve((cx-5,cy+radius-1), (cx-5,cy+radius+7),
                      (left+3,cy+radius+9,left,cy+radius+19,left,bottom),
                      (left+7,bottom+3,right-6,bottom+4,right,bottom),
                      (right,cy+radius+19,right-3,cy+radius+9,cx+6,cy+radius+7),
                      (cx+6,cy+radius-1)), tone)
        c.polygon(curve((cx+5,cy+radius+8), (right-2,cy+radius+12,right-2,bottom-4,right-1,bottom),
                        (cx+2,bottom+1), (cx+8,bottom-10,cx+8,cy+radius+14,cx+5,cy+radius+8)), tone-35)
        c.shape(curve((cx-radius,cy-2), (cx-radius-1,cy-radius-3,cx+radius-1,cy-radius-5,cx+radius,cy-2),
                      (cx+radius+1,cy+radius+3,cx-radius+1,cy+radius+5,cx-radius,cy-2)), tone)
        c.polygon(curve((cx+radius-3,cy-5), (cx+radius+3,cy+9,cx+2,cy+radius+4,cx-5,cy+radius-1),
                        (cx+3,cy+radius-3,cx+4,cy+1,cx+radius-3,cy-5)), tone-35)
        c.stroke(curve((cx-radius+4,cy-4), (cx-radius+4,cy-8,cx-4,cy-radius+1,cx-1,cy-radius+1)), PALE, 2.3)
    person(34, 45, 11, 14, 55, 91, 209)
    person(93, 43, 11, 73, 114, 91, 202)
    person(63, 39, 14, 36, 92, 108, 235)
    c.stroke(curve((46,85), (45,89,44,94,44,98)), (254,254,254,220), 2.5)
    c.stroke(curve((55,65), (61,71,66,71,72,64)), (130,130,130,220), 2)


def refugees(c):
    # A substantial diagonal staff and tied cloth bundle; no tiny walking figure
    # competing with the silhouette when this is reduced to a world-map marker.
    c.stroke(curve((22,110), (40,87,69,44,91,17)), INK, 8)
    c.stroke(curve((22,109), (41,85,69,45,91,17)), 202, 4.8)
    c.stroke(curve((22,107), (39,85,52,66,61,53)), PALE, 1.7)
    c.shape(curve((72,39), (61,37,54,48,54,66), (51,84,61,94,79,93),
                  (98,95,108,84,103,68), (99,52,91,46,88,40),
                  (82,36,77,39,72,39)), BODY)
    c.polygon(curve((83,44), (89,57,96,67,93,78), (91,88,77,90,66,89),
                    (78,97,104,92,104,77), (104,59,91,47,83,44)), SHADE)
    c.polygon(curve((67,48), (59,57,58,70,60,75), (64,77,65,71,64,66),
                    (65,56,70,50,73,46)), PALE)
    c.stroke(curve((74,44), (71,51,70,57,70,62)), (128,128,128,230), 2.3)
    c.stroke(curve((85,46), (83,53,88,57,90,63)), (128,128,128,230), 2.3)
    c.stroke(curve((66,81), (70,85,74,86,80,86)), (131,131,131,210), 2)
    c.shape(curve((74,39), (66,37,63,29,67,26), (73,24,79,31,79,36),
                  (82,30,88,27,91,30), (95,37,86,42,81,42)), 219, 2.5)
    c.shape(curve((75,36), (82,33,85,37,83,42), (80,47,73,43,75,36)), 189, 2)
    c.stroke([(68,29), (74,35)], PALE, 2)


def chronicle(c):
    # Splayed page blocks with a curved gutter and a narrow darker cover below.
    c.shape(curve((12,31), (29,27,48,31,63,41), (81,30,101,27,116,30),
                  (115,99), (94,96,77,101,64,109),
                  (46,99,30,98,12,99)), 160, 3)
    c.shape(curve((15,25), (34,22,49,29,63,38), (64,102),
                  (47,93,31,91,15,93)), BODY, 2.5)
    c.shape(curve((64,38), (81,28,98,22,112,25), (112,93),
                  (94,91,77,96,64,102)), 234, 2.5)
    c.polygon(curve((18,28), (36,26,49,32,58,38), (58,91),
                    (45,85,31,85,18,86)), PALE)
    c.polygon(curve((69,40), (82,31,97,27,109,28), (109,84),
                    (91,84,81,89,69,94)), PALE)
    c.polygon(curve((58,37), (62,39), (64,101), (57,95),
                    (59,72,59,51,58,37)), 178)
    c.stroke(curve((64,39), (62,56,63,81,64,101)), (103,103,103,240), 2.3)
    c.stroke(curve((19,89), (33,88,46,92,55,96)), 151, 1.8)
    c.stroke(curve((73,96), (84,91,98,88,108,89)), 162, 1.8)
    for y, end in ((45,50), (56,48), (67,51)):
        c.stroke(curve((25,y), (34,y,43,y+2,end,y+6)), (162,162,162,235), 2.2)
    for y, end in ((45,101), (56,101), (67,95)):
        c.stroke(curve((77,y+6), (86,y+2,93,y,end,y)), (162,162,162,235), 2.2)


ART = [('World', 'WarBand', war_band), ('World', 'Merchants', merchants),
       ('World', 'Settlers', settlers), ('World', 'Envoy', envoy),
       ('World', 'Visitors', visitors), ('World', 'Refugees', refugees),
       ('UI', 'Chronicle', chronicle)]


def write_png(path, data, width=SIZE, height=SIZE):
    def chunk(tag, content):
        return (struct.pack('>I', len(content)) + tag + content
                + struct.pack('>I', zlib.crc32(tag+content) & 0xffffffff))
    raw = b''.join(b'\0'+data[y*width*4:(y+1)*width*4] for y in range(height))
    png = (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0))
           + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))
    Path(path).write_bytes(png)


# A tiny preview-only bitmap alphabet keeps the contact sheet dependency-free.
FONT = dict(zip('ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789/-', (
    '010/101/111/101/101', '110/101/110/101/110', '011/100/100/100/011',
    '110/101/101/101/110', '111/100/110/100/111', '111/100/110/100/100',
    '011/100/101/101/011', '101/101/111/101/101', '111/010/010/010/111',
    '001/001/001/101/010', '101/101/110/101/101', '100/100/100/100/111',
    '101/111/111/101/101', '101/111/111/111/101', '010/101/101/101/010',
    '110/101/110/100/100', '010/101/101/111/011', '110/101/110/101/101',
    '011/100/010/001/110', '111/010/010/010/010', '101/101/101/101/111',
    '101/101/101/101/010', '101/101/111/111/101', '101/101/010/101/101',
    '101/101/010/010/010', '111/001/010/100/111',
    '111/101/101/101/111', '010/110/010/010/111', '110/001/010/100/111',
    '110/001/010/001/110', '101/101/111/001/001', '111/100/110/001/110',
    '011/100/110/101/010', '111/001/010/010/010', '010/101/010/101/010',
    '010/101/011/001/110', '001/001/010/100/100', '000/000/111/000/000')))


def label(c, x, y, text, scale=2):
    for char in text.upper():
        for row, bits in enumerate(FONT.get(char, '000/000/000/000/000').split('/')):
            for col, bit in enumerate(bits):
                if bit == '1':
                    for dy in range(scale):
                        for dx in range(scale):
                            c.pixel(x+col*scale+dx, y+row*scale+dy, (216,216,199,255))
        x += 4*scale


def preview(icons):
    width, height = 1200, 1120
    c = Canvas(width, height)
    # Broad olive/earth patches and subdued watercourses suggest the world map
    # without borrowing a game asset or hiding the icons in a busy screenshot.
    for y in range(height):
        for x in range(width):
            field = (math.sin(x/73+y/95)*4 + math.sin(x/31-y/53)*3
                     + math.sin(x/137-y/47)*3)
            earth = max(0, math.sin(x/181+y/103))*8
            grain = ((x*13+y*29+(x*y)%19)%11-5)*0.35
            color = (round(52+field+earth+grain), round(59+field+grain),
                     round(42+field-earth/3+grain), 255)
            c.pixel(x, y, color)
    for offset in (-100, 470, 950):
        c.stroke(curve((offset,80), (offset+130,310,offset-90,420,offset+80,620),
                       (offset+230,830,offset+100,930,offset+190,1120)),
                 (36,55,66,90), 3)
    label(c, 22, 20, 'LIVING WORLD / PAINTED ICONS', 3)
    for text, x in [('128 / 1X', 142), ('64', 310), ('32', 405), ('24', 477),
                    ('RED 32 / 24', 570), ('BLUE 32 / 24', 755),
                    ('YELLOW 32 / 24', 945)]:
        label(c, x, 61, text)
    tints = [(224,95,84), (106,175,237), (237,207,112)]
    for row, (name,data) in enumerate(icons.items()):
        y = 84+row*144
        label(c, 16, y+61, name, 2)
        for size,x in [(128,136), (64,308), (32,404), (24,477)]:
            stamp(c,data,size,x,y+(128-size)//2,(255,255,255))
        for j,tint in enumerate(tints):
            stamp(c,data,32,570+j*190,y+48,tint)
            stamp(c,data,24,617+j*190,y+52,tint)
        if name == 'Chronicle':
            # Also check the untinted book in a dark main-button-bar setting.
            c.polygon([(1080,y+36),(1180,y+36),(1180,y+92),(1080,y+92)],
                      (28,34,38,255))
            stamp(c,data,32,1089,y+48,(255,255,255))
            stamp(c,data,24,1136,y+52,(255,255,255))
    label(c, 20, 1100, 'RGBA / GREYSCALE / 4X SUPERSAMPLING / FACTION MULTIPLY', 2)
    write_png('/tmp/lw-art-preview.png', c.data, width, height)


def stamp(canvas, data, size, x, y, tint):
    small = resize(data, SIZE, SIZE, size, size)
    for sy in range(size):
        for sx in range(size):
            i = (sy*size+sx)*4
            color = tuple(round(small[i+k]*tint[k]/255) for k in range(3))+(small[i+3],)
            canvas.pixel(x+sx,y+sy,color)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--preview', action='store_true', help='also write /tmp/lw-art-preview.png')
    args = parser.parse_args()
    icons = {}
    for folder, name, draw in ART:
        canvas = Canvas(SIZE, SIZE, SS)
        draw(canvas)
        data = canvas.finish()
        path = ROOT / folder / (name+'.png')
        path.parent.mkdir(parents=True, exist_ok=True)
        write_png(path, data)
        icons[name] = data
        print('wrote', path.relative_to(ROOT), f'{SIZE}x{SIZE}')
    if args.preview:
        preview(icons)
        print('wrote /tmp/lw-art-preview.png')


if __name__ == '__main__':
    main()

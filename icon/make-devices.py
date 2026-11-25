# Draws the device icons: big case (tower), small case (mini), laptop. Same paper-cut look as LanTalk.ico.
# Output in devices/: <name>.svg, <name>.ico (16-256 px), <name>-512.png, <name>-mono.svg (one colour, for avatars),
# <name>-mono-128.png, preview.png. Needs Pillow.
import importlib.util, math, os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location('make_icon', os.path.join(HERE, 'make-icon.py'))
base = importlib.util.module_from_spec(spec); spec.loader.exec_module(base)
CLAY, IVORY, INK = base.CLAY, base.IVORY, base.INK
DEEP = (173, 82, 51)                              # the face turned away from the light
SHINE = (46, 45, 43)                              # reflection on a dark screen
OUT = os.path.join(HERE, 'devices')

def rnd(pts, r):
    """Polygon with rounded corners; r is one radius or one per corner."""
    out, n = [], len(pts)
    rs = r if isinstance(r, (list, tuple)) else [r] * n
    for i in range(n):
        (px, py), (x, y), (nx, ny) = pts[i - 1], pts[i], pts[(i + 1) % n]
        a, b = math.hypot(px - x, py - y), math.hypot(nx - x, ny - y)
        k = min(rs[i], a / 2, b / 2)
        if k <= 0: out.append((x, y)); continue
        s, e = (x + (px - x) * k / a, y + (py - y) * k / a), (x + (nx - x) * k / b, y + (ny - y) * k / b)
        out += base.bez(s, (s[0] + (x - s[0]) * .56, s[1] + (y - s[1]) * .56), (e[0] + (x - e[0]) * .56, e[1] + (y - e[1]) * .56), e, 12)
    return out

def oval(cx, cy, rx, ry=None, wob=0.012): return base.blob(cx, cy, rx, ry or rx, 2, wob)
def box(x1, y1, x2, y2, r=0): return rnd([(x1, y1), (x2, y1), (x2, y2), (x1, y2)], r)

def fan(cx, cy, rx, ry, col, hub, n=5):
    """Fan seen almost straight on: swept blades and a hub, so it can't be taken for a loudspeaker."""
    out = []
    for b in range(n):
        a, w = math.tau * b / n - .5, math.tau / n
        p = []
        for i in range(9): t = i / 8; p.append((.2 + .8 * t, a + .62 * t * t))                # leading edge
        for i in range(9): t = i / 8; p.append((1, a + .62 + w * .74 * t))                    # tip
        for i in range(9): t = 1 - i / 8; p.append((.2 + .8 * t, a + w * .74 + .62 * t * t))  # trailing edge
        out.append(([(cx + rx * r * math.cos(q), cy + ry * r * math.sin(q)) for r, q in p], col))
    return out + [(oval(cx, cy, rx * .36, ry * .36, 0), hub)]

# ---- colour: one drawing for 32 px and up, a bolder one on the pixel grid for 24 and for 16/20 px ----
def tower(size):
    if size <= 20:
        return 16, [(box(2, 0, 14, 16, 1.4), CLAY), (rnd([(2, 0), (5, 0), (5, 16), (2, 16)], [1.4, 0, 0, 1.4]), DEEP), (box(3, 2, 4, 3), IVORY),
                    (box(6, 2, 13, 13), INK), (oval(8.5, 5, 2, 2, 0), IVORY), (box(11, 3, 12, 7), IVORY), (box(6, 8, 12, 10), CLAY)]
    if size <= 24:
        return 24, [(box(3, 0, 21, 23, 2), CLAY), (rnd([(3, 0), (8, 0), (8, 23), (3, 23)], [2, 0, 0, 2]), DEEP), (box(4, 23, 7, 24), DEEP), (box(17, 23, 20, 24), DEEP),
                    (box(5, 3, 6, 5), IVORY), (box(10, 3, 19, 19, .6), INK), (oval(13, 7, 2.3, 2.3, 0), IVORY), (box(16, 5, 17, 9), IVORY), (box(10, 12, 18, 14), CLAY)]
    s = [(box(56, 226, 80, 247, 6), DEEP), (box(178, 220, 202, 243, 6), DEEP),
         (rnd([(91, 10), (216, 24), (214, 230), (91, 246)], [0, 18, 18, 0]), CLAY),
         (rnd([(92, 10), (92, 246), (42, 230), (41, 24)], [0, 0, 16, 16]), DEEP),
         (oval(66, 50, 8, 9), IVORY), (rnd([(61, 82), (71, 81), (71, 204), (61, 203)], 5), CLAY),
         (rnd([(106, 34), (200, 43), (199, 186), (106, 195)], 12), INK)]
    s += fan(138, 84, 26, 28, IVORY, INK)
    for x in (173, 184): s.append((rnd([(x, 60), (x + 6, 60.5), (x + 6, 108), (x, 108)], 2.5), IVORY))
    s += [(rnd([(106, 134), (190, 132), (190, 157), (106, 160)], [0, 6, 6, 0]), CLAY), (rnd([(116, 144), (166, 142.5), (166, 148), (116, 150)], 2.7), INK)]
    return 256, s

def mini(size):
    if size <= 20:
        return 16, [(box(1, 4, 15, 14, 1.4), CLAY), (rnd([(1, 4), (4, 4), (4, 14), (1, 14)], [1.4, 0, 0, 1.4]), DEEP), (box(2, 6, 3, 7), IVORY), (box(2, 14, 4, 15), DEEP), (box(12, 14, 14, 15), DEEP),
                    (box(5, 5, 14, 12), INK), (oval(7.5, 7.5, 1.8, 1.8, 0), IVORY), (box(11, 6, 12, 9), IVORY), (box(5, 10, 13, 11), CLAY)]
    if size <= 24:
        return 24, [(box(2, 6, 22, 21, 2), CLAY), (rnd([(2, 6), (7, 6), (7, 21), (2, 21)], [2, 0, 0, 2]), DEEP), (box(3, 21, 6, 22), DEEP), (box(18, 21, 21, 22), DEEP),
                    (box(4, 9, 5, 11), IVORY), (box(9, 8, 20, 18, .6), INK), (oval(12, 11, 2, 2, 0), IVORY), (box(15, 9, 16, 13), IVORY), (box(17, 9, 18, 13), IVORY), (box(9, 14, 19, 16), CLAY)]
    s = [(box(42, 196, 66, 217, 6), DEEP), (box(196, 190, 220, 213, 6), DEEP),
         (rnd([(84, 52), (234, 64), (232, 200), (84, 216)], [0, 18, 18, 0]), CLAY),
         (rnd([(85, 52), (85, 216), (24, 200), (23, 64)], [0, 0, 18, 18]), DEEP),
         (oval(54, 91, 9, 10), IVORY)]
    for y in (126, 145, 164): s.append((rnd([(42, y), (67, y + (y - 126) * .04), (67, y + 8 + (y - 126) * .04), (42, y + 7)], 3.5), CLAY))
    s.append((rnd([(101, 74), (217, 82), (216, 175), (101, 183)], 12), INK))
    s += fan(138, 112, 25, 26, IVORY, INK)
    for x in (178, 191): s.append((rnd([(x, 94), (x + 7, 94.5), (x + 7, 130), (x, 130)], 2.5), IVORY))
    s += [(rnd([(101, 146), (206, 144), (206, 163), (101, 166)], [0, 6, 6, 0]), CLAY)]
    return 256, s

def laptop(size):
    if size <= 20:
        return 16, [(box(2, 2, 14, 12, 1.2), CLAY), (box(3, 3, 13, 11), INK), (rnd([(0, 12), (16, 12), (16, 13), (15, 14), (1, 14), (0, 13)], 0), DEEP), (box(6, 12, 10, 13), IVORY)]
    if size <= 24:
        return 24, [(box(3, 3, 21, 18, 2), CLAY), (box(5, 5, 19, 16, .6), INK), (rnd([(0, 18), (24, 18), (22.5, 21), (1.5, 21)], [0, 0, 1, 1]), DEEP), (box(9, 18, 15, 19), IVORY)]
    s = [(rnd([(40, 46), (217, 43), (219, 196), (38, 196)], [18, 18, 0, 0]), CLAY), (rnd([(56, 62), (201, 59), (202, 178), (55, 179)], 8), INK),
         ([(150, 60.1), (186, 59.3), (128, 178.5), (92, 178.7)], SHINE),
         (rnd([(8, 190), (248, 190), (246, 203), (226, 217), (30, 217), (10, 203)], [5, 5, 6, 12, 12, 6]), DEEP),
         (rnd([(98, 190), (158, 190), (152, 201), (104, 201)], [0, 0, 6, 6]), IVORY)]
    return 256, s

def dot(cx, cy, rx, ry): return oval(cx, cy, rx, ry, 0)[::8]  # 45 points are plenty at this size

# ---- one colour, nested contours filled even-odd. Line icons seen straight from the front: symmetric, centred, one line
# weight, outer edges on the 24 px grid, so they look level and sharp at list size. ----
LINE = 1.6
def frame(x1, y1, x2, y2, r): return [box(x1, y1, x2, y2, r), box(x1 + LINE, y1 + LINE, x2 - LINE, y2 - LINE, r - LINE)]
MONO = {
    'tower': frame(6, 2, 18, 22, 2.6) + [box(9, 5.6, 15, 7, .7), box(9, 8.6, 15, 10, .7), dot(12, 17.5, 1.25, 1.25)],
    'mini': frame(4, 5, 20, 19, 2.8) + [box(8, 8.6, 16, 10, .7), dot(12, 14.5, 1.25, 1.25)],
    'laptop': frame(4, 4, 20, 17, 2.4) + [rnd([(1.5, 18.4), (22.5, 18.4), (22.5, 19), (21.2, 20.2), (2.8, 20.2), (1.5, 19)], [.5, .5, .6, 1, 1, .6])],
}
NAMES = {'tower': tower, 'mini': mini, 'laptop': laptop}

def render(fn, size):
    g, sh = fn(size)
    n = size * max(4, 2048 // size); k = n / g
    img = Image.new('RGB', (n, n), CLAY); mask = Image.new('L', (n, n), 0)
    d, dm = ImageDraw.Draw(img), ImageDraw.Draw(mask)
    for pts, col in sh:
        p = [(x * k - .5, y * k - .5) for x, y in pts]
        d.polygon(p, fill=col); dm.polygon(p, fill=255)
    out = img.resize((size, size), Image.BOX); out.putalpha(mask.resize((size, size), Image.BOX))
    return out

def mono(name, size, col):
    from PIL import ImageChops
    n = size * max(4, 1024 // size); k = n / 24
    m = Image.new('1', (n, n), 0)
    for pts in MONO[name]:
        t = Image.new('1', (n, n), 0); ImageDraw.Draw(t).polygon([(x * k - .5, y * k - .5) for x, y in pts], fill=1)
        m = ImageChops.logical_xor(m, t)
    im = Image.new('RGB', (size, size), col); im.putalpha(m.convert('L').resize((size, size), Image.BOX))
    return im

def path(pts): return 'M' + 'L'.join('%.2f %.2f' % p for p in pts) + 'Z'

def main():
    os.makedirs(OUT, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    ims = {}
    for name, fn in NAMES.items():
        ims[name] = {z: render(fn, z) for z in sizes}
        base.write_ico(os.path.join(OUT, name + '.ico'), ims[name])
        render(fn, 512).save(os.path.join(OUT, name + '-512.png'))
        mono(name, 128, INK).save(os.path.join(OUT, name + '-mono-128.png'))
        with open(os.path.join(OUT, name + '.svg'), 'w', encoding='utf-8', newline='\n') as f:
            f.write('\n'.join(['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">'] + ['<path fill="#%02x%02x%02x" d="%s"/>' % (*c, path(p)) for p, c in fn(256)[1]] + ['</svg>', '']))
        with open(os.path.join(OUT, name + '-mono.svg'), 'w', encoding='utf-8', newline='\n') as f:
            f.write('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path fill="currentColor" fill-rule="evenodd" d="%s"/></svg>\n' % ''.join(path(p) for p in MONO[name]))

    # Preview: the set, real pixels, small sizes enlarged, and the avatars LanTalk's list would show.
    W, H = 1280, 660; pv = Image.new('RGB', (W, H * 2))
    for j, bg in enumerate([IVORY, (32, 32, 32)]):
        y0 = j * H; d = ImageDraw.Draw(pv); d.rectangle((0, y0, W, y0 + H), fill=bg)
        for i, name in enumerate(NAMES):
            x0 = 30 + i * 415
            im = ims[name][256]; pv.paste(im, (x0 + 60, y0 + 20), im)
            x = x0
            for z in (96, 64, 48, 32, 24, 20, 16):
                im = ims[name][z]; pv.paste(im, (x, y0 + 300 + (96 - z) // 2), im); x += z + 12
            x = x0
            for z in (16, 24, 32):
                e = ims[name][z].resize((z * (96 // z),) * 2, Image.NEAREST); pv.paste(e, (x, y0 + 415), e); x += 120
            x = x0                                # avatars: 36 px drawn with the theme's colours, at 1x and 2x
            for px, pos in ((36, 0), (72, 1)):
                for fill, col in (((61, 61, 58), IVORY), ((216, 210, 192), (107, 106, 100))):
                    a = Image.new('L', (px * 8,) * 2, 0); ImageDraw.Draw(a).ellipse((0, 0, px * 8 - 1, px * 8 - 1), fill=255)
                    a = a.resize((px, px), Image.BOX); pv.paste(Image.new('RGB', (px, px), fill), (x, y0 + 545 + (72 - px) // 2), a)
                    q = round(px * 2 / 3); m = mono(name, q, col); pv.paste(m, (x + (px - q) // 2, y0 + 545 + (72 - q) // 2), m); x += px + 14
    pv.save(os.path.join(OUT, 'preview.png'))

if __name__ == '__main__': main()

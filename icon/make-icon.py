# Draws the LanTalk icon: a hand-cut speech bubble holding three linked computers.
# Output: ../LanTalk.ico (16-256 px), LanTalk.svg, LanTalk-512.png, preview.png. Needs Pillow.
import io, math, os, struct
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
CLAY, IVORY, INK = (217, 119, 87), (250, 249, 245), (20, 20, 19)

def bez(p0, p1, p2, p3, n=40):
    for i in range(n + 1):
        t = i / n; u = 1 - t
        yield tuple(u*u*u*a + 3*u*u*t*b + 3*u*t*t*c + t*t*t*d for a, b, c, d in zip(p0, p1, p2, p3))

def blob(cx, cy, rx, ry, e, wob):
    """Superellipse with a slight low-frequency wobble, so it looks cut by hand rather than by a ruler."""
    out = []
    for i in range(360):
        a = math.radians(i); c, s = math.cos(a), math.sin(a)
        k = 1 + wob * (0.6 * math.sin(2 * a + 0.7) + 0.55 * math.sin(3 * a + 2.1) + 0.3 * math.sin(5 * a))
        out.append((cx + rx * k * math.copysign(abs(c) ** (2 / e), c), cy + ry * k * math.copysign(abs(s) ** (2 / e), s)))
    return out

def link(a, b, w, thin, bow):
    """Brush stroke from a to b: full width at the ends, thinner in the middle, slightly bowed."""
    (x1, y1), (x2, y2) = a, b
    dx, dy = x2 - x1, y2 - y1; n = math.hypot(dx, dy); nx, ny = -dy / n, dx / n
    L, R = [], []
    for i in range(41):
        t = i / 40; h = w / 2 * (thin + (1 - thin) * (2 * t - 1) ** 2); o = bow * 4 * t * (1 - t)
        x, y = x1 + dx * t + nx * o, y1 + dy * t + ny * o
        L.append((x + nx * h, y + ny * h)); R.append((x - nx * h, y - ny * h))
    return L + R[::-1]

# One shape list per size class, each on its own grid. Small sizes sit on the pixel grid and are bolder.
def shapes(size):
    if size <= 16:
        g, b = 16, blob(8, 6.6, 8, 6.6, 3.0, 0)
        t = [(2.6, 9), (2.6, 15.9), *bez((2.6, 15.9), (5, 15.6), (8, 14.6), (9.5, 12), 12)]
        n, w, thin, bow = [(8, 4, 2.0), (4.5, 8.5, 1.75), (11.5, 8.5, 1.75)], 1.05, 1, 0
    elif size <= 24:
        g, b = 24, blob(12, 10, 11.7, 9.8, 3.0, 0.006)
        t = [(4, 14), (3.6, 23.6), *bez((3.6, 23.6), (7.5, 23.2), (12, 21.5), (14, 18), 12)]
        n, w, thin, bow = [(12, 5.5, 2.6), (6.5, 13.5, 2.5), (17.5, 13.5, 2.5)], 2.1, .75, 0
    else:
        g, b = 256, blob(128, 107, 119, 98, 2.9, 0.014)
        t = [(46, 150), *bez((46, 160), (46, 196), (42, 220), (30, 240), 20), *bez((30, 240), (28, 247), (33, 250), (40, 249), 8), *bez((40, 249), (84, 243), (122, 226), (146, 196), 30)]
        n, w, thin, bow = [(130, 60, 25), (70, 144, 23), (188, 139, 24)], 25, .46, 7
    out = [(b, CLAY), (t, CLAY)]
    for i, j in ((0, 1), (1, 2), (2, 0)): out.append((link(n[i][:2], n[j][:2], w, thin, bow), IVORY))
    for i, (x, y, r) in enumerate(n):
        out.append((blob(x, y, r, r, 2, 0.02 if size > 24 else 0), INK if i == 0 else IVORY))
    return g, out

def render(size):
    g, sh = shapes(size)
    n = size * max(4, 2048 // size); k = n / g
    img = Image.new('RGB', (n, n), CLAY); mask = Image.new('L', (n, n), 0)
    d, dm = ImageDraw.Draw(img), ImageDraw.Draw(mask)
    for pts, col in sh:
        p = [(x * k, y * k) for x, y in pts]
        d.polygon(p, fill=col)
        if col == CLAY: dm.polygon(p, fill=255)
    img.putalpha(mask)                           # colour is solid everywhere, so edges don't darken
    return img.convert('RGBa').resize((size, size), Image.BOX).convert('RGBA')

def dib(im):
    """32-bit BMP icon entry (older Windows APIs and GDI+ don't all read PNG entries)."""
    w, h = im.size
    px = im.transpose(Image.FLIP_TOP_BOTTOM).tobytes('raw', 'BGRA')
    a = im.getchannel('A').transpose(Image.FLIP_TOP_BOTTOM).load()
    row = (w + 31) // 32 * 4
    m = bytearray(row * h)
    for y in range(h):
        for x in range(w):
            if a[x, y] == 0: m[y * row + x // 8] |= 0x80 >> x % 8
    return struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, len(px) + len(m), 0, 0, 0, 0) + px + bytes(m)

def write_ico(path, ims):
    sizes = sorted(ims)
    blobs = []
    for z in sizes:
        if z >= 64:
            f = io.BytesIO(); ims[z].save(f, 'PNG'); blobs.append(f.getvalue())
        else: blobs.append(dib(ims[z]))
    off = 6 + 16 * len(sizes)
    out = struct.pack('<HHH', 0, 1, len(sizes))
    for z, b in zip(sizes, blobs):
        out += struct.pack('<BBBBHHII', z % 256, z % 256, 0, 0, 1, 32, len(b), off); off += len(b)
    with open(path, 'wb') as f: f.write(out + b''.join(blobs))

def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    ims = {z: render(z) for z in sizes}
    write_ico(os.path.join(HERE, '..', 'LanTalk.ico'), ims)
    render(512).save(os.path.join(HERE, 'LanTalk-512.png'))
    svg = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">']
    for pts, col in shapes(256)[1]:
        svg.append('<path fill="#%02x%02x%02x" d="M%sZ"/>' % (*col, 'L'.join('%.1f %.1f' % p for p in pts)))
    with open(os.path.join(HERE, 'LanTalk.svg'), 'w', encoding='utf-8') as f: f.write('\n'.join(svg + ['</svg>', '']))

    # Preview: real pixels plus the small sizes enlarged, on ivory and dark.
    W = 1120; pv = Image.new('RGB', (W, 760))
    for j, bg in enumerate([IVORY, (32, 32, 32)]):
        ImageDraw.Draw(pv).rectangle((0, j * 380, W, j * 380 + 380), fill=bg)
        x = 20
        for z in (256, 128, 64, 48, 32, 24, 20, 16):
            pv.paste(ims[z], (x, j * 380 + 20 + (256 - z) // 2), ims[z]); x += z + 18
        x = 660
        for z in (16, 24, 32):
            e = ims[z].resize((z * (128 // z), z * (128 // z)), Image.NEAREST)
            pv.paste(e, (x, j * 380 + 235), e); x += 145
    pv.save(os.path.join(HERE, 'preview.png'))

if __name__ == '__main__': main()

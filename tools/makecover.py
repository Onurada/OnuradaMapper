"""Generate a 512x512 cover: saber-colour gradient, soft light streaks, a heart, the title and artist.
usage: makecover.py out.jpg "Title" "Artist" [AAAAAA BBBBBB] [mapper]"""
import sys, math, random
from PIL import Image, ImageDraw, ImageFilter, ImageFont

out, title, artist = sys.argv[1], sys.argv[2], sys.argv[3]
ca = sys.argv[4] if len(sys.argv) > 4 else 'FF4FA3'
cb = sys.argv[5] if len(sys.argv) > 5 else '4FC3FF'
mapper = sys.argv[6] if len(sys.argv) > 6 else ''
N = 512
A = tuple(int(ca[i:i + 2], 16) for i in (0, 2, 4)); B = tuple(int(cb[i:i + 2], 16) for i in (0, 2, 4))
rnd = random.Random(hash(title) & 0xFFFF)

# diagonal gradient, darkened towards the bottom for the text
img = Image.new('RGB', (N, N))
px = img.load()
for y in range(N):
    for x in range(N):
        t = (x + y) / (2 * N - 2)
        dark = 0.35 + 0.65 * (1 - y / N) ** 0.6
        px[x, y] = tuple(int((A[i] + (B[i] - A[i]) * t) * dark) for i in range(3))
# light streaks (lasers)
glow = Image.new('RGB', (N, N))
g = ImageDraw.Draw(glow)
for k in range(7):
    x0 = rnd.randint(-100, N + 100); ang = rnd.uniform(-0.6, 0.6)
    col = A if k % 2 else B
    g.line([(x0, N + 20), (x0 + math.sin(ang) * N * 1.5, -N * 0.5)], fill=tuple(min(255, c + 60) for c in col), width=rnd.randint(3, 9))
glow = glow.filter(ImageFilter.GaussianBlur(6))
img = Image.blend(img, Image.composite(glow, img, glow.convert('L')), 0.55)
# heart
hl = Image.new('L', (N, N), 0)
pts = []
for i in range(300):
    t = 2 * math.pi * i / 300
    x = 16 * math.sin(t) ** 3; y = 13 * math.cos(t) - 5 * math.cos(2 * t) - 2 * math.cos(3 * t) - math.cos(4 * t)
    pts.append((N * 0.5 + x * 6.2, N * 0.30 - y * 6.2))
ImageDraw.Draw(hl).polygon(pts, fill=255)
halo = hl.filter(ImageFilter.GaussianBlur(18))
img.paste((255, 255, 255), (0, 0), halo.point(lambda v: v // 3))
img.paste((255, 255, 255), (0, 0), hl.point(lambda v: v * 9 // 10))


def font(size):
    for f in ('C:/Windows/Fonts/ariblk.ttf', 'C:/Windows/Fonts/segoeuib.ttf', 'C:/Windows/Fonts/arialbd.ttf'):
        try: return ImageFont.truetype(f, size)
        except OSError: pass
    return ImageFont.load_default()


d = ImageDraw.Draw(img)


def fit(text, maxw, start):
    s = start
    while s > 18:
        f = font(s)
        if d.textlength(text, font=f) <= maxw: return f
        s -= 2
    return font(18)


T = title.upper()
lines = [T]
if d.textlength(T, font=font(64)) > N - 60 and ' ' in T:   # two lines for long titles
    words = T.split(); half = len(words) // 2 or 1
    lines = [' '.join(words[:half]), ' '.join(words[half:])]
fonts = [fit(ln, N - 60, 72 if len(lines) == 1 else 60) for ln in lines]
fa = fit(artist, N - 80, 30)
block = sum(f.size * 1.05 for f in fonts) + (fa.size + 10 if artist else 0)
y = N - 44 - block   # the text block sits above the "by" line
for ln, f in zip(lines, fonts):
    w = d.textlength(ln, font=f)
    d.text(((N - w) / 2 + 3, y + 3), ln, font=f, fill=(0, 0, 0))
    d.text(((N - w) / 2, y), ln, font=f, fill=(255, 255, 255))
    y += f.size * 1.05
if artist:
    w = d.textlength(artist, font=fa)
    d.text(((N - w) / 2 + 2, y + 10), artist, font=fa, fill=(0, 0, 0))
    d.text(((N - w) / 2, y + 8), artist, font=fa, fill=tuple(min(255, c + 110) for c in A))
if mapper:
    fm = font(16)
    d.text((N - d.textlength('by ' + mapper, font=fm) - 14, N - 28), 'by ' + mapper, font=fm, fill=(255, 255, 255))
img.save(out, quality=92)
print('cover', out)

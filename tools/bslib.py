"""Shared helpers: directions, v4 writers, wall-pixel font."""
import json, math, os, unicodedata


# ---------------- workspace / environment ----------------
# The desktop app runs these tools from its own folder against a separate workspace (BSM_ROOT).
def workspace(default=None):
    return os.environ.get('BSM_ROOT') or default or os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def songs_path(S):
    """songs.json: <workspace>/songs.json, or the older <workspace>/tools/songs.json if that one exists."""
    old = os.path.join(S, 'tools', 'songs.json')
    return old if os.path.exists(old) else os.path.join(S, 'songs.json')


def device():
    """'cuda' or 'cpu' (BSM_DEVICE), defaulting to cuda when torch can see a GPU."""
    d = os.environ.get('BSM_DEVICE', 'auto')
    if d != 'auto': return d
    try:
        import torch
        return 'cuda' if torch.cuda.is_available() else 'cpu'
    except Exception:
        return 'cpu'


def mapper_name(cfg=None):
    return (cfg or {}).get('mapper') or os.environ.get('BSM_MAPPER') or 'Onurada'

# ---------------- directions ----------------
DVEC = {0: (0, 1), 1: (0, -1), 2: (-1, 0), 3: (1, 0), 4: (-1, 1), 5: (1, 1), 6: (-1, -1), 7: (1, -1)}
MIRROR_D = {0: 0, 1: 1, 2: 3, 3: 2, 4: 5, 5: 4, 6: 7, 7: 6, 8: 8}
VFLIP_D = {0: 1, 1: 0, 2: 2, 3: 3, 4: 6, 5: 7, 6: 4, 7: 5, 8: 8}
VEC2D = {v: k for k, v in DVEC.items()}


def unit(d):
    x, y = DVEC[d]
    n = math.hypot(x, y)
    return (x / n, y / n)


def opposite(d):
    if d == 8: return 8
    x, y = DVEC[d]
    return VEC2D[(-x, -y)]


# ---------------- ASCII sanitising ----------------
TR = str.maketrans({'ı': 'i', 'İ': 'I', 'ş': 's', 'Ş': 'S', 'ğ': 'g', 'Ğ': 'G', 'ü': 'u', 'Ü': 'U', 'ö': 'o', 'Ö': 'O', 'ç': 'c', 'Ç': 'C', 'â': 'a', 'Â': 'A', 'î': 'i', 'û': 'u'})


def ascii_only(s):
    s = s.translate(TR)
    s = unicodedata.normalize('NFKD', s).encode('ascii', 'ignore').decode('ascii')
    return s


# ---------------- 5-row pixel font ----------------
_F = {
 'A': ['.#.', '#.#', '###', '#.#', '#.#'], 'B': ['##.', '#.#', '##.', '#.#', '##.'], 'C': ['.##', '#..', '#..', '#..', '.##'],
 'D': ['##.', '#.#', '#.#', '#.#', '##.'], 'E': ['###', '#..', '##.', '#..', '###'], 'F': ['###', '#..', '##.', '#..', '#..'],
 'G': ['.##.', '#...', '#.##', '#..#', '.##.'], 'H': ['#.#', '#.#', '###', '#.#', '#.#'], 'I': ['###', '.#.', '.#.', '.#.', '###'],
 'J': ['..#', '..#', '..#', '#.#', '.#.'], 'K': ['#..#', '#.#.', '##..', '#.#.', '#..#'], 'L': ['#..', '#..', '#..', '#..', '###'],
 'M': ['#...#', '##.##', '#.#.#', '#...#', '#...#'], 'N': ['#..#', '##.#', '#.##', '#..#', '#..#'], 'O': ['.#.', '#.#', '#.#', '#.#', '.#.'],
 'P': ['##.', '#.#', '##.', '#..', '#..'], 'Q': ['.##.', '#..#', '#..#', '#.#.', '.#.#'], 'R': ['##.', '#.#', '##.', '#.#', '#.#'],
 'S': ['.##', '#..', '.#.', '..#', '##.'], 'T': ['###', '.#.', '.#.', '.#.', '.#.'], 'U': ['#.#', '#.#', '#.#', '#.#', '###'],
 'V': ['#.#', '#.#', '#.#', '#.#', '.#.'], 'W': ['#...#', '#...#', '#.#.#', '##.##', '#...#'], 'X': ['#.#', '#.#', '.#.', '#.#', '#.#'],
 'Y': ['#.#', '#.#', '.#.', '.#.', '.#.'], 'Z': ['###', '..#', '.#.', '#..', '###'],
 '0': ['###', '#.#', '#.#', '#.#', '###'], '1': ['.#', '##', '.#', '.#', '.#'], '2': ['##.', '..#', '.#.', '#..', '###'],
 '3': ['##.', '..#', '.#.', '..#', '##.'], '4': ['#.#', '#.#', '###', '..#', '..#'], '5': ['###', '#..', '##.', '..#', '##.'],
 '6': ['.##', '#..', '###', '#.#', '###'], '7': ['###', '..#', '.#.', '.#.', '.#.'], '8': ['###', '#.#', '###', '#.#', '###'],
 '9': ['###', '#.#', '###', '..#', '##.'], '!': ['#', '#', '#', '.', '#'], '?': ['##.', '..#', '.#.', '...', '.#.'],
 '.': ['.', '.', '.', '.', '#'], ',': ['.', '.', '.', '#', '#'], "'": ['#', '#', '.', '.', '.'], '-': ['..', '..', '##', '..', '..'],
 '&': ['.#.', '#.#', '.#.', '#.#', '.##'], ' ': ['..', '..', '..', '..', '..'],
}
HEART = ['.##.##.', '#######', '.#####.', '..###..', '...#...']


def glyph(ch):
    return _F.get(ch.upper(), _F['?'] if ch.strip() else _F[' '])


def text_bitmap(text, spacing=1):
    """returns list of 5 strings (row 0 = top)"""
    rows = [''] * 5
    first = True
    for ch in ascii_only(text).upper():
        g = glyph(ch)
        for r in range(5):
            rows[r] += ('.' * spacing if not first else '') + g[r]
        first = False
    return rows


def bitmap_rects(rows):
    """greedy merge of '#' pixels into rectangles. rows[0] is the top row. returns (col, row_from_bottom, w, h)"""
    H = len(rows); W = max(len(r) for r in rows) if rows else 0
    grid = [[(c < len(rows[r]) and rows[r][c] == '#') for c in range(W)] for r in range(H)]
    used = [[False] * W for _ in range(H)]
    rects = []
    for c in range(W):
        for r in range(H):
            if grid[r][c] and not used[r][c]:
                # grow down (vertical run)
                h = 1
                while r + h < H and grid[r + h][c] and not used[r + h][c]: h += 1
                # grow right while the whole column segment is filled
                w = 1
                while c + w < W and all(grid[rr][c + w] and not used[rr][c + w] for rr in range(r, r + h)): w += 1
                for rr in range(r, r + h):
                    for cc in range(c, c + w): used[rr][cc] = True
                rects.append((c, H - (r + h), w, h))
    return rects


def text_points(text, n, height_m, seed=0, font='C:/Windows/Fonts/ariblk.ttf'):
    """n points (metres, centred, y up) evenly scattered over the filled shape of `text` in a heavy font"""
    import numpy as np
    from PIL import Image, ImageDraw, ImageFont
    f = ImageFont.truetype(font, 160)
    l, t, r, b = f.getbbox(text)
    img = Image.new('L', (r - l + 8, b - t + 8), 0)
    ImageDraw.Draw(img).text((4 - l, 4 - t), text, font=f, fill=255)
    a = np.array(img) > 127
    ys, xs = np.nonzero(a)
    rng = np.random.default_rng(seed)
    idx = rng.choice(len(xs), size=min(n, len(xs)), replace=False)
    s = height_m / a.shape[0]
    px = (xs[idx] + rng.random(len(idx)) - a.shape[1] / 2) * s
    py = (a.shape[0] / 2 - ys[idx] - rng.random(len(idx))) * s
    return [[round(float(x), 3), round(float(y), 3)] for x, y in zip(px, py)]


# ---------------- v4 writers ----------------
class Indexer:
    """deduplicating data array for v4 'xxxData' arrays"""
    def __init__(self):
        self.items = []; self.map = {}

    def idx(self, d):
        d = {k: v for k, v in d.items() if v not in (0, 0.0, None) or k in ('d',) and False}
        key = json.dumps(d, sort_keys=True)
        if key not in self.map:
            self.map[key] = len(self.items); self.items.append(d)
        return self.map[key]


def r4(x):
    return round(float(x), 4)


def write_beatmap_v4(path, notes, bombs, obstacles, arcs, chains, njs_events=None):
    """notes: dict(b,x,y,c,d,a); bombs: dict(b,x,y); obstacles: dict(b,d,x,y,w,h);
    arcs: dict(c,hb,hx,hy,hd,hm,tb,tx,ty,td,tm,m); chains: dict(c,hb,hx,hy,hd,tb,tx,ty,sc,s)"""
    ND, BD, OD, AD, CD = Indexer(), Indexer(), Indexer(), Indexer(), Indexer()
    out = {'version': '4.0.0', 'colorNotes': [], 'bombNotes': [], 'obstacles': [], 'chains': [], 'arcs': []}
    for n in sorted(notes, key=lambda n: (n['b'], n['c'], n['x'], n['y'])):
        i = ND.idx({'x': n['x'], 'y': n['y'], 'c': n['c'], 'd': n['d'], 'a': n.get('a', 0)})
        out['colorNotes'].append({'b': r4(n['b']), 'r': 0, 'i': i})
    for n in sorted(bombs, key=lambda n: n['b']):
        out['bombNotes'].append({'b': r4(n['b']), 'r': 0, 'i': BD.idx({'x': n['x'], 'y': n['y']})})
    for o in sorted(obstacles, key=lambda o: o['b']):
        out['obstacles'].append({'b': r4(o['b']), 'r': 0, 'i': OD.idx({'d': r4(o['d']), 'x': o['x'], 'y': o['y'], 'w': o['w'], 'h': o['h']})})
    for a in sorted(arcs, key=lambda a: a['hb']):
        hi = ND.idx({'x': a['hx'], 'y': a['hy'], 'c': a['c'], 'd': a['hd']})
        ti = ND.idx({'x': a['tx'], 'y': a['ty'], 'c': a['c'], 'd': a['td']})
        ai = AD.idx({'m': r4(a.get('hm', 1.0)), 'tm': r4(a.get('tm', 1.0)), 'a': a.get('m', 0)})
        out['arcs'].append({'hb': r4(a['hb']), 'tb': r4(a['tb']), 'hr': 0, 'tr': 0, 'hi': hi, 'ti': ti, 'ai': ai})
    for c in sorted(chains, key=lambda c: c['hb']):
        i = ND.idx({'x': c['hx'], 'y': c['hy'], 'c': c['c'], 'd': c['hd']})
        ci = CD.idx({'tx': c['tx'], 'ty': c['ty'], 'c': c['sc'], 's': r4(c['s'])})
        out['chains'].append({'hb': r4(c['hb']), 'tb': r4(c['tb']), 'hr': 0, 'tr': 0, 'i': i, 'ci': ci})
    out['colorNotesData'] = ND.items; out['bombNotesData'] = BD.items; out['obstaclesData'] = OD.items
    out['chainsData'] = CD.items; out['arcsData'] = AD.items
    # njs_events: dict(b, d=relative NJS, e=easing into this value from the previous event, -1 = instant)
    JD = Indexer()
    out['njsEvents'] = [{'b': r4(e['b']), 'i': JD.idx({'d': r4(e.get('d', 0.0)), 'e': e.get('e', -1)})} for e in sorted(njs_events or [], key=lambda e: e['b'])]
    out['njsEventData'] = JD.items
    # data entries: keep zero-valued keys out but 'd' of obstacles must stay meaningful
    json.dump(out, open(path, 'w'), separators=(',', ':'))
    return out

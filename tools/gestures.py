"""Lyric gestures: a word in the song triggers a hand-made move.
Each gesture returns notes / arcs / chains / bombs relative to its bar (beats), starting at offset o.
Notes are (beat, x, y, colour, direction, angle); directions 0 up 1 down 2 left 3 right 4 UL 5 UR 6 DL 7 DR 8 dot.
Every gesture enters with a down (or horizontal) swing per hand and leaves on an up swing, so they chain safely."""
R, B = 0, 1
SUPPORTED = ('Hard', 'Expert', 'ExpertPlus')


def _stream(o, step, length, pos_r=(1, 0), pos_b=(2, 0)):
    """alternating hands, each hand down/up in place; per-hand count kept even so it ends on an up swing"""
    n = int(length / step + 1e-6)
    n -= n % 4
    out = []
    for k in range(n):
        c = k % 2; h = k // 2
        d = 1 if h % 2 == 0 else 0
        x, y = (pos_r if c == R else pos_b)
        out.append((o + k * step, x, y if d == 1 else y, c, d, 0))
    return out


def make(name, D, o):
    """-> dict(notes, arcs, chains, bombs, length) or None if the difficulty has no version of it"""
    if D not in SUPPORTED: return None
    hard = D == 'Hard'
    g = dict(notes=[], arcs=[], chains=[], bombs=[])
    if name == 'zor':            # heavy double slam with bombs flanking the top corners
        g['notes'] = [(o, 1, 2, R, 1, 0), (o, 2, 2, B, 1, 0), (o + 1, 1, 0, R, 0, 0), (o + 1, 2, 0, B, 0, 0)]
        if not hard: g['notes'] += [(o + 2, 1, 1, R, 1, 0), (o + 2, 2, 1, B, 1, 0), (o + 3, 0, 0, R, 0, 0), (o + 3, 3, 0, B, 0, 0)]
        g['bombs'] = [(o + 0.5, 0, 2), (o + 0.5, 3, 2)] + ([] if hard else [(o + 2.5, 0, 2), (o + 2.5, 3, 2)])
    elif name == 'gokyuzu':      # reach for the sky: everything climbs to the top row
        g['notes'] = [(o, 1, 0, R, 1, 0), (o, 2, 0, B, 1, 0), (o + 1, 1, 1, R, 0, 0), (o + 1, 2, 1, B, 0, 0)]
        if not hard: g['notes'] += [(o + 2, 0, 2, R, 1, 0), (o + 2, 3, 2, B, 1, 0)]
        tail = o + (3 if not hard else 2.5)
        hb = o + 2 if not hard else o + 1
        # final up-swing as an arc that keeps rising out to the top corners
        g['notes'] += [(tail - 0.5, 1, 1, R, 0, 0), (tail - 0.5, 2, 1, B, 0, 0)] if not hard else []
        g['arcs'] = [dict(c=R, hb=tail - 0.5 if not hard else hb, hx=1, hy=1, hd=0, hm=1.0, tb=tail, tx=0, ty=2, td=0, tm=1.0, m=0),
                     dict(c=B, hb=tail - 0.5 if not hard else hb, hx=2, hy=1, hd=0, hm=1.0, tb=tail, tx=3, ty=2, td=0, tm=1.0, m=0)]
    elif name == 'yok':          # "no": both hands shake left-right together, then look up
        step = 1.0 if hard else 0.5
        g['notes'] = []
        for k in range(4):
            left = k % 2 == 0
            g['notes'] += [(o + k * step, 1 if left else 0, 1, R, 2 if left else 3, 0), (o + k * step, 3 if left else 2, 1, B, 2 if left else 3, 0)]
        end = o + 4 * step
        if end <= 4: g['notes'] += [(min(end, 3.75), 1, 0, R, 0, 0), (min(end, 3.75), 2, 0, B, 0, 0)]
    elif name == 'don':          # "turn": windmill, the swing axis rotates 45 degrees each pair, hands alternate
        step = 1.0
        rd = [(1, 2, 1), (0, 0, 0), (1, 1, 6), (0, 1, 5)]   # (x, y, dir) red: down, up, down-left, up-right
        bd = [(2, 2, 1), (3, 0, 0), (2, 1, 7), (3, 1, 4)]   # mirrored blue
        k_ = 2 if hard else 4
        for k in range(k_):
            g['notes'].append((o + k * step, rd[k][0], rd[k][1], R, rd[k][2], 0))
            if o + k * step + 0.5 < 4: g['notes'].append((o + k * step + 0.5, bd[k][0], bd[k][1], B, bd[k][2], 0))
    elif name == 'durma':        # "don't stop": an unbroken stream to the end of the bar
        step = {'ExpertPlus': 0.25, 'Expert': 0.5, 'Hard': 1.0}[D]
        g['notes'] = _stream(o, step, 4 - o)
    elif name == 'tutma':        # "don't hold on": both hands let go upward on wide arcs
        g['notes'] = [(o, 1, 2, R, 1, 0), (o, 2, 2, B, 1, 0), (o + 1, 1, 0, R, 0, 0), (o + 1, 2, 0, B, 0, 0)]
        g['arcs'] = [dict(c=R, hb=o + 1, hx=1, hy=0, hd=0, hm=1.0, tb=o + 2.5, tx=0, ty=2, td=0, tm=1.2, m=0),
                     dict(c=B, hb=o + 1, hx=2, hy=0, hd=0, hm=1.0, tb=o + 2.5, tx=3, ty=2, td=0, tm=1.2, m=0)]
    elif name == 'gozunu':       # "close your eyes": lights go out, only soft dots in the middle
        n = 2 if hard else 4
        for k in range(n):   # the last pair is an up arrow so the swing direction stays known after the dots
            d = 0 if k == n - 1 else 8
            g['notes'] += [(o + k * (4 / n), 1, 1, R, d, 0), (o + k * (4 / n), 2, 1, B, d, 0)]
    else:
        return None
    g['notes'] = [n for n in g['notes'] if n[0] < 4 - 1e-6]
    g['bombs'] = [b for b in g['bombs'] if b[0] < 4 - 1e-6]
    return g

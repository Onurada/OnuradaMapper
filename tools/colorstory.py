"""Six-colour light story on top of a transplanted lightshow.

Palette: normal = (c0, c1, cW), boost = (c0b, c1b, cWb). The game forces W to white for custom levels; the
OnuradaLyrics mod restores cW / cWb from lyrics.json["wcolors"], so all six colours are usable.

Segment modes (song seconds -> beats via tbeat):
  intro   normal palette, everything colour 1, dimmed
  verse   normal palette, donor colours
  verse2  normal palette, colours 1 and 2 swapped (pink / yellow lead)
  calm    normal palette, dimmed, no strobes
  build   boost palette, donor colours
  break   near blackout in colour 2 (tension before a drop)
  chorus  rainbow chase: palette flips every bar, each light group steps through the colours per bar,
          plus two groups driven by the real kick drum and two by the snare
  hype    boost palette with the kick / snare layer
  dream   boost palette, everything colour 1, dim, no strobes
  outro   all groups one colour, cycling through the six colours bar by bar
Flashes: a rainbow strobe across every colour group (all six colours in under a beat).
"""
import numpy as np

ALL = {'f': 1, 'p': 1}                 # index filter: every light of a group
BOX = {'d': 1, 't': 1}                 # plain event box
BOX_WAVE = {'w': 0.3, 'd': 1, 't': 1}  # ripple across the group
CYCLE6 = [(0, 0), (1, 0), (0, 2), (1, 2), (0, 1), (1, 1)]   # (boost, colour): pink red yellow green blue purple


def _ev(lb, i): return lb.evs[1].items[i]


def _set(lb, item, **kw):
    """copy of a light colour event entry with fields replaced (None removes)"""
    d = dict(_ev(lb, item['i']))
    for k, v in kw.items():
        if v is None: d.pop(k, None)
        else: d[k] = v
    out = {'i': lb.evs[1].add(d)}
    if item.get('b'): out['b'] = item['b']
    return out


def _scale(lb, item, k, strobe=True):
    d = _ev(lb, item['i'])
    kw = {'b': round(d['b'] * k, 3) if d.get('b') else None}
    if not strobe: kw.update(f=None, sb=None, sf=None)
    return _set(lb, item, **kw)


def _group(lb, b, g, events, box=BOX):
    """events: list of (offset, dict)"""
    f = lb.filt.add(ALL); bx = lb.boxes[1].add(box)
    ll = [dict({'i': lb.evs[1].add(d)}, **({'b': round(o, 4)} if o else {})) for o, d in events]
    lb.groups.append({'b': round(b, 4), 'g': g, 't': 1, 'e': [{'f': f, 'e': bx, 'l': ll}]})


def _restore(lb, groups, b0, b1):
    """re-send each group's latest event before b0 at b1 (state after a flash / reactive layer)"""
    for g in groups:
        last = None
        for ev in lb.groups:
            if ev['t'] == 1 and ev['g'] == g and ev['b'] < b0 - 1e-4 and (last is None or ev['b'] >= last['b']): last = ev
        if last is not None: lb.groups.append(dict(last, b=round(b1, 4)))


def onsets(x, period, t0, thr, min_gap):
    """peak times (song seconds) of a 16-per-beat feature"""
    out, last = [], -9.0
    for j in range(1, len(x) - 1):
        if x[j] > thr and x[j] >= x[j - 1] and x[j] >= x[j + 1]:
            t = t0 + j * period / 16
            if t - last >= min_gap: out.append(t); last = t
    return out


def apply(lb, story, tbeat, end_beat, kicks=(), snares=()):
    """story: dict(segments=[[t0, t1, mode], ...], flashes=[t, ...]). Rewrites lb in place."""
    color_groups = sorted({g['g'] for g in lb.groups if g['t'] == 1})
    if not color_groups: return
    cnt = {g: sum(1 for e in lb.groups if e['t'] == 1 and e['g'] == g) for g in color_groups}
    busiest = sorted(color_groups, key=lambda g: -cnt[g])
    kick_g, snare_g = busiest[:2], busiest[2:4]
    rank = {g: i for i, g in enumerate(color_groups)}
    segs = [(float(round(tbeat(a))), float(round(tbeat(b))), m) for a, b, m in story['segments']]   # snapped to whole beats
    boost = []   # (beat, on)

    def seg_at(b):
        if b < segs[0][0]: return segs[0]   # lead-in state before the first segment
        for a, e, m in segs:
            if a - 1e-4 <= b < e - 1e-4: return a, e, m
        return None

    # ---- recolour / rescale the transplanted colour events ----
    react = [(a, e) for a, e, m in segs if m in ('chorus', 'hype')]
    keep = []
    for ev in lb.groups:
        if ev['t'] != 1:
            keep.append(ev); continue
        s = seg_at(ev['b'])
        if s is None:
            keep.append(ev); continue
        a, e, m = s
        if m in ('chorus', 'hype') and ev['g'] in kick_g + snare_g: continue   # replaced by the drum layer
        k = int((ev['b'] - a) // 4)
        new_e = []
        for box in ev['e']:
            ll = []
            for it in box['l']:
                c = _ev(lb, it['i']).get('c', 0)
                if c == -1: ll.append(it); continue
                if m == 'intro': it = _scale(lb, _set(lb, it, c=1), 0.7)
                elif m == 'verse2': it = _set(lb, it, c={0: 0, 1: 2, 2: 1}[c] or None)
                elif m == 'calm': it = _scale(lb, it, 0.6, strobe=False)
                elif m == 'break': it = _scale(lb, _set(lb, it, c=2), 0.15, strobe=False)
                elif m == 'chorus': it = _set(lb, it, c=((rank[ev['g']] + k) % 3) or None)
                elif m == 'dream': it = _scale(lb, _set(lb, it, c=1), 0.35, strobe=False)
                elif m == 'outro': it = _set(lb, it, c=CYCLE6[k % 6][1] or None)
                ll.append(it)
            new_e.append(dict(box, l=ll))
        keep.append(dict(ev, e=new_e))
    lb.groups = keep

    # ---- palette schedule ----
    for a, e, m in segs:
        if m in ('intro', 'verse', 'verse2', 'calm', 'break'): boost.append((a, 0))
        elif m in ('build', 'hype', 'dream'): boost.append((a, 1))
        elif m == 'chorus':
            k = 0
            while a + 4 * k < e - 1e-4: boost.append((a + 4 * k, k % 2)); k += 1
        elif m == 'outro':
            k = 0
            while a + 4 * k < min(e, end_beat): boost.append((a + 4 * k, CYCLE6[k % 6][0])); k += 1

    # ---- intro: slow blue breathing so the title card has a glow behind it ----
    for a, e, m in segs:
        if m != 'intro': continue
        k = 0
        while a + 4 * k < e - 1e-4:
            for g in color_groups:
                _group(lb, a + 4 * k, g, [(0, {'c': 1, 'b': 0.7 if k % 2 else 0.25})], BOX_WAVE)
            k += 1
    # ---- outro: every group crossfades through the six colours, one per bar, then fades out ----
    for a, e, m in segs:
        if m != 'outro': continue
        e = min(e, end_beat)
        lb.groups = [ev for ev in lb.groups if not (ev['t'] == 1 and a - 1e-4 <= ev['b'] < e)]
        k = 0
        while a + 4 * k < e - 2:
            for g in color_groups:
                _group(lb, a + 4 * k, g, [(0, {'c': CYCLE6[k % 6][1], 'b': 1.1 - 0.3 * (k % 2)})], BOX_WAVE)
            k += 1
        for g in color_groups:
            _group(lb, e - 0.5, g, [(0, {'c': CYCLE6[(k - 1) % 6][1]})])

    # ---- drum layer: kicks ripple through two groups, snares snap two others ----
    fl = [round(tbeat(t) * 4) / 4 for t in story.get('flashes', [])]
    def free(b): return not any(f0 - 0.05 <= b < f0 + 0.8 for f0 in fl)
    for a, e, m in segs:
        if m not in ('chorus', 'hype'): continue
        kb = [tbeat(t) for t in kicks if a <= tbeat(t) < e - 0.25 and free(tbeat(t))]
        for n, b in enumerate(kb):
            c = (n % 3) if m == 'chorus' else (n % 2) * 2
            g = kick_g[n % len(kick_g)]
            _group(lb, b, g, [(0, {'e': -1, 'c': c, 'b': 1.5}), (0.45, {'c': c})], BOX_WAVE)
        for n, b in enumerate(tbeat(t) for t in snares if a <= tbeat(t) < e - 0.25 and free(tbeat(t))):
            for g in snare_g:
                _group(lb, b, g, [(0, {'e': -1, 'c': 2, 'b': 1.8}), (0.3, {'c': 2})])
        _restore(lb, kick_g + snare_g, a, e)

    # ---- rainbow flashes ----
    for b0 in fl:
        # state each group should return to: its latest event up to the end of the flash, then clear the window
        last = {}
        for ev in lb.groups:
            if ev['t'] == 1 and ev['b'] < b0 + 0.75 - 1e-4 and (ev['g'] not in last or ev['b'] >= last[ev['g']]['b']): last[ev['g']] = ev
        lb.groups = [ev for ev in lb.groups if not (ev['t'] == 1 and b0 - 1e-4 <= ev['b'] < b0 + 0.75 - 1e-4)]
        prev_on = 0
        for a, on in boost:
            if a <= b0 + 1e-4: prev_on = on
        boost = [(a, on) for a, on in boost if not (b0 - 1e-4 <= a < b0 + 0.75)]
        for q, (on, c) in enumerate(CYCLE6):
            boost.append((b0 + q * 0.125, on))
            for g in color_groups:
                _group(lb, b0 + q * 0.125, g, [(0, {'e': -1, 'c': c, 'b': 1.8})])
        s = seg_at(b0 + 0.75)
        boost.append((b0 + 0.75, prev_on if s is None or s[2] != 'chorus' else 0))
        for ev in last.values(): lb.groups.append(dict(ev, b=round(b0 + 0.75, 4)))

    # ---- blackouts (lyric gesture "close your eyes"): every colour group off, then back to its previous state ----
    for t0, t1 in story.get('blackouts', []):
        b0, b1 = round(tbeat(t0) * 4) / 4, round(tbeat(t1) * 4) / 4
        last = {}
        for ev in lb.groups:
            if ev['t'] == 1 and ev['b'] < b1 - 1e-4 and (ev['g'] not in last or ev['b'] >= last[ev['g']]['b']): last[ev['g']] = ev
        lb.groups = [ev for ev in lb.groups if not (ev['t'] == 1 and b0 - 1e-4 <= ev['b'] < b1 - 1e-4)]
        for g in color_groups: _group(lb, b0, g, [(0, {'e': -1})])
        for ev in last.values(): lb.groups.append(dict(ev, b=round(b1, 4)))
    boost.sort()
    lb.boost = [{'b': round(b, 4), 'i': lb.cbd.add({'b': 1} if on else {})} for b, on in boost if b <= end_beat]
    lb.groups = [g for g in lb.groups if g['b'] <= end_beat]
    return dict(kick_groups=kick_g, snare_groups=snare_g, kicks=len(kicks), snares=len(snares))

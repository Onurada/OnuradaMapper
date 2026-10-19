"""Data-driven mapper: stitches official-map bar windows to fit a new song's rhythm with flow constraints."""
import math, numpy as np
from bslib import DVEC, MIRROR_D, unit, opposite

NPS = {'Easy': 1.1, 'Normal': 1.75, 'Hard': 2.7, 'Expert': 3.8, 'ExpertPlus': 5.0}
LAMBDA = {'Easy': 0.55, 'Normal': 0.45, 'Hard': 0.33, 'Expert': 0.24, 'ExpertPlus': 0.17}
MINGAP = {'Easy': 0.5, 'Normal': 0.34, 'Hard': 0.22, 'Expert': 0.15, 'ExpertPlus': 0.11}
SUB = 16
SLOTS = 64
INF = 1e9


def corpus_phase(m):
    """beat phase (0..3) where the corpus map's bars start: phase with most two-hand hits / arc heads."""
    sc = np.zeros(4)
    times = {}
    for n in m['notes']:
        times.setdefault(round(n[0], 3), set()).add(n[3])
    for t, cs in times.items():
        if abs(t - round(t)) < 1e-3:
            sc[int(round(t)) % 4] += 2.0 if len(cs) == 2 else 0.6
    for a in m['arcs']:
        if abs(a['hb'] - round(a['hb'])) < 1e-3: sc[int(round(a['hb'])) % 4] += 0.5
    return int(np.argmax(sc))


class Window:
    __slots__ = ('mid', 's', 'k', 'notes', 'bombs', 'obst', 'arcs', 'chains', 'occ', 'n', 'states', 'key', 'level', 'special', 'env')

    def __init__(self):
        self.special = None


def hand_states(notes, arcs, chains):
    """entry/exit swing state per hand: (rb, x, y, d_effective) using effective direction for dots"""
    ent = {0: None, 1: None}; ex = {0: None, 1: None}
    for c in (0, 1):
        hn = sorted([n for n in notes if n[3] == c], key=lambda n: n[0])
        sw = []
        lastt = None
        for n in hn:
            if lastt is not None and abs(n[0] - lastt) < 1e-3:  # stacked note same swing
                continue
            sw.append(n); lastt = n[0]
        # leading dots: infer backwards from the first directed swing (they alternate)
        f = next((i for i, n in enumerate(sw) if n[4] != 8), None)
        effs = [8] * len(sw)
        if f is not None:
            effs[f] = sw[f][4]
            for i in range(f - 1, -1, -1): effs[i] = opposite(effs[i + 1])
            for i in range(f + 1, len(sw)):
                effs[i] = sw[i][4] if sw[i][4] != 8 else opposite(effs[i - 1])
        seq = [(n[0], n[1], n[2], e) for n, e in zip(sw, effs)]
        if seq:
            ent[c] = seq[0]
            ex[c] = seq[-1]
        for a in arcs:
            if a['c'] == c and (ex[c] is None or a['tb'] > ex[c][0] + 1e-3):
                ex[c] = (a['tb'], a['tx'], a['ty'], a['td'])
        for ch in chains:
            if ch['c'] == c and (ex[c] is None or ch['tb'] > ex[c][0] + 1e-3):
                ex[c] = (ch['tb'], ch['tx'], ch['ty'], ch['hd'])
    return ent, ex


def mirror_state(st):
    if st is None: return None
    return (st[0], 3 - st[1], st[2], MIRROR_D[st[3]])


def build_windows(corpus, diffs, song_bpm, extra_filter=None):
    wins = []
    for mid, m in enumerate(corpus):
        if m['diff'] not in diffs: continue
        if extra_filter and not extra_filter(m): continue
        bpm = abs(m['bpm'])
        for k in (0.5, 1.0, 2.0):
            ratio = bpm / (k * song_bpm)
            if not (0.86 <= ratio <= 1.16): continue
            L = 4 * k
            ph = corpus_phase(m)
            notes = m['notes']
            if not notes: continue
            last = notes[-1][0]
            starts = np.arange(ph, last + 1e-6, L)
            nt = np.array([n[0] for n in notes])
            for s in starts:
                e = s + L
                i0, i1 = np.searchsorted(nt, s - 1e-4), np.searchsorted(nt, e - 1e-4)
                wn = [((n[0] - s) / k, n[1], n[2], n[3], n[4], n[5]) for n in notes[i0:i1]]
                if not wn: continue
                if any(not (0 <= n[1] <= 3 and 0 <= n[2] <= 2) for n in wn): continue
                arcs = []
                for a in m['arcs']:
                    if s - 1e-4 <= a['hb'] < e - 1e-4:
                        if a['tb'] > e + 0.0001 * L: continue
                        b = dict(a); b['hb'] = (a['hb'] - s) / k; b['tb'] = (a['tb'] - s) / k
                        arcs.append(b)
                chains = []
                for ch in m['chains']:
                    if s - 1e-4 <= ch['hb'] < e - 1e-4 and ch['tb'] < e - 1e-4:
                        b = dict(ch); b['hb'] = (ch['hb'] - s) / k; b['tb'] = (ch['tb'] - s) / k
                        chains.append(b)
                bombs = [((b[0] - s) / k, b[1], b[2]) for b in m['bombs'] if s - 1e-4 <= b[0] < e - 1e-4 and 0 <= b[1] <= 3 and 0 <= b[2] <= 2]
                obst = [((o[0] - s) / k, o[1] / k, o[2], o[3], o[4], o[5]) for o in m['obst']
                        if s - 1e-4 <= o[0] < e - 1e-4 and o[0] + o[1] <= e + 1e-4 and o[2] >= 0 and o[2] + o[4] <= 4 and o[4] >= 1]
                w = Window()
                w.mid, w.s, w.k, w.level = mid, s, k, m['level']
                w.env = m.get('env')
                w.notes, w.bombs, w.obst, w.arcs, w.chains = wn, bombs, obst, arcs, chains
                occ = np.zeros(SLOTS, np.float32)
                for t in set(round(n[0], 4) for n in wn):
                    occ[min(SLOTS - 1, int(round(t * SUB)))] = 1
                w.occ = occ
                w.n = len(wn)
                ent, ex = hand_states(wn, arcs, chains)
                w.states = [(ent, ex), ({0: mirror_state(ent[1]), 1: mirror_state(ent[0])}, {0: mirror_state(ex[1]), 1: mirror_state(ex[0])})]
                w.key = (mid, round(s, 3), k)
                wins.append(w)
    return wins


def make_special(notes, arcs, kind):
    w = Window()
    w.mid, w.s, w.k, w.level = -1, 0, 1, kind
    w.notes = notes; w.arcs = arcs; w.chains = []; w.bombs = []; w.obst = []
    occ = np.zeros(SLOTS, np.float32)
    for t in set(round(n[0], 4) for n in notes): occ[min(SLOTS - 1, int(round(t * SUB)))] = 1
    w.occ = occ; w.n = len(notes)
    ent, ex = hand_states(notes, arcs, [])
    w.states = [(ent, ex), (ent, ex)]
    w.key = ('special', kind)
    w.special = kind
    return w


def heart_window(arc_beats):
    """2BeLoved-style heart: down-swing at bottom centre, then both hands out to top corners and arcs back to centre."""
    notes = [(0.0, 1, 0, 0, 1, 0), (0.0, 2, 0, 1, 1, 0), (1.0, 0, 2, 0, 4, 0), (1.0, 3, 2, 1, 5, 0)]
    tb = 1.0 + arc_beats
    arcs = [dict(c=0, hb=1.0, hx=0, hy=2, hd=4, hm=1.0, tb=tb, tx=1, ty=0, td=7, tm=1.0, m=0),
            dict(c=1, hb=1.0, hx=3, hy=2, hd=5, hm=1.0, tb=tb, tx=2, ty=0, td=6, tm=1.0, m=0)]
    return make_special(notes, arcs, 'heart')


def flow_cost(prev, nxt, gap_s, D):
    """prev/nxt: (rb,x,y,d). gap_s: seconds between them"""
    if prev is None or nxt is None: return 0.0
    if gap_s < MINGAP[D]: return INF
    pd, nd = prev[3], nxt[3]
    c = 0.0
    if pd != 8 and nd != 8:
        pu, nu = unit(pd), unit(nd)
        dot = pu[0] * nu[0] + pu[1] * nu[1]
        if dot > 0.5:
            if gap_s < 0.9: return INF
            c += 7.0 if gap_s < 1.4 else 1.5
        elif dot > -0.2: c += 1.5 if gap_s < 0.7 else 0.4
        ex, ey = prev[1] + 0.6 * pu[0], prev[2] + 0.6 * pu[1]
        sx, sy = nxt[1] - 0.6 * nu[0], nxt[2] - 0.6 * nu[1]
    else:
        ex, ey, sx, sy = prev[1], prev[2], nxt[1], nxt[2]
    dist = math.hypot(ex - sx, ey - sy)
    c += max(0.0, dist - 1.2) * 0.9 / max(gap_s, 0.25)
    # hand side preference: red left / blue right
    return c


def side_cost(st, c):
    if st is None: return 0
    x = st[1]
    return 0.35 * (x - 1.5 if c == 0 else 1.5 - x) if (c == 0 and x >= 3) or (c == 1 and x <= 0) else 0.0


GENRE = {}
for _e in ['BritneyEnvironment', 'LizzoEnvironment', 'GagaEnvironment', 'TheWeekndEnvironment', 'BTSEnvironment', 'BillieEnvironment', 'KDAEnvironment']: GENRE[_e] = 'pop'
for _e in ['HipHopEnvironment', 'HipHopMixtape2Environment', 'TimbalandEnvironment', 'InterscopeEnvironment']: GENRE[_e] = 'hiphop'
for _e in ['MetallicaEnvironment', 'LinkinParkEnvironment', 'LinkinPark2Environment', 'TheRollingStonesEnvironment', 'QueenEnvironment', 'GreenDayEnvironment',
           'GreenDayGrenadeEnvironment', 'PanicEnvironment', 'Panic2Environment', 'RockMixtapeEnvironment', 'PyroEnvironment', 'DragonsEnvironment', 'Dragons2Environment', 'ColdplayEnvironment']: GENRE[_e] = 'rock'
for _e in ['MonstercatEnvironment', 'Monstercat2Environment', 'EDMEnvironment', 'SkrillexEnvironment', 'DaftPunkEnvironment', 'ProdigyEnvironment']: GENRE[_e] = 'edm'


def style_bonus(w, style):
    if not style: return 0.0
    return float(style.get(GENRE.get(w.env, 'ost'), style.get('other', 0.0)))


class Hyp:
    __slots__ = ('score', 'choice', 'last', 'used', 'mapsrun')

    def __init__(self, score, choice, last, used):
        self.score, self.choice, self.last, self.used = score, choice, last, used


def plan(D, wins, P, targets, bar_s, rep, specials=None, K=48, B=28, seed=0, forced_empty=None, style=None, pinned=None):
    """P: (nbars, 64) salience. targets: notes per bar. bar_s: seconds per bar. rep[i]: earlier bar index or -1.
    specials: dict bar -> Window to strongly prefer. pinned: dict bar -> (window or None, tf) that bar must use.
    returns list of (window or None, tf)"""
    nb = P.shape[0]
    occ = np.stack([w.occ for w in wins]) if wins else np.zeros((0, SLOTS))
    ncount = np.array([w.n for w in wins], np.float32)
    lam = LAMBDA[D]
    # dilated occupancy for coverage
    occd = np.maximum(occ, np.maximum(np.roll(occ, 1, 1), np.roll(occ, -1, 1)))
    slot_s = bar_s / SLOTS
    sbonus = np.array([style_bonus(w, style) for w in wins], np.float32) if wins else 0.0
    beams = [Hyp(0.0, [], {0: None, 1: None}, {})]
    for i in range(nb):
        Pi = P[i]
        strong = (Pi > 0.75).astype(np.float32)
        tgt = targets[i]
        if pinned is not None and i in pinned:
            cand = [(pinned[i][0], pinned[i][1], 0.0)]
        elif forced_empty is not None and forced_empty[i]:
            cand = [(None, 0, 0.0)]
        else:
            hit = occ @ (Pi - lam)
            uncovered = (strong[None, :] * (1 - occd)).sum(1)
            dens = -1.2 * ((ncount - tgt) / max(tgt, 1.5)) ** 2 * max(tgt, 1.5) * 0.5
            local = hit - 0.45 * uncovered * (0.5 + lam) + dens + sbonus
            if seed:   # re-roll: jitter the window ranking per bar (same jitter in every pass for this seed)
                local = local + np.random.default_rng(seed * 100003 + i).normal(0, 0.35 * (float(np.std(local)) + 1e-3), len(local))
            top = np.argsort(-local)[:K]
            cand = [(wins[j], tf, float(local[j])) for j in top for tf in (0, 1)]
            empty_local = -float((np.clip(Pi - lam, 0, None)).sum()) * 0.8 - (1.0 if tgt > 3 else 0.0)
            cand.append((None, 0, empty_local))
            if specials and i in specials:
                sw = specials[i]
                cand.append((sw, 0, float(sw.occ @ (Pi - lam)) + 25.0))
        newb = []
        for h in beams:
            extra = []
            if rep[i] >= 0 and rep[i] < len(h.choice) and h.choice[rep[i]][0] is not None and not (pinned is not None and i in pinned):
                rw, rtf = h.choice[rep[i]]
                if not (forced_empty is not None and forced_empty[i]):
                    extra.append((rw, rtf, float(rw.occ @ (Pi - lam)) + 0.0))
            prevc = h.choice[-1] if h.choice else (None, 0)
            for w, tf, loc in cand + extra:
                sc = h.score + loc
                if w is None:
                    newb.append((sc, h, w, tf, h.last)); continue
                ent, ex = w.states[tf]
                bad = False
                for c in (0, 1):
                    pl = h.last[c]
                    if pl is None or ent[c] is None: continue
                    gap = (i + ent[c][0] / 4.0) * bar_s - pl[0]
                    fc = flow_cost(pl[1], ent[c], gap, D)
                    if fc >= INF: bad = True; break
                    sc -= fc
                if bad: continue
                # continuity bonus
                pw, ptf = prevc
                if pw is not None and pw.mid == w.mid and pw.k == w.k and ptf == tf and abs(w.s - (pw.s + 4 * w.k)) < 1e-3:
                    sc += 4.5
                elif pw is not None and pw.mid == w.mid:
                    sc += 1.0
                if rep[i] >= 0 and rep[i] < len(h.choice) and h.choice[rep[i]][0] is w and h.choice[rep[i]][1] == tf:
                    sc += 4.0
                elif w.key in h.used and i - h.used[w.key] < 32 and w.special is None:
                    sc -= 3.5
                newlast = dict(h.last)
                for c in (0, 1):
                    if ex[c] is not None:
                        newlast[c] = ((i + ex[c][0] / 4.0) * bar_s, ex[c])
                newb.append((sc, h, w, tf, newlast))
        newb.sort(key=lambda t: -t[0])
        beams = []
        seen = set()
        for sc, h, w, tf, last in newb:
            sig = (id(h), None if w is None else w.key, tf)
            if sig in seen: continue
            seen.add(sig)
            used = h.used
            if w is not None:
                used = dict(h.used); used[w.key] = i
            beams.append(Hyp(sc, h.choice + [(w, tf)], last, used))
            if len(beams) >= B: break
        if pinned is not None and i not in pinned and all(b.choice[-1][0] is not None for b in beams):
            # keep one empty-bar path alive so a pinned special right after always has a valid entry
            e = next((t for t in newb if t[2] is None), None)
            if e is not None: beams.append(Hyp(e[0], e[1].choice + [(None, 0)], e[4], e[1].used))
        if not beams: raise RuntimeError(f'{D}: no valid plan at bar {i} (pinned={pinned is not None and i in pinned})')
    best = max(beams, key=lambda h: h.score)
    return best.choice


def apply_tf_note(n, tf):
    rb, x, y, c, d, a = n
    if tf == 1: return (rb, 3 - x, y, 1 - c, MIRROR_D[d], -a)
    return n


def realize(choice, bar_beat0=4.0):
    """turn plan into absolute objects (beats). bar i starts at bar_beat0 + 4*i"""
    notes, bombs, obst, arcs, chains = [], [], [], [], []
    for i, (w, tf) in enumerate(choice):
        if w is None: continue
        base = bar_beat0 + 4 * i
        for n in w.notes:
            rb, x, y, c, d, a = apply_tf_note(n, tf)
            notes.append(dict(b=base + rb, x=x, y=y, c=c, d=d, a=a, src=w.level))
        for b in w.bombs:
            bombs.append(dict(b=base + b[0], x=(3 - b[1]) if tf else b[1], y=b[2]))
        for o in w.obst:
            x = (4 - o[2] - o[4]) if tf else o[2]
            obst.append(dict(b=base + o[0], d=o[1], x=x, y=o[3], w=o[4], h=o[5]))
        for a in w.arcs:
            a2 = dict(a); a2['hb'] = base + a['hb']; a2['tb'] = base + a['tb']
            if tf:
                a2['c'] = 1 - a['c']; a2['hx'] = 3 - a['hx']; a2['tx'] = 3 - a['tx']
                a2['hd'] = MIRROR_D[a['hd']]; a2['td'] = MIRROR_D[a['td']]
                a2['m'] = {0: 0, 1: 2, 2: 1}.get(a.get('m', 0), 0)
            arcs.append(a2)
        for ch in w.chains:
            c2 = dict(ch); c2['hb'] = base + ch['hb']; c2['tb'] = base + ch['tb']
            if tf:
                c2['c'] = 1 - ch['c']; c2['hx'] = 3 - ch['hx']; c2['tx'] = 3 - ch['tx']; c2['hd'] = MIRROR_D[ch['hd']]
            chains.append(c2)
    return notes, bombs, obst, arcs, chains


def cleanup(notes, bombs, obst, arcs, chains, beat_s):
    """global validity fixes"""
    # duplicate positions at same time
    seen = {}
    out = []
    for n in sorted(notes, key=lambda n: n['b']):
        key = (round(n['b'], 3), n['x'], n['y'])
        if key in seen: continue
        seen[key] = n; out.append(n)
    notes = out
    # same-hand notes too close in time at different swing (cross-window) -> drop later one
    # (plan() already enforces gaps at boundaries; this is a safety net)
    # bombs near notes
    def near_note(b, x, y, tol):
        return any(abs(n['b'] - b) < tol and n['x'] == x and n['y'] == y for n in notes)
    bombs = [b for b in bombs if not near_note(b['b'], b['x'], b['y'], 0.3)]
    # walls colliding with notes / arcs (lanes 0..3 only)
    def wall_ok(o):
        for n in notes:
            if o['b'] - 0.05 <= n['b'] <= o['b'] + o['d'] + 0.05 and o['x'] <= n['x'] < o['x'] + o['w'] and o['y'] <= n['y'] + 0.5 and n['y'] < o['y'] + o['h']:
                return False
        for b in bombs:
            if o['b'] - 0.05 <= b['b'] <= o['b'] + o['d'] + 0.05 and o['x'] <= b['x'] < o['x'] + o['w']:
                return False
        return True
    obst = [o for o in obst if wall_ok(o)]
    # avoid the game's merging of consecutive same-shape walls in the same lane being surprising: fine.
    return notes, bombs, obst, arcs, chains

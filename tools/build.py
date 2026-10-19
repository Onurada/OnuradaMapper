"""Build one song's Beat Saber v4 map. usage: build.py <song_id> [--out DIR] [--diffs ...]"""
import sys, os, json, pickle, math, random, subprocess, shutil, re, glob
import numpy as np
sys.path.insert(0, os.path.dirname(__file__))
from bslib import ascii_only, text_bitmap, bitmap_rects, HEART, write_beatmap_v4, MIRROR_D, text_points, workspace, songs_path, mapper_name
import mapper as M
import lights as L
import colorstory as CS
import gestures as G

S = workspace()
CFG = json.load(open(songs_path(S), encoding='utf-8-sig'))
DIFFS = ['Easy', 'Normal', 'Hard', 'Expert', 'ExpertPlus']
DIFF_RANK = {d: i for i, d in enumerate(DIFFS)}
LOVE = {'ask', 'aski', 'askim', 'askimi', 'askin', 'aska', 'askla', 'sevgilim', 'seviyorum', 'sevdim', 'sevgi', 'sevda', 'sevmek',
        'kalp', 'kalbim', 'kalbime', 'kalbimi', 'kalbin', 'kalbini', 'love', 'heart', 'lover', 'amor', 'corazon', 'mi amor', 'besos', 'beso', 'kiss'}
LOVE_JA = ['æ„›', 'å¥½ã', 'æ‹']


STYLES = {
    'rap': {'hiphop': 1.6, 'pop': 0.8, 'edm': 0.4, 'rock': 0.0, 'ost': -0.3},
    'pop': {'pop': 1.6, 'hiphop': 0.6, 'edm': 0.5, 'rock': 0.2, 'ost': -0.2},
    'dance': {'pop': 1.2, 'hiphop': 1.0, 'edm': 0.8, 'rock': 0.0, 'ost': 0.0},
    'rock': {'rock': 1.6, 'pop': 0.5, 'edm': 0.2, 'hiphop': 0.0, 'ost': 0.0},
    'edm': {'edm': 1.4, 'hiphop': 0.8, 'ost': 0.6, 'pop': 0.3, 'rock': 0.0},
}
GENRE_OF = {'01': 'edm', '02': 'dance', '03': 'dance', '04': 'pop', '08': 'rock', '09': 'rock', '16': 'dance', '17': 'pop', '18': 'pop'}


def hexc(h): return h.upper() + ('FF' if len(h) == 6 else '')


def njs_for(D, bpm):
    base = {'Easy': 10, 'Normal': 11, 'Hard': 13, 'Expert': 15.5, 'ExpertPlus': 17.5}[D]
    if bpm < 100: base -= 1
    if bpm > 150: base += 1
    return base


def hjd_seconds(njs, period, offset=0.0):
    num = 4.0
    while njs * period * num > 17.999: num /= 2
    num = max(0.25, num + offset)
    return num * period


def norm_word(w):
    return re.sub(r'[^a-z0-9 ]', '', ascii_only(w).lower()).strip()


def main():
    sid = sys.argv[1]
    cfg = CFG[sid]
    outroot = sys.argv[sys.argv.index('--out') + 1] if '--out' in sys.argv else os.path.join(S, 'out')
    seed = int(cfg.get('seed', 0))   # 0 = the original map; other values re-roll notes and lights
    rng = random.Random(int(sid) * 7 + 1 + seed * 7919)
    A = json.load(open(os.path.join(S, 'ana', sid + '.json')))
    F = np.load(os.path.join(S, 'ana', sid + '.npz'))
    BT = np.array(A['beat_times'])
    period = float(np.mean(np.diff(BT[:A['nbars'] * 4 + 1])))
    bpm = 60.0 / period
    BEAT0 = 4
    pad = BEAT0 * period - BT[0]           # seconds of silence prepended -> bar 0 at map beat BEAT0
    if pad < 0.2:
        BEAT0 = 8; pad = BEAT0 * period - BT[0]
    dur = A['dur']
    KI = np.arange(len(BT), dtype=float)

    def tbeat(t):
        """original song time -> map beat"""
        if t < BT[0]: k = (t - BT[0]) / period
        elif t > BT[-1]: k = KI[-1] + (t - BT[-1]) / period
        else: k = float(np.interp(t, BT, KI))
        return BEAT0 + k
    end_beat = tbeat(dur)
    nb = A['nbars']
    bar_s = 4 * period

    # ---------------- salience per slot ----------------
    vw = cfg.get('vocal_weight', 0.85)
    kick, snare, hat = F['kick'], F['snare'], F['hat']
    voc = F['vocals'] * (F['vrms'] > 0.12)
    drums = np.maximum(kick, snare * 0.95) + 0.25 * hat
    P = np.maximum(drums, vw * voc) + 0.2 * F['bass'] + 0.25 * F['other']
    ref = np.percentile(P[P > 0.05], 93) if (P > 0.05).any() else 1
    P = np.clip(P / ref, 0, 1.4)
    prior = np.array([1.0 if j % 16 == 0 else 0.92 if j % 8 == 0 else 0.78 if j % 4 == 0 else 0.5 if j % 2 == 0 else 0.3 for j in range(64)])
    P = P[:nb * 64].reshape(nb, 64) * (0.35 + 0.65 * prior)
    Ps = P.copy()
    Ps[:, 1:] = np.maximum(Ps[:, 1:], 0.8 * P[:, :-1]); Ps[:, :-1] = np.maximum(Ps[:, :-1], 0.8 * P[:, 1:])

    bars = A['bars']
    mr, dr = np.array(bars['mrms']), np.array(bars['drms'])
    dens = (P > 0.5).sum(1).astype(float)
    def nz(x):
        lo, hi = np.percentile(x, 10), np.percentile(x, 95)
        return np.clip((x - lo) / max(hi - lo, 1e-6), 0, 1)
    inten = nz(0.5 * mr + 0.3 * dr) * 0.7 + 0.3 * nz(dens)
    silent = mr < 0.07

    # ---------------- repeats ----------------
    SIM, RSIM = F['SIM'], F['RSIM']
    rep = -np.ones(nb, int)
    for i in range(nb):
        best = None
        for j in range(0, i - 3):
            s = SIM[i, j] * 0.6 + RSIM[i, j] * 0.4
            if rep[i - 1] >= 0 and j == rep[i - 1] + 1: s += 0.05
            if s > 0.9 and (best is None or s > best[0]): best = (s, j)
        if best: rep[i] = best[1]

    # ---------------- lyrics ----------------
    words = []
    wf = cfg.get('words_file', sid)
    if os.path.exists(os.path.join(S, 'words', wf + '.json')):
        words = json.load(open(os.path.join(S, 'words', wf + '.json'), encoding='utf-8'))['words']
    if 'words_manual' in cfg:   # [[start, text], ...] overrides
        words = [dict(start=w[0], end=w[0] + 0.3, word=w[1], p=1.0) for w in cfg['words_manual']]
    love_beats = []
    for w in words:
        nwd = norm_word(w['word'])
        if nwd in LOVE or any(ch in w['word'] for ch in LOVE_JA):
            love_beats.append(tbeat(w['start']))
    if not cfg.get('hearts', True): love_beats = []
    love_beats += [tbeat(t) for t in cfg.get('heart_times', [])]
    if cfg.get('heart_chorus'):
        # chorus-like section starts: big intensity rise into the top third
        hi = np.percentile(inten, 66)
        for b0 in A['bounds'][1:-1]:
            if b0 + 2 < nb and inten[b0:b0 + 4].mean() > hi and inten[b0:b0 + 4].mean() - inten[max(0, b0 - 4):b0].mean() > 0.12:
                love_beats.append(BEAT0 + 4 * b0 + 4)   # second bar of the chorus
    love_beats.sort()

    # lyric chunks to display
    chunks = []
    lyric_lines = []
    lyr = cfg.get('lyrics')
    if lyr:
        repl = {k.lower(): v for k, v in lyr.get('replace', {}).items()}
        drop = set(x.lower() for x in lyr.get('drop', []))
        sel = []   # (time, text, groupable)
        breaks = set()
        for w in words:
            raw = norm_word(w['word'])
            if not raw or raw in drop: continue
            if w.get('p', 1) < lyr.get('min_p', 0.3) and raw not in repl: continue
            txt = repl.get(raw, ascii_only(w['word']).upper().strip(' ,.!?'))
            if any(a <= w['start'] <= b for a, b in lyr.get('ranges', [])):
                sel.append((w['start'], txt, True))
                if w['word'].strip()[-1:] in ',.?!;': breaks.add(w['start'])
            for h in lyr.get('hooks', []):
                if re.search(h['re'], raw):
                    sel.append((w['start'], h['text'], False))
        # whisper's own phrase segmentation: every transcribed line ends a karaoke line
        if wf.endswith('_al') and os.path.exists(os.path.join(S, 'words', wf[:-3] + '.json')):
            J0 = json.load(open(os.path.join(S, 'words', wf[:-3] + '.json'), encoding='utf-8'))
            w0 = J0.get('words', [])
            if len(w0) == len(words):
                for ln in J0.get('lines', []):
                    idx = [i for i, w in enumerate(w0) if ln['start'] - 0.05 <= w['start'] < ln['end'] - 0.05]
                    if idx: breaks.add(words[max(idx)]['start'])
        for a_, b_ in lyr.get('ranges', []):   # each range ends its own line
            last_ = [w['start'] for w in words if a_ <= w['start'] <= b_]
            if last_: breaks.add(max(last_))
        for t, txt in lyr.get('manual', []): sel.append((t, ascii_only(txt).upper(), False))
        if lyr.get('chant'):
            ch = lyr['chant']
            v = F['vocals'] * (F['vrms'] > 0.25)
            last = -9
            for j in range(1, len(v) - 1):
                if v[j] > ch.get('thr', 0.6) and v[j] >= v[j - 1] and v[j] >= v[j + 1]:
                    t = BT[0] + j * period / 16
                    if t - last >= ch.get('min_gap', 0.9):
                        sel.append((t, ch['text'], False)); last = t
        sel.sort()
        maxc = lyr.get('max_chars', 14); gapmin = lyr.get('min_gap', 0.42)
        cur = None
        for t, txt, grp in sel:
            if cur and grp and cur[2] and t - cur[0] < lyr.get('group', 0.6) and len(cur[1]) + 1 + len(txt) <= maxc:
                cur = (cur[0], cur[1] + ' ' + txt, True); chunks[-1] = cur; continue
            if chunks and t - chunks[-1][0] < gapmin: continue
            cur = (t, txt, grp); chunks.append(cur)
        chunks = [(t, txt) for t, txt, _ in chunks]
        # karaoke lines for the in-game lyrics mod (times in original song seconds)
        curl = None
        for t, txt, grp in sel:
            if not grp:
                if lyric_lines and lyric_lines[-1]['grp'] is False and t - lyric_lines[-1]['start'] < lyr.get('min_gap', 0.42):
                    continue
                ws = txt.split(); step = 0.3
                lyric_lines.append({'start': t, 'end': t + max(1.3, step * len(ws) + 0.8), 'grp': False,
                                    'words': [{'t': t + i * step, 'w': x} for i, x in enumerate(ws)]})
                curl = None; continue
            cur_txt = ' '.join(w['w'] for w in curl['words']) if curl else ''
            if curl is None or curl.get('brk') or t - curl['words'][-1]['t'] > lyr.get('line_gap', 1.3) or len(cur_txt) + 1 + len(txt) > lyr.get('line_chars', 30):
                curl = {'start': t, 'end': t + 0.7, 'grp': True, 'words': []}; lyric_lines.append(curl)
            curl['words'].append({'t': t, 'w': txt}); curl['end'] = t + 0.7
            curl['brk'] = t in breaks
        for ml in lyr.get('manual_lines', []):   # [[t, word], ...] with exact word times
            lyric_lines.append({'start': ml[0][0], 'end': ml[-1][0] + 1.2, 'grp': False,
                                'words': [{'t': t, 'w': ascii_only(w).upper()} for t, w in ml]})

    # ---------------- audio / cover ----------------
    outdir = os.path.join(outroot, cfg['folder'])
    os.makedirs(outdir, exist_ok=True)
    egg = os.path.join(outdir, 'song.egg')
    if not os.path.exists(egg) or '--reaudio' in sys.argv:
        subprocess.run(['ffmpeg', '-y', '-loglevel', 'error', '-i', os.path.join(S, 'audio', sid + '.wav'),
                        '-af', f'adelay={int(round(pad * 1000))}:all=1,afade=t=out:st={dur + pad - 1.5:.3f}:d=1.5',
                        '-c:a', 'libvorbis', '-q:a', '7', '-f', 'ogg', egg], check=True)
    cover = os.path.join(S, 'covers', sid + '.jpg')
    if os.path.exists(cover): shutil.copy(cover, os.path.join(outdir, 'cover.jpg'))

    # drop downbeats (song seconds) -> bar index; a drop replaces any other special on its bar
    drop_bars = sorted(set(int(round((tbeat(t) - BEAT0) / 4.0)) for t in cfg.get('drops', [])))
    # lyric gestures [{t, g}] -> (bar, offset in bar, name); a word late in a bar moves its gesture to the next downbeat
    gesture_bars = []
    for ge in cfg.get('gestures', []):
        b_ = tbeat(ge['t']) - BEAT0
        gb, go = int(b_ // 4), round((b_ % 4) * 2) / 2
        if go > 1.5: gb, go = gb + 1, 0.0
        if all(gb != x[0] for x in gesture_bars): gesture_bars.append((gb, go, ge['g']))

    # ---------------- per difficulty ----------------
    corpus = pickle.load(open(os.path.join(S, 'corpus.pkl'), 'rb'))
    diff_info = []
    last_note_b = 0.0
    report = {'bpm': bpm, 'pad': pad, 'chunks': len(chunks), 'hearts': [], 'diffs': {}}
    for D in DIFFS:
        allowed = {'ExpertPlus': ['ExpertPlus'], 'Expert': ['Expert'], 'Hard': ['Hard'], 'Normal': ['Normal'], 'Easy': ['Easy']}[D]
        wins = M.build_windows(corpus, allowed, bpm)
        if len(wins) < 400:
            wins += M.build_windows(corpus, {'ExpertPlus': ['Expert'], 'Expert': ['ExpertPlus', 'Hard'], 'Hard': ['Expert', 'Normal'], 'Normal': ['Hard', 'Easy'], 'Easy': ['Normal']}[D], bpm)
        njs = cfg.get('njs', {}).get(D, njs_for(D, bpm))
        hjd = hjd_seconds(njs, period)
        targets = M.NPS[D] * cfg.get('density', 1.0) * bar_s * (0.45 + 0.7 * inten)
        targets[silent] = 0
        # hearts
        specials = {}
        lastb = -99
        nh = 0
        for lb in love_beats:
            bi = int((lb - BEAT0) // 4)
            if bi < 1 or bi >= nb - 1 or silent[bi] or bi - lastb < 6 or nh >= cfg.get('max_hearts', 6): continue
            if D == 'Easy':
                hw = M.make_special([(0.0, 1, 0, 0, 1, 0), (0.0, 2, 0, 1, 1, 0), (2.0, 0, 2, 0, 4, 0), (2.0, 3, 2, 1, 5, 0)],
                                    [dict(c=0, hb=2.0, hx=0, hy=2, hd=4, hm=1.0, tb=3.75, tx=1, ty=0, td=7, tm=1.0, m=0),
                                     dict(c=1, hb=2.0, hx=3, hy=2, hd=5, hm=1.0, tb=3.75, tx=2, ty=0, td=6, tm=1.0, m=0)], 'heart')
            else:
                gap = 1.0 if period >= 0.33 else 2.0
                arcb = max(1.0, min(2.0, round((0.7 / period) * 2) / 2))
                notes_h = [(0.0, 1, 0, 0, 1, 0), (0.0, 2, 0, 1, 1, 0), (gap, 0, 2, 0, 4, 0), (gap, 3, 2, 1, 5, 0)]
                arcs_h = [dict(c=0, hb=gap, hx=0, hy=2, hd=4, hm=1.0, tb=min(3.9, gap + arcb), tx=1, ty=0, td=7, tm=1.0, m=0),
                          dict(c=1, hb=gap, hx=3, hy=2, hd=5, hm=1.0, tb=min(3.9, gap + arcb), tx=2, ty=0, td=6, tm=1.0, m=0)]
                hw = M.make_special(notes_h, arcs_h, 'heart')
            specials[bi] = hw; lastb = bi; nh += 1
        # signature move on hook words (e.g. "toz pembe"): sparkly chains down the middle, then a heart
        if D != 'Easy':
            for st_ in cfg.get('signature_times', []):
                bi = int(round((tbeat(st_) - BEAT0) / 4.0))
                if bi < 1 or bi >= nb - 1 or silent[bi]: continue
                gap = 1.0 if period >= 0.33 else 2.0
                arcb = max(1.0, min(2.0, round((0.7 / period) * 2) / 2))
                tb_ = min(3.9, gap + arcb)
                notes_s = [(0.0, 1, 2, 0, 1, 0), (0.0, 2, 2, 1, 1, 0), (gap, 0, 2, 0, 4, 0), (gap, 3, 2, 1, 5, 0)]
                arcs_s = [dict(c=0, hb=gap, hx=0, hy=2, hd=4, hm=1.0, tb=tb_, tx=1, ty=0, td=7, tm=1.0, m=0),
                          dict(c=1, hb=gap, hx=3, hy=2, hd=5, hm=1.0, tb=tb_, tx=2, ty=0, td=6, tm=1.0, m=0)]
                sw = M.make_special(notes_s, arcs_s, 'signature')
                sw.chains = [dict(c=0, hb=0.0, hx=1, hy=2, hd=1, tb=0.25, tx=1, ty=0, sc=5, s=1.0),
                             dict(c=1, hb=0.0, hx=2, hy=2, hd=1, tb=0.25, tx=2, ty=0, sc=5, s=1.0)]
                ent_, ex_ = M.hand_states(sw.notes, sw.arcs, sw.chains); sw.states = [(ent_, ex_), (ent_, ex_)]
                specials[bi] = sw
        forced = silent.copy()
        style = STYLES[cfg.get('genre', GENRE_OF.get(sid, 'rap'))]
        choice = M.plan(D, wins, Ps, targets, bar_s, rep, specials=specials, forced_empty=forced, style=style, seed=seed)
        # drops: the bar before rises on one long arc pair ("inhale"), then the drop bar bursts open.
        # Everything else stays pinned to the plan above; only the bar before / after each drop is re-planned.
        pinned = {i: c for i, c in enumerate(choice)}
        for bi in drop_bars:
            if bi < 2 or bi >= nb - 1: continue
            for j in (bi - 2, bi - 1, bi + 1): pinned.pop(j, None)
            if D != 'Easy':
                arcs_in = [dict(c=0, hb=0.0, hx=1, hy=0, hd=0, hm=1.0, tb=3.0, tx=0, ty=2, td=0, tm=1.0, m=0),
                           dict(c=1, hb=0.0, hx=2, hy=0, hd=0, hm=1.0, tb=3.0, tx=3, ty=2, td=0, tm=1.0, m=0)]
                pinned[bi - 1] = (M.make_special([(0.0, 1, 0, 0, 0, 0), (0.0, 2, 0, 1, 0, 0)], arcs_in, 'drop_in'), 0)
            if D in ('Expert', 'ExpertPlus'):
                notes_d = [(1.0, 0, 1, 0, 0, 0), (1.0, 3, 1, 1, 0, 0), (2.0, 1, 0, 0, 1, 0), (2.0, 2, 0, 1, 1, 0),
                           (3.0, 0, 2, 0, 4, 0), (3.0, 3, 2, 1, 5, 0)]
            elif D == 'Easy':
                notes_d = [(0.0, 1, 1, 0, 1, 0), (0.0, 2, 1, 1, 1, 0), (2.0, 0, 1, 0, 0, 0), (2.0, 3, 1, 1, 0, 0)]
            else:
                notes_d = [(2.0, 0, 1, 0, 0, 0), (2.0, 3, 1, 1, 0, 0)]
            if D != 'Easy': notes_d = [(0.0, 1, 2, 0, 1, 0), (0.0, 2, 2, 1, 1, 0)] + notes_d   # chain heads
            dw = M.make_special(notes_d, [], 'drop')
            if D != 'Easy':   # both hands burst outward on sparkling chains
                dw.chains = [dict(c=0, hb=0.0, hx=1, hy=2, hd=1, tb=0.25, tx=0, ty=0, sc=5, s=1.0),
                             dict(c=1, hb=0.0, hx=2, hy=2, hd=1, tb=0.25, tx=3, ty=0, sc=5, s=1.0)]
                ent_, ex_ = M.hand_states(dw.notes, [], dw.chains); dw.states = [(ent_, ex_), (ent_, ex_)]
            pinned[bi] = (dw, 0)
        # lyric gestures: the bar of a key word gets its hand-made move; its free neighbours are re-planned around it
        drop_owned = {j for bi in drop_bars for j in (bi - 1, bi)}
        gest_used = []
        for gb, go, gname in gesture_bars:
            if gb < 1 or gb >= nb - 1 or gb in drop_owned or gb + 1 in drop_owned or silent[gb]: continue   # drop_in opens on an up swing too
            g = G.make(gname, D, go)
            if g is None or not g['notes']: continue
            gw = M.make_special(g['notes'], g['arcs'], 'gesture_' + gname)
            gw.chains = g['chains']; gw.bombs = g['bombs']
            ent_, ex_ = M.hand_states(gw.notes, gw.arcs, gw.chains); gw.states = [(ent_, ex_), (ent_, ex_)]
            pinned[gb] = (gw, 0); gest_used.append((gb, gname))
        owned = drop_owned | {gb for gb, _ in gest_used}
        for gb, _ in gest_used:
            for j in (gb - 1, gb + 1):
                if j not in owned: pinned.pop(j, None)
        if drop_bars or gest_used:
            choice = M.plan(D, wins, Ps, targets, bar_s, rep, specials=specials, forced_empty=forced, style=style, pinned=pinned, B=200, seed=seed)
        notes, bombs, obst, arcs, chains = M.realize(choice, bar_beat0=BEAT0)
        hearts_used = [i for i, (w, tf) in enumerate(choice) if w is not None and w.special == 'heart']
        drops_used = [i for i, (w, tf) in enumerate(choice) if w is not None and w.special in ('drop_in', 'drop')]
        notes, bombs, obst, arcs, chains = M.cleanup(notes, bombs, obst, arcs, chains, period)
        # drop objects past the end
        notes = [n for n in notes if n['b'] < end_beat - 0.5]
        arcs = [a for a in arcs if a['tb'] < end_beat - 0.5]
        chains = [c for c in chains if c['tb'] < end_beat - 0.5]
        bombs = [b for b in bombs if b['b'] < end_beat - 0.5]
        obst = [o for o in obst if o['b'] + o['d'] < end_beat - 0.5]
        if notes: last_note_b = max(last_note_b, max(n['b'] for n in notes))
        # ------------- lyric / heart wall billboards -------------
        thick = 0.25 / (njs * period)   # ~0.25 m deep
        def billboard(rows, arrive_b, sides=('L', 'R'), xl=None):
            W = len(rows[0])
            out = []
            for side in sides:
                x0 = (-1 - W) if side == 'L' else 5
                if side == 'C': x0 = 2 - W // 2
                for (c, r, w, h) in bitmap_rects(rows):
                    out.append(dict(b=arrive_b, d=thick, x=x0 + c, y=r, w=w, h=h))
            return out
        lead = hjd + 0.2
        if cfg.get('lyrics', {}).get('on_cut'):
            nts = np.array(sorted(n['b'] for n in notes))
        textwalls = []
        def b2egg(b): return BT[0] + pad + (b - BEAT0) * period
        if D == 'ExpertPlus':
            heart_times = [round(b2egg(BEAT0 + 4 * hb + (1.0 if period >= 0.33 else 2.0)), 3) for hb in hearts_used]
            if cfg.get('lyrics', {}).get('on_cut') and len(nts):
                for L_ in lyric_lines:   # snap each line to the nearest note so the word appears as you cut
                    tb = tbeat(L_['start']); j = np.argmin(np.abs(nts - tb))
                    if abs(nts[j] - tb) < 0.75:
                        dt = (nts[j] - tb) * period
                        L_['start'] += dt; L_['end'] += dt
                        for w_ in L_['words']: w_['t'] += dt
        for t, txt in (chunks if cfg.get('wall_text') else []):
            tb = tbeat(t)
            if cfg.get('lyrics', {}).get('on_cut') and len(nts):
                j = np.argmin(np.abs(nts - tb))
                if abs(nts[j] - tb) < 0.75: tb = nts[j]
            arrive = tb + lead / period
            ws = txt.split()
            if len(ws) >= 2:
                best = min(range(1, len(ws)), key=lambda k: abs(len(' '.join(ws[:k])) - len(' '.join(ws[k:]))))
                textwalls += billboard(text_bitmap(' '.join(ws[:best])), arrive, ('L',))
                textwalls += billboard(text_bitmap(' '.join(ws[best:])), arrive, ('R',))
            elif len(txt) > 6:
                m = (len(txt) + 1) // 2
                textwalls += billboard(text_bitmap(txt[:m]), arrive, ('L',))
                textwalls += billboard(text_bitmap(txt[m:]), arrive, ('R',))
            else:
                textwalls += billboard(text_bitmap(txt), arrive)
        for hb in (hearts_used if cfg.get('wall_text') else []):
            arrive = BEAT0 + 4 * hb + 1.0 + lead / period * 0.6
            textwalls += billboard(HEART, arrive)
            textwalls += billboard(HEART, arrive + 0.5)
        report['hearts'] = hearts_used
        # hook words spelled in walls, letter by letter (left word / right word), e.g. TOZ | PEMBE
        hw = cfg.get('hook_walls')
        hook_arrivals = []
        if hw:
            for t in hw.get('times', cfg.get('signature_times', [])):
                arrive = tbeat(t) + lead / period
                hook_arrivals.append(arrive)
                for side, word in zip(('L', 'R'), hw['text']):
                    W = len(text_bitmap(word)[0]); x0 = (-1 - W) if side == 'L' else 5
                    off = 0
                    for li, ch in enumerate(word):
                        rows = text_bitmap(ch)
                        for (c, r, w, h) in bitmap_rects(rows):
                            textwalls.append(dict(b=arrive + 0.14 * li, d=thick, x=x0 + off + c, y=r, w=w, h=h))
                        off += len(rows[0]) + 1
        # chorus: a tunnel of wall hearts flying past both sides, one per bar (not where a hook word passes)
        for a_, b_ in cfg.get('chorus', []):
            ba, bb = tbeat(a_), tbeat(b_)
            k_ = math.ceil((ba - BEAT0) / 4.0)
            while BEAT0 + 4 * k_ < bb:
                if not any(abs(BEAT0 + 4 * k_ - ar) < 2.5 for ar in hook_arrivals):
                    textwalls += billboard(HEART, BEAT0 + 4 * k_ + 0.02)
                k_ += 1
        # drops: pillars close in from far out during the inhale bar, then a wall shockwave blasts outward
        for bi in drop_bars:
            if bi < 2 or bi >= nb - 1: continue
            b_in, b_drop = BEAT0 + 4 * (bi - 1), BEAT0 + 4 * bi
            for k in range(7):
                textwalls += [dict(b=b_in + 0.5 * k, d=0.25, x=-7 + k, y=0, w=1, h=5), dict(b=b_in + 0.5 * k, d=0.25, x=10 - k, y=0, w=1, h=5)]
            for k in range(8):
                h_ = 5 - k // 2
                textwalls += [dict(b=b_drop + 0.0625 * k, d=0.12, x=-1 - k, y=0, w=1, h=h_), dict(b=b_drop + 0.0625 * k, d=0.12, x=4 + k, y=0, w=1, h=h_)]
        obst_all = obst + textwalls
        fn = f'{D}Standard.dat'
        write_beatmap_v4(os.path.join(outdir, fn), notes, bombs, obst_all, arcs, chains)
        nps = len(notes) / max(1e-6, (dur - 2))
        srcs = {}
        for n in notes: srcs[n.get('src')] = srcs.get(n.get('src'), 0) + 1
        report['diffs'][D] = dict(notes=len(notes), nps=round(nps, 2), arcs=len(arcs), chains=len(chains), bombs=len(bombs), walls=len(obst),
                                  textwalls=len(textwalls), drops=drops_used, gestures=gest_used, njs=njs, hjd=round(hjd, 3), top_sources=sorted(srcs.items(), key=lambda kv: -kv[1])[:6])
        diff_info.append(dict(characteristic='Standard', difficulty=D,
                              beatmapAuthors={'mappers': [mapper_name(cfg)], 'lighters': [mapper_name(cfg)]},
                              environmentNameIdx=0, beatmapColorSchemeIdx=0, noteJumpMovementSpeed=njs, noteJumpStartBeatOffset=0,
                              beatmapDataFilename=fn, lightshowDataFilename='Lightshow.dat'))
        print(D, report['diffs'][D], flush=True)

    # ---------------- lightshow ----------------
    env = cfg['env']
    donors = []
    for m in corpus:
        if m['env'] != env or m['diff'] != 'ExpertPlus': continue
        lsf = glob.glob(os.path.join(S, 'corpus', m['level'], '*ommon.lightshow.json')) or glob.glob(os.path.join(S, 'corpus', m['level'], '*.lightshow.json'))
        if not lsf: continue
        r = abs(m['bpm']) / bpm
        if not (0.72 <= r <= 1.38) and not cfg.get('any_donor_bpm'): continue
        if cfg.get('donors') and m['level'] not in cfg['donors']: continue
        src = L.load_donor(lsf[0])
        ph = M.corpus_phase(m)
        last = max([e['b'] for e in src['basicEvents']] + [e['b'] for e in src.get('eventBoxGroups', [])] + [0])
        nbd = int((last - ph) // 4) + 1
        inten_d = 0.6 * L.donor_bar_intensity(m, ph, nbd) + 0.4 * L.light_event_density(src, ph, nbd)
        donors.append(dict(src=src, inten=inten_d, phase=ph, name=m['level']))
    if not donors: raise SystemExit('no donors for ' + env)
    bounds = A['bounds']
    secs = []
    for a, b in zip(bounds[:-1], bounds[1:]):
        while b - a > 16:
            secs.append((a, a + 8)); a += 8
        secs.append((a, b))
    rep_sec = []
    for si, (a, b) in enumerate(secs):
        r = -1
        for sj in range(si):
            a2, b2 = secs[sj]
            if b2 - a2 == b - a:
                sim = np.mean([SIM[a + q, a2 + q] for q in range(b - a)])
                if sim > 0.88: r = sj; break
        rep_sec.append(r)
    lb, chosen = L.build_lightshow(donors, list(inten), secs, rep_sec, float(BEAT0), end_beat, rng)
    # chorus: boosted (brighter) palette held for the whole chorus, with a strobe-like entry
    for a_, b_ in cfg.get('chorus', []):
        ba, bb = tbeat(a_), tbeat(b_)
        lb.boost = [e for e in lb.boost if not (ba - 0.01 <= e['b'] <= bb + 0.01)]
        for q, on in enumerate([1, 0, 1, 0, 1, 0, 1]):
            lb.boost.append({'b': round(ba + q * 0.125, 4), 'i': lb.cbd.add({'b': on} if on else {})})
        lb.boost.append({'b': round(bb, 4), 'i': lb.cbd.add({})})
    for st_ in cfg.get('signature_times', []):
        b0 = tbeat(st_)
        for q, on in enumerate([0, 1, 0, 1]):
            lb.boost.append({'b': round(b0 + q * 0.125, 4), 'i': lb.cbd.add({'b': on} if on else {})})
    for t in cfg.get('flash_times', []):
        b0 = tbeat(t)
        for q, on in enumerate([1, 0, 1, 0, 1, 0]):
            lb.boost.append({'b': round(b0 + q * 0.25, 4), 'i': lb.cbd.add({'b': on} if on else {})})
    if cfg.get('lightstory'):   # six-colour story (replaces the palette / boost schedule above)
        kick_t = CS.onsets(F['kick'], period, BT[0], 0.9, 0.2)
        snare_t = CS.onsets(np.where(F['kick'] < 0.6, F['snare'], 0), period, BT[0], 0.7, 0.2)
        story = dict(cfg['lightstory'])
        story['blackouts'] = story.get('blackouts', []) + [[float(BT[min(4 * gb, len(BT) - 1)]), float(BT[min(4 * gb + 4, len(BT) - 1)])] for gb, go, gn in gesture_bars if gn == 'gozunu']
        report['lightstory'] = CS.apply(lb, story, tbeat, end_beat, kick_t, snare_t)
        print('lightstory', report['lightstory'])
    lj = lb.json()
    json.dump(lj, open(os.path.join(outdir, 'Lightshow.dat'), 'w'), separators=(',', ':'))
    report['light_donors'] = [(secs[si], donors[dn]['name'], ds) for si, (dn, ds) in chosen.items()]

    # ---------------- info / audio data ----------------
    nsamp = int(round((dur + pad) * 44100))
    def smp(t): return int(round(t * 44100))
    if A.get('warped'):
        regions = [{'si': 0, 'ei': smp(BT[0] + pad), 'sb': 0, 'eb': BEAT0}]
        for k in range(len(BT) - 1):
            si, ei = smp(BT[k] + pad), smp(BT[k + 1] + pad)
            if si >= nsamp: break
            if ei >= nsamp:
                regions.append({'si': si, 'ei': nsamp, 'sb': BEAT0 + k, 'eb': round(BEAT0 + k + (nsamp - si) / (ei - si), 4)}); break
            regions.append({'si': si, 'ei': ei, 'sb': BEAT0 + k, 'eb': BEAT0 + k + 1})
        else:
            si = smp(BT[-1] + pad)
            regions.append({'si': si, 'ei': nsamp, 'sb': BEAT0 + len(BT) - 1, 'eb': round(BEAT0 + len(BT) - 1 + (nsamp - si) / 44100 / period, 4)})
    else:
        regions = [{'si': 0, 'ei': nsamp, 'sb': 0, 'eb': round(nsamp / 44100 / period, 4)}]
    json.dump({'version': '4.0.0', 'songChecksum': '', 'songSampleCount': nsamp, 'songFrequency': 44100,
               'bpmData': regions, 'lufsData': []},
              open(os.path.join(outdir, 'AudioData.dat'), 'w'), indent=1)
    col = cfg['colors']
    info = {
        'version': '4.0.1',
        'song': {'title': ascii_only(cfg['title']) + f' (by {ascii_only(mapper_name(cfg))})', 'subTitle': ascii_only(cfg.get('subtitle', '')), 'author': ascii_only(cfg['artist'])},
        'audio': {'songFilename': 'song.egg', 'songDuration': round(dur + pad, 3), 'audioDataFilename': 'AudioData.dat', 'bpm': round(bpm, 3),
                  'lufs': 0, 'previewStartTime': round(cfg.get('preview', (A['bounds'][min(2, len(A['bounds']) - 1)] * 4 + 4) * period), 2), 'previewDuration': 12},
        'songPreviewFilename': 'song.egg', 'coverImageFilename': 'cover.jpg',
        'environmentNames': [env],
        'colorSchemes': [{'overrideNotes': True, 'overrideLights': True, 'colorSchemeName': ascii_only(cfg['title']),
                          'saberAColor': hexc(col['a']), 'saberBColor': hexc(col['b']), 'environmentColor0': hexc(col['e0']),
                          'environmentColor1': hexc(col['e1']), 'obstaclesColor': hexc(col['w']),
                          'environmentColor0Boost': hexc(col.get('e0b', col['e0'])), 'environmentColor1Boost': hexc(col.get('e1b', col['e1'])),
                          'environmentColorW': hexc(col.get('ew', 'FFFFFF')), 'environmentColorWBoost': hexc(col.get('ewb', 'FFFFFF'))}],
        'difficultyBeatmaps': diff_info,
    }
    json.dump(info, open(os.path.join(outdir, 'Info.dat'), 'w'), indent=1)
    # lyrics for the OnuradaLyrics mod: times in song.egg seconds
    lines_out = []
    for L_ in sorted(lyric_lines, key=lambda x: x['start']):
        off_ = (cfg.get('lyrics') or {}).get('offset', 0.0) if L_.get('grp') else 0.0   # optional per-song delay of sung lines
        lines_out.append({'start': round(L_['start'] + pad + off_, 3), 'end': round(L_['end'] + pad + off_, 3),
                          'words': [{'t': round(w_['t'] + pad + off_, 3), 'w': w_['w']} for w_ in L_['words']]})
    for i in range(len(lines_out) - 1):   # never overlap the next line
        lines_out[i]['end'] = min(lines_out[i]['end'], lines_out[i + 1]['start'] - 0.05)
    lyr_json = {'title': info['song']['title'], 'color': col['a'][:6], 'lines': lines_out, 'hearts': heart_times, 'bursts': [round(t + pad, 3) for t in cfg.get('burst_times', [])], 'fx': cfg.get('lyrics', {}).get('fx', '')}
    if cfg.get('video'):
        v = dict(cfg['video']); src = os.path.join(S, 'vid', v['file'])
        if os.path.exists(src):
            shutil.copy(src, os.path.join(outdir, v['file']))
            v['start'] = round(v['start'] + pad, 3); v['end'] = round(v['end'] + pad, 3)
            lyr_json['video'] = v
    if 'dust' in cfg:   # pink dust: floating motes in the given ranges (default: chorus), puffs on hooks and drops, puffs on cuts
        dc = cfg['dust']
        ranges = dc.get('ranges', cfg.get('chorus', []))
        drops_ = cfg.get('drops', [])
        bursts = dc.get('bursts', [[t, 1.0] for t in cfg.get('signature_times', []) if not any(0 <= t - d_ < 3.0 for d_ in drops_)]
                        + [[t, 1.7] for t in drops_])
        beats = [round(b2egg(b), 3) for b in range(int(BEAT0), int(end_beat) + 1)
                 if any(tbeat(a_) - 0.01 <= b <= tbeat(b_) for a_, b_ in ranges)]
        lyr_json['dust'] = {'ranges': [[round(a_ + pad, 3), round(b_ + pad, 3)] for a_, b_ in ranges],
                            'bursts': sorted([round(t + pad, 3), s] for t, s in bursts), 'beats': beats,
                            'cut': dc.get('cut', True), 'colors': dc.get('colors', [col['a'][:6], col['b'][:6], col['w'][:6]]),
                            'dream': [[round(a_ + pad, 3), round(b_ + pad, 3)] for a_, b_ in cfg.get('dream', [])],
                            'dream_colors': dc.get('dream_colors', [col.get('e1b', col['b'])[:6], col['e1'][:6], 'FFFFFF'])}
        if dc.get('text'):   # intro title gathers out of dust and bursts; outro text forms, then blows away
            texts = []
            if cfg.get('intro'):
                it = cfg['intro']   # intro times are song.egg seconds already
                texts.append({'start': it['start'], 'end': it['end'], 'exit': 'burst', 'y': 0.4,
                              'points': text_points(it['title'], 900, 1.2, seed=1)})
            if cfg.get('outro') and cfg['outro'].get('text'):
                ot = cfg['outro']
                texts.append({'start': round(ot['start'] + pad + 1.0, 3), 'end': round(ot['end'] + pad, 3), 'exit': 'wind', 'y': 0.6,
                              'points': text_points(ot['text'], 800, 0.95, seed=2)})
            lyr_json['dust']['texts'] = texts
    if cfg.get('dream'):   # heartbeat rumble: lub-dub every two beats, racing every beat in the last two bars before the drop
        hb = []
        for a_, b_ in cfg['dream']:
            ba, bb = tbeat(a_), tbeat(b_)
            k0 = math.ceil(ba)
            for k in range(k0, int(math.floor(bb))):
                if bb - k <= 8 or (k - k0) % 2 == 0:
                    t = b2egg(k); hb += [[round(t, 3), 0], [round(t + 0.17, 3), 1]]
        lyr_json['heartbeat'] = hb
    if cfg.get('combo'):   # combo milestones + full-combo celebration (after the last note of the hardest diff)
        lyr_json['combo'] = {'milestones': [50, 100, 150, 200, 300, 400, 500, 600, 700, 800], 'fc': round(b2egg(last_note_b) + 0.6, 3)}
    if col.get('ew'):   # the game forces W to white for custom levels; the mod puts these back
        lyr_json['wcolors'] = {'scheme': ascii_only(cfg['title']), 'w': col['ew'][:6], 'wb': col.get('ewb', col['ew'])[:6],
                               'rainbow': [x[:6] for x in (col['e0'], col.get('e0b', col['e0']), col['ew'], col.get('ewb', col['ew']), col['e1'], col.get('e1b', col['e1']))]}
    if cfg.get('intro'):   # cinematic title at the start (times are song.egg seconds already)
        lyr_json['intro'] = dict(cfg['intro'])
    if cfg.get('outro'):   # heart rain + closing text
        o = dict(cfg['outro']); o['start'] = round(o['start'] + pad, 3); o['end'] = round(o['end'] + pad, 3)
        lyr_json['outro'] = o
    json.dump(lyr_json, open(os.path.join(outdir, 'lyrics.json'), 'w'), indent=1)
    report['lyric_lines'] = len(lines_out)
    json.dump(report, open(os.path.join(outdir, '..', f'report_{sid}.json'), 'w'), indent=1, default=str)
    print('lights', report['light_donors'])
    print('chunks', chunks[:60])
    print('hearts', report['hearts'])


if __name__ == '__main__':
    main()

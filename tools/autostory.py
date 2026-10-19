"""Derive a full "Toz Pembe treatment" config for a song from its analysis, transcript and voice-ID result.
usage: autostory.py S id "Title" "Artist" env [--apply] [--keep] [--mapper NAME] [--dream voice|voice+auto|auto|none]
                    [--karaoke dream|all|none] [--video FILE.bc3|none] [--colors AAAAAA BBBBBB]
Finds chorus sections (energy + repetition + title words), drops (chorus starts where the drums hit after a dip),
the Zoktay part (voice/<id>.json, becomes the dream section with the zoktay video and karaoke lyrics;
without a voice match, --dream auto takes the calmest sung section in the middle of the song),
hook times (title words sung), intro/outro, and the six-colour light story segments."""
import sys, os, json, re, numpy as np
sys.stdout.reconfigure(encoding='utf-8')
sys.path.insert(0, os.path.dirname(__file__))
from bslib import ascii_only, songs_path, mapper_name

S, sid, title, artist, env = sys.argv[1:6]
def opt(name, default=None, n=1):
    if name not in sys.argv: return default
    i = sys.argv.index(name)
    return sys.argv[i + 1] if n == 1 else sys.argv[i + 1:i + 1 + n]
MAPPER = opt('--mapper') or mapper_name()
DREAM = opt('--dream', 'voice')        # voice: voice/<id>.json, voice+auto: that or else auto, auto, none
KARAOKE = opt('--karaoke', 'dream')    # where sung lines are shown: dream section, whole song, nowhere
VIDEO = opt('--video') or ('zoktay.bc3' if os.path.exists(f'{S}/vid/zoktay.bc3') else 'none')
A = json.load(open(f'{S}/ana/{sid}.json')); F = np.load(f'{S}/ana/{sid}.npz')
BT = np.array(A['beat_times']); nb = A['nbars']; dur = A['dur']
def bar_t(i): return float(BT[min(4 * i, len(BT) - 1)]) if i * 4 < len(BT) else float(BT[-1] + (4 * i - len(BT) + 1) * A['period'])
mr, dr = np.array(A['bars']['mrms']), np.array(A['bars']['drms'])
vr = F['vrms'][:nb * 64].reshape(nb, 64).mean(1) if len(F['vrms']) >= nb * 64 else np.zeros(nb)
SIM = F['SIM']
bounds = [b for b in A['bounds'] if b <= nb]
secs = []
for a, b in zip(bounds[:-1], bounds[1:]):   # long sections are split into 8-bar phrases
    while b - a > 12: secs.append((a, a + 8)); a += 8
    if b > a: secs.append((a, b))

# ---- words ----
wf = f'{S}/words/{sid}_al.json' if os.path.exists(f'{S}/words/{sid}_al.json') else f'{S}/words/{sid}.json'
words = json.load(open(wf, encoding='utf-8'))['words'] if os.path.exists(wf) else []
def norm(w): return re.sub(r'[^a-z0-9]', '', ascii_only(w).lower())
tw = [norm(x) for x in title.split() if len(norm(x)) >= 2]
key = max(tw, key=len) if tw else ''
title_hits = [w['start'] for w in words if key and (norm(w['word']) == key or (len(key) >= 4 and norm(w['word']).startswith(key[:4])))]

# ---- Zoktay part (voice) ----
dream = None
vp = f'{S}/voice/{sid}.json'
if DREAM in ('voice', 'voice+auto') and os.path.exists(vp):
    V = json.load(open(vp))
    segs = [s for s in V['segments'] if 8 <= s[1] - s[0] <= 45]
    if segs:
        s = max(segs, key=lambda s: s[2] + 0.004 * (s[1] - s[0]))
        # snap to bars
        ba = int(np.argmin([abs(bar_t(i) - s[0]) for i in range(nb)])); bb = int(np.argmin([abs(bar_t(i) - s[1]) for i in range(nb + 1)]))
        if bb - ba >= 3: dream = [ba, bb]
if dream is None and DREAM in ('voice+auto', 'auto') and nb >= 24:
    # calmest sung stretch in the middle of the song: vocals on, drums and energy down
    def zz(x): x = np.asarray(x, float); return (x - x.mean()) / (x.std() + 1e-6)
    zv, zd, zm = zz(vr), zz(dr), zz(mr)
    best = None
    for a in range(int(nb * 0.3), int(nb * 0.8)):
        for L in (8, 6, 4):
            b = a + L
            if b > nb - 4 or not 8 <= bar_t(b) - bar_t(a) <= 45: continue
            if (vr[a:b] > 0.2 * vr.max()).mean() < 0.6: continue
            sc = zv[a:b].mean() - zd[a:b].mean() - 0.5 * zm[a:b].mean() + (0.3 if a in A['bounds'] else 0) + 0.05 * L
            if best is None or sc > best[0]: best = (sc, a, b)
    if best: dream = [best[1], best[2]]
# ---- chorus sections ----
def z(x): x = np.asarray(x, float); return (x - x.mean()) / (x.std() + 1e-6)
E = [mr[a:b].mean() for a, b in secs]; D = [dr[a:b].mean() for a, b in secs]
R = []
for i, (a, b) in enumerate(secs):
    best = 0
    for j, (c, d) in enumerate(secs):
        if i == j: continue
        L = min(b - a, d - c)
        if L < 2: continue
        best = max(best, float(np.mean([SIM[a + q, c + q] for q in range(L)])))
    R.append(best)
H = [sum(1 for t in title_hits if bar_t(a) <= t < bar_t(b)) for a, b in secs]
score = z(E) + 0.6 * z(D) + 0.8 * z(R) + 0.9 * np.minimum(H, 3)
def zov(a, b): return 0 if not dream else max(0, min(b, dream[1]) - max(a, dream[0])) / max(1, b - a)
med = float(np.median(E))
cand = [i for i, (a, b) in enumerate(secs) if (b - a) >= 2 and a > 2 and zov(a, b) < 0.5 and
        ((H[i] >= 1 and E[i] >= 0.8 * med) or score[i] > 1.0 or (not title_hits and score[i] > 0.6))]
chosen, used_bars = set(), 0
for i in sorted(cand, key=lambda i: -score[i]):   # choruses never take more than half the song
    a, b = secs[i]
    if used_bars + (b - a) <= 0.5 * nb or not chosen: chosen.add(i); used_bars += b - a
chorus_bars = []
for i, (a, b) in enumerate(secs):
    if i not in chosen: continue
    if chorus_bars and chorus_bars[-1][1] == a: chorus_bars[-1][1] = b
    else: chorus_bars.append([a, b])
if dream:   # the Zoktay part wins over a chorus: cut it out of any chorus it overlaps
    cut = []
    for a, b in chorus_bars:
        if b <= dream[0] or a >= dream[1]: cut.append([a, b]); continue
        if dream[0] - a >= 2: cut.append([a, dream[0]])
        if b - dream[1] >= 2: cut.append([dream[1], b])
    chorus_bars = cut

# ---- drops: chorus starts (drums return / energy jump) ----
drops = []
for a, b in chorus_bars:
    if a < 2: continue
    jump = dr[a] - dr[a - 1:a].mean()
    if not drops or jump > 0.05 or mr[a] - mr[a - 1] > 0.05 or len(drops) < 2:
        drops.append(a)
if dream and dream[1] < nb - 2 and all(abs(dream[1] - d) > 2 for d in drops): drops.append(dream[1])
drops = sorted(d for d in drops if not (dream and dream[0] <= d < dream[1]))[:4]
# ---- intro / outro ----
first_voc = next((i for i in range(nb) if vr[i] > 0.25 * vr.max()), 4)
last_loud = max(i for i in range(nb) if mr[i] > 0.35 * mr.max())
intro_end = max(4.0, min(7.0, bar_t(max(1, first_voc)) - 0.3))
outro_start = bar_t(last_loud + 1)
if dur - outro_start < 6: outro_start = max(bar_t(nb - 3), dur - 9)

# ---- light story segments ----
lab = ['verse'] * nb
for i in range(nb):
    if dr[i] < 0.35 * dr.max() and mr[i] < 0.6 * mr.max(): lab[i] = 'calm'
for a, b in chorus_bars:
    for i in range(a, b): lab[i] = 'chorus'
for i in range(nb):   # instrumental high-energy bars outside choruses
    if lab[i] == 'verse' and vr[i] < 0.15 * vr.max() and dr[i] > 0.6 * dr.max(): lab[i] = 'hype'
if dream:
    for i in range(*dream): lab[i] = 'dream'
for d in drops:
    if d - 1 >= 0 and lab[d - 1] != 'dream': lab[d - 1] = 'break' if dr[d - 1] < 0.6 * dr[d] else 'build'
first_ch = chorus_bars[0][0] if chorus_bars else nb
for i in range(max(0, first_ch - 3), max(0, first_ch - 1)):
    if lab[i] == 'verse': lab[i] = 'build'
for i in range(0, max(1, first_voc)):
    if lab[i] != 'chorus': lab[i] = 'intro'
# merge one-bar blips into the previous label (keeps break / build / dream)
for i in range(1, nb - 1):
    if lab[i] in ('verse', 'calm', 'hype', 'intro') and lab[i - 1] == lab[i + 1] and lab[i] != lab[i - 1]: lab[i] = lab[i - 1]
segments = []
for i in range(nb):
    t0 = 0.0 if i == 0 else round(bar_t(i), 2)
    if segments and segments[-1][2] == lab[i]: continue
    if segments: segments[-1][1] = t0
    segments.append([t0, None, lab[i]])
segments[-1][1] = round(outro_start, 2)
segments = [s for s in segments if s[1] - s[0] > 0.05]
segments.append([round(outro_start, 2), round(dur + 10, 2), 'outro'])

# ---- hooks ----
sig = []
for t in title_hits:
    if any(bar_t(a) <= t < bar_t(b) for a, b in chorus_bars) and (not sig or t - sig[-1] > 7): sig.append(round(t, 2))
sig = sig[:6]
tw_up = [ascii_only(x).upper() for x in title.split()]
hook_text = [' '.join(tw_up[:len(tw_up) // 2]), ' '.join(tw_up[len(tw_up) // 2:])] if len(tw_up) >= 2 else [tw_up[0], tw_up[0]]

SABERS = [('FF7EB6', 'B48CFF'), ('FF4FA3', '4FC3FF'), ('FF6F91', 'FFC75F'), ('C77DFF', '72EFDD'), ('FF5D8F', '7AE582'), ('FF9E00', 'B5179E')]
sa, sb = opt('--colors', None, 2) or SABERS[int(sid) % len(SABERS)]
cfg = {
    'title': title, 'artist': artist, 'mapper': MAPPER, 'folder': f'{ascii_only(MAPPER)} {sid} ({ascii_only(title)})', 'env': env, 'genre': 'pop', 'max_hearts': 7,
    'words_file': os.path.basename(wf)[:-5],
    'colors': {'a': sa, 'b': sb, 'e0': 'FF4FA3', 'e1': '2E7BFF', 'ew': 'FFD21F', 'e0b': 'FF1F3D', 'e1b': '9B3DFF', 'ewb': '2BFF6A', 'w': 'FFB3D4'},
    'chorus': [[round(bar_t(a), 2), round(bar_t(b), 2)] for a, b in chorus_bars],
    'drops': [round(bar_t(d), 2) for d in drops],
    'burst_times': [round(bar_t(d) - 0.1, 2) for d in drops],
    'signature_times': sig,
    'hook_walls': {'text': hook_text},
    'intro': {'artist': ascii_only(artist).upper(), 'title': ascii_only(title).upper(), 'start': 0.4, 'end': round(intro_end, 2)},
    'outro': {'start': round(outro_start, 2), 'end': round(dur - 0.3, 2), 'text': 'BY ' + ascii_only(MAPPER).upper()},
    'lightstory': {'segments': segments, 'flashes': [round(bar_t(d), 2) for d in drops]},
    'dust': {'cut': True, 'text': True}, 'combo': True,
    'lyrics': {'hooks': [{'re': '^' + key + '$', 'text': ascii_only(title).upper()}] if key else [], 'min_gap': 2.0},
}
if dream:
    a, b = bar_t(dream[0]), bar_t(dream[1])
    cfg['dream'] = [[round(a, 2), round(b, 2)]]
if dream and VIDEO != 'none' and os.path.exists(f'{S}/vid/{VIDEO}'):
    cfg['video'] = {'file': VIDEO, 'start': round(a, 2), 'end': round(b, 2), 'fps': 24, 'mode': 'pingpong', 'x': 0, 'y': 0, 'z': 22, 'height': 7}
if KARAOKE == 'all' and words:
    a, b = max(0.0, words[0]['start'] - 0.5), dur
if (dream and KARAOKE == 'dream') or (KARAOKE == 'all' and words):
    cfg['lyrics'].update({'ranges': [[round(a, 2), round(b, 2)]], 'group': 0.75, 'max_chars': 16, 'min_gap': 0.5, 'min_p': 0.15})
print(json.dumps({'id': sid, 'title': title, 'bars': nb, 'dur': round(dur, 1), 'chorus': cfg['chorus'], 'drops': cfg['drops'], 'zoktay': cfg.get('dream'),
                  'hooks': sig, 'key': key, 'segments': [(s[0], s[2]) for s in segments]}, ensure_ascii=False))
if '--apply' in sys.argv:
    p = songs_path(S)
    C = json.load(open(p, encoding='utf-8-sig')) if os.path.exists(p) else {}
    old = C.get(sid, {})
    keep = {k: v for k, v in old.items() if k in ('words_file', 'lyrics', 'env', 'colors', 'max_hearts', 'heart_chorus', 'words_manual', 'folder', 'title', 'artist', 'subtitle', 'mapper')} if '--keep' in sys.argv else {}
    new = dict(cfg); new.update(keep)
    if '--keep' in sys.argv:
        new['colors'] = dict(cfg['colors'], a=old.get('colors', {}).get('a', cfg['colors']['a']), b=old.get('colors', {}).get('b', cfg['colors']['b']))
        if 'dream' in cfg:
            new['lyrics'] = dict(old.get('lyrics', {}), ranges=cfg['lyrics']['ranges'])
    C[sid] = new
    json.dump(C, open(p, 'w', encoding='utf-8'), indent=1, ensure_ascii=False)

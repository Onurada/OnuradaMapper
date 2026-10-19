"""Normalize all official Standard beatmaps (v2/v3/v4) into corpus.pkl"""
import sys, os, glob, json, pickle, re
CORPUS = sys.argv[1]; META = sys.argv[2]; OUT = sys.argv[3]
meta = json.load(open(META, encoding='utf-8'))
meta_l = {k.lower(): v for k, v in meta.items()}
DIFFS = ['ExpertPlus', 'Expert', 'Hard', 'Normal', 'Easy']
BAD = ['OneSaber', 'NoArrows', '360', '90Degree', 'Legacy', 'Lightshow', 'Lawless']


def g(d, k, default=0):
    v = d.get(k, default)
    return default if v is None else v


def parse_v4(j):
    nd = j.get('colorNotesData', []); bd = j.get('bombNotesData', []); od = j.get('obstaclesData', [])
    ad = j.get('arcsData', []); cd = j.get('chainsData', [])
    notes = [(g(o, 'b', 0.0), g(nd[g(o, 'i')], 'x'), g(nd[g(o, 'i')], 'y'), g(nd[g(o, 'i')], 'c'), g(nd[g(o, 'i')], 'd'), g(nd[g(o, 'i')], 'a')) for o in j.get('colorNotes', [])]
    bombs = [(g(o, 'b', 0.0), g(bd[g(o, 'i')], 'x'), g(bd[g(o, 'i')], 'y')) for o in j.get('bombNotes', [])]
    obst = [(g(o, 'b', 0.0), g(od[g(o, 'i')], 'd', 0.0), g(od[g(o, 'i')], 'x'), g(od[g(o, 'i')], 'y'), g(od[g(o, 'i')], 'w', 1), g(od[g(o, 'i')], 'h', 1)) for o in j.get('obstacles', [])]
    arcs = []
    for o in j.get('arcs', []):
        h = nd[g(o, 'hi')]; t = nd[g(o, 'ti')]; a = ad[g(o, 'ai')]
        arcs.append(dict(c=g(h, 'c'), hb=g(o, 'hb', 0.0), hx=g(h, 'x'), hy=g(h, 'y'), hd=g(h, 'd'), hm=g(a, 'm', 1.0),
                         tb=g(o, 'tb', 0.0), tx=g(t, 'x'), ty=g(t, 'y'), td=g(t, 'd'), tm=g(a, 'tm', 1.0), m=g(a, 'a')))
    chains = []
    for o in j.get('chains', []):
        h = nd[g(o, 'i')]; c = cd[g(o, 'ci')]
        chains.append(dict(c=g(h, 'c'), hb=g(o, 'hb', 0.0), hx=g(h, 'x'), hy=g(h, 'y'), hd=g(h, 'd'),
                           tb=g(o, 'tb', 0.0), tx=g(c, 'tx'), ty=g(c, 'ty'), sc=g(c, 'c', 3), s=g(c, 's', 1.0)))
    return notes, bombs, obst, arcs, chains


def parse_v3(j):
    notes = [(g(o, 'b', 0.0), g(o, 'x'), g(o, 'y'), g(o, 'c'), g(o, 'd'), g(o, 'a')) for o in j.get('colorNotes', [])]
    bombs = [(g(o, 'b', 0.0), g(o, 'x'), g(o, 'y')) for o in j.get('bombNotes', [])]
    obst = [(g(o, 'b', 0.0), g(o, 'd', 0.0), g(o, 'x'), g(o, 'y'), g(o, 'w', 1), g(o, 'h', 1)) for o in j.get('obstacles', [])]
    arcs = [dict(c=g(o, 'c'), hb=g(o, 'b', 0.0), hx=g(o, 'x'), hy=g(o, 'y'), hd=g(o, 'd'), hm=g(o, 'mu', 1.0), tb=g(o, 'tb', 0.0),
                 tx=g(o, 'tx'), ty=g(o, 'ty'), td=g(o, 'tc'), tm=g(o, 'tmu', 1.0), m=g(o, 'm')) for o in j.get('sliders', [])]
    chains = [dict(c=g(o, 'c'), hb=g(o, 'b', 0.0), hx=g(o, 'x'), hy=g(o, 'y'), hd=g(o, 'd'), tb=g(o, 'tb', 0.0), tx=g(o, 'tx'),
                   ty=g(o, 'ty'), sc=g(o, 'sc', 3), s=g(o, 's', 1.0)) for o in j.get('burstSliders', [])]
    return notes, bombs, obst, arcs, chains


def parse_v2(j):
    notes, bombs, obst = [], [], []
    for o in j.get('_notes', []):
        if o['_type'] == 3: bombs.append((o['_time'], o['_lineIndex'], o['_lineLayer']))
        elif o['_type'] in (0, 1): notes.append((o['_time'], o['_lineIndex'], o['_lineLayer'], o['_type'], o['_cutDirection'], 0))
    for o in j.get('_obstacles', []):
        full = o.get('_type', 0) == 0
        obst.append((o['_time'], o['_duration'], o['_lineIndex'], 0 if full else 2, o['_width'], 5 if full else 3))
    return notes, bombs, obst, [], []


out = []
for lvl in sorted(os.listdir(CORPUS)):
    d = os.path.join(CORPUS, lvl)
    m = meta_l.get(lvl.lower())
    bpm = None
    for af in glob.glob(os.path.join(d, '*.audio.json')):
        try:
            aj = json.load(open(af, encoding='utf-8'))
            bd = aj.get('bpmData', [])
            if bd:
                r = bd[0]
                bpm = (r['eb'] - r['sb']) / ((r['ei'] - r['si']) / aj['songFrequency']) * 60.0
                if len(bd) > 1: bpm = -bpm  # variable bpm flag
        except Exception: pass
    if bpm is None and m: bpm = m['bpm']
    if bpm is None: continue
    for f in glob.glob(os.path.join(d, '*.beatmap.json')) + [x for x in glob.glob(os.path.join(d, '*.json')) if '.' not in os.path.basename(x)[:-5] and os.path.basename(x) != 'leveldata.json']:
        base = os.path.basename(f).replace('.beatmap.json', '').replace('.json', '')
        if any(b in base for b in BAD): continue
        diff = next((x for x in DIFFS if base.endswith(x)), None)
        if diff is None: continue
        try: j = json.load(open(f, encoding='utf-8'))
        except Exception: continue
        v = str(j.get('version', j.get('_version', '')))
        try:
            if v.startswith('4'): parsed = parse_v4(j)
            elif v.startswith('3'): parsed = parse_v3(j)
            elif v.startswith('2'): parsed = parse_v2(j)
            else: continue
        except Exception as e:
            print('parsefail', lvl, base, e); continue
        notes, bombs, obst, arcs, chains = parsed
        if len(notes) < 30: continue
        njs, off = None, None
        if m:
            idx = DIFFS[::-1].index(diff)
            for dd in m['diffs']:
                if dd.get('difficulty') == idx: njs, off = dd.get('noteJumpMovementSpeed'), dd.get('noteJumpStartBeatOffset'); break
        out.append(dict(level=lvl, diff=diff, bpm=bpm, env=(m['envs'][0] if m and m['envs'] else None), njs=njs, offset=off,
                        notes=sorted(notes), bombs=sorted(bombs), obst=sorted(obst), arcs=arcs, chains=chains,
                        author=(m or {}).get('author'), name=(m or {}).get('name')))
pickle.dump(out, open(OUT, 'wb'))
import collections
print(len(out), collections.Counter(o['diff'] for o in out))
print('variable bpm maps', sum(1 for o in out if o['bpm'] < 0))
print('arcs', sum(len(o['arcs']) for o in out), 'chains', sum(len(o['chains']) for o in out))

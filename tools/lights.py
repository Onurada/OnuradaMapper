"""Lightshow by transplanting official lightshow segments (same environment) onto song sections."""
import json, glob, os, numpy as np

BOX_KEYS = {1: ('lightColorEventBoxes', 'lightColorEvents'), 2: ('lightRotationEventBoxes', 'lightRotationEvents'),
            3: ('lightTranslationEventBoxes', 'lightTranslationEvents'), 4: ('fxEventBoxes', 'floatFxEvents')}


class Dedup:
    def __init__(self): self.items, self.m = [], {}

    def add(self, d):
        k = json.dumps(d, sort_keys=True)
        if k not in self.m: self.m[k] = len(self.items); self.items.append(d)
        return self.m[k]


class LightBuilder:
    def __init__(self):
        self.basic = []; self.boost = []; self.groups = []
        self.bd, self.cbd, self.filt = Dedup(), Dedup(), Dedup()
        self.boxes = {t: Dedup() for t in BOX_KEYS}; self.evs = {t: Dedup() for t in BOX_KEYS}

    def add_basic(self, src, ev, b):
        self.basic.append({'b': round(b, 4), 'i': self.bd.add(src['basicEventsData'][ev.get('i', 0)])})

    def add_boost(self, src, ev, b):
        self.boost.append({'b': round(b, 4), 'i': self.cbd.add(src['colorBoostEventsData'][ev.get('i', 0)])})

    def add_group(self, src, g, b):
        t = g.get('t', 1)
        bk, ek = BOX_KEYS[t]
        ne = []
        for e in g.get('e', []):
            f = self.filt.add(src['indexFilters'][e.get('f', 0)])
            bx = self.boxes[t].add(src[bk][e.get('e', 0)])
            ll = [dict({'i': self.evs[t].add(src[ek][l.get('i', 0)])}, **({'b': l['b']} if l.get('b') else {})) for l in e.get('l', [])]
            ne.append({'f': f, 'e': bx, 'l': ll})
        self.groups.append({'b': round(b, 4), 'g': g.get('g', 0), 't': t, 'e': ne})

    def json(self):
        out = {'version': '4.0.0', 'waypoints': [], 'waypointsData': [],
               'basicEvents': sorted(self.basic, key=lambda e: e['b']), 'basicEventsData': self.bd.items,
               'colorBoostEvents': sorted(self.boost, key=lambda e: e['b']), 'colorBoostEventsData': self.cbd.items,
               'eventBoxGroups': sorted(self.groups, key=lambda e: (e['b'], e['g'], e['t'])), 'indexFilters': self.filt.items}
        for t, (bk, ek) in BOX_KEYS.items():
            out[bk] = self.boxes[t].items; out[ek] = self.evs[t].items
        out['basicEventTypesWithKeywords'] = {'d': []}
        out['useNormalEventsAsCompatibleEvents'] = True
        return out


def load_donor(path):
    j = json.load(open(path, encoding='utf-8'))
    for e in j.get('basicEvents', []): e.setdefault('b', 0.0)
    for e in j.get('colorBoostEvents', []): e.setdefault('b', 0.0)
    for e in j.get('eventBoxGroups', []): e.setdefault('b', 0.0)
    return j


def copy_segment(lb, src, s, e, dst, snapshot=True, dst_end=None):
    """copy donor events in [s,e) to dst. snapshot: replay last state before s at dst."""
    d = dst - s
    if snapshot:
        last_basic = {}
        for ev in src['basicEvents']:
            if ev['b'] < s - 1e-4:
                data = src['basicEventsData'][ev.get('i', 0)]
                last_basic[data.get('t', 0)] = ev
        for t, ev in last_basic.items():
            # rotation-speed / ring events are fine to replay; skip ring rotations (8,9) which are triggers
            data = src['basicEventsData'][ev.get('i', 0)]
            if data.get('t', 0) in (8, 9): continue
            lb.add_basic(src, ev, dst)
        lastb = [ev for ev in src.get('colorBoostEvents', []) if ev['b'] < s - 1e-4]
        if lastb: lb.add_boost(src, lastb[-1], dst)
        last_g = {}
        for g in src.get('eventBoxGroups', []):
            if g['b'] < s - 1e-4: last_g[(g.get('g', 0), g.get('t', 1))] = g
        for g in last_g.values():
            lb.add_group(src, g, dst)
    for ev in src['basicEvents']:
        if s - 1e-4 <= ev['b'] < e - 1e-4: lb.add_basic(src, ev, ev['b'] + d)
    for ev in src.get('colorBoostEvents', []):
        if s - 1e-4 <= ev['b'] < e - 1e-4: lb.add_boost(src, ev, ev['b'] + d)
    for g in src.get('eventBoxGroups', []):
        if s - 1e-4 <= g['b'] < e - 1e-4: lb.add_group(src, g, g['b'] + d)


def donor_bar_intensity(corpus_map, phase, nbars):
    """note density per bar of donor (from its hardest beatmap)"""
    cnt = np.zeros(nbars)
    for n in corpus_map['notes']:
        i = int((n[0] - phase) // 4)
        if 0 <= i < nbars: cnt[i] += 1
    if cnt.max() > 0: cnt = cnt / np.percentile(cnt[cnt > 0], 90)
    return np.clip(cnt, 0, 1.3)


def light_event_density(src, phase, nbars):
    cnt = np.zeros(nbars)
    for ev in src['basicEvents'] + src.get('eventBoxGroups', []):
        i = int((ev['b'] - phase) // 4)
        if 0 <= i < nbars: cnt[i] += 1
    if cnt.max() > 0: cnt = cnt / np.percentile(cnt[cnt > 0], 90)
    return np.clip(cnt, 0, 1.3)


def build_lightshow(donors, my_int, sections, rep_sec, bar_beat0, song_end_beat, rng):
    """donors: list of dict(src=lightshow json, inten=array per bar, phase=float, name)
    my_int: intensity per bar (0..1). sections: list of (a, b) bar ranges. rep_sec: section index -> earlier section idx or -1"""
    lb = LightBuilder()
    chosen = {}
    used = set()
    prev_end = None
    for si, (a, b) in enumerate(sections):
        L = b - a
        if rep_sec[si] >= 0 and rep_sec[si] in chosen and sections[rep_sec[si]][1] - sections[rep_sec[si]][0] >= L:
            dn, ds = chosen[rep_sec[si]]
        else:
            best = None
            target = np.array(my_int[a:b])
            for dn, D in enumerate(donors):
                inten = D['inten']
                nb = len(inten)
                for ds in range(0, max(1, nb - L + 1)):
                    seg = inten[ds:ds + L]
                    if len(seg) < L: continue
                    err = float(np.mean((seg - target) ** 2))
                    # prefer starting at donor phrase boundaries (multiples of 4 bars) and real changes
                    if ds % 4: err += 0.04
                    if ds > 0 and abs(inten[ds] - inten[ds - 1]) < 0.15 and abs(target[0] - (my_int[a - 1] if a > 0 else 0)) > 0.25: err += 0.05
                    if (dn, ds) in used: err += 0.08
                    if prev_end is not None and (dn, ds) == prev_end: err -= 0.045   # keep one donor flowing
                    elif prev_end is not None and dn == prev_end[0]: err -= 0.01
                    if si == 0 and ds != 0: err += 0.03
                    err += rng.random() * 0.01
                    if best is None or err < best[0]: best = (err, dn, ds)
            _, dn, ds = best
        chosen[si] = (dn, ds)
        prev_end = (dn, ds + L)
        for k in range(L): used.add((dn, ds + k))
        D = donors[dn]
        s = D['phase'] + 4 * ds
        e = s + 4 * L
        dst = bar_beat0 + 4 * a
        lead = min(dst, s) if si == 0 else 0.0  # first section also covers the lead-in before bar 0
        copy_segment(lb, D['src'], s - lead, e, dst - lead, snapshot=True)
    # outro: copy nothing past song end; trim
    lb.basic = [e for e in lb.basic if e['b'] <= song_end_beat]
    lb.boost = [e for e in lb.boost if e['b'] <= song_end_beat]
    lb.groups = [e for e in lb.groups if e['b'] <= song_end_beat]
    return lb, chosen

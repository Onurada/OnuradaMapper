"""Audio analysis for one song: tempo/grid, per-slot onset features per stem, bar features, sections.
usage: analyze.py <mix.wav> <stems_dir> <out_prefix> [bpm_hint]
"""
import sys, json, numpy as np, librosa, scipy.signal as ss

mix_path, stem_dir, outp = sys.argv[1], sys.argv[2], sys.argv[3]
bpm_hint = float(sys.argv[4]) if len(sys.argv) > 4 and sys.argv[4] != '-' else None
SR = 22050
HOP = 128   # ~5.8ms


def load(p):
    y, _ = librosa.load(p, sr=SR, mono=True)
    return y


mix = load(mix_path)
stems = {k: load(f'{stem_dir}/{k}.wav') for k in ['drums', 'bass', 'other', 'vocals']}
dur = len(mix) / SR

# ---------- onset envelopes ----------
def flux(y, fmin=None, fmax=None, n_mels=96):
    S = librosa.feature.melspectrogram(y=y, sr=SR, hop_length=HOP, n_fft=2048, n_mels=n_mels, fmin=fmin or 20, fmax=fmax or SR // 2)
    L = librosa.power_to_db(S, ref=np.max)
    d = np.maximum(0, np.diff(L, axis=1, prepend=L[:, :1]))
    e = d.mean(axis=0)
    e = e - ss.medfilt(e, 31) * 0.5
    return np.maximum(e, 0)

env = {
    'kick': flux(stems['drums'], 20, 150, 32),
    'snare': flux(stems['drums'], 150, 5000, 64),
    'hat': flux(stems['drums'], 5000, 11000, 32),
    'bass': flux(stems['bass'], 20, 400, 32),
    'other': flux(stems['other']),
    'vocals': flux(stems['vocals'], 100, 8000),
    'mix': flux(mix),
}
# vocal activity (rms), mix rms
def rms(y):
    return librosa.feature.rms(y=y, frame_length=1024, hop_length=HOP)[0]
vrms = rms(stems['vocals']); mrms = rms(mix); drms = rms(stems['drums']); brms = rms(stems['bass'])
nfr = min(len(v) for v in list(env.values()) + [vrms, mrms, drms, brms])
for k in env: env[k] = env[k][:nfr]
vrms, mrms, drms, brms = vrms[:nfr], mrms[:nfr], drms[:nfr], brms[:nfr]
ftimes = librosa.frames_to_time(np.arange(nfr), sr=SR, hop_length=HOP)

# ---------- tempo ----------
oenv = librosa.onset.onset_strength(y=mix, sr=SR, hop_length=512)
drum_oenv = librosa.onset.onset_strength(y=stems['drums'], sr=SR, hop_length=512)
use = drum_oenv if drms.mean() > 0.2 * mrms.mean() else oenv
tempo, beats = librosa.beat.beat_track(onset_envelope=use, sr=SR, hop_length=512, start_bpm=bpm_hint or 120, tightness=200)
bt = librosa.frames_to_time(beats, sr=SR, hop_length=512)
# robust linear fit of beat times
idx = np.arange(len(bt))
for _ in range(5):
    A = np.vstack([idx, np.ones_like(idx)]).T
    p, *_ = np.linalg.lstsq(A, bt, rcond=None)
    res = bt - A @ p
    keep = np.abs(res) < max(0.03, 2.5 * np.std(res))
    if keep.all(): break
    idx, bt = idx[keep], bt[keep]
period = p[0]
bpm = 60.0 / period
if bpm_hint:
    # allow octave correction toward hint
    for f in (0.5, 2.0, 2/3., 1.5):
        if abs(bpm * f - bpm_hint) < abs(bpm - bpm_hint): bpm = bpm * f
    period = 60.0 / bpm


# refine bpm + phase by maximizing grid alignment with summed onset env
comb = env['kick'] / (env['kick'].max() + 1e-9) + env['snare'] / (env['snare'].max() + 1e-9) + 0.5 * env['mix'] / (env['mix'].max() + 1e-9)
comb = np.convolve(comb, np.hanning(5) / np.hanning(5).sum(), mode='same')

def align_score(bpm_, ph):
    per = 60 / bpm_
    ts = np.arange(ph, dur, per / 2)
    fi = np.clip(np.round(ts * SR / HOP).astype(int), 0, nfr - 1)
    return comb[fi].sum()

best = None
for b_ in np.linspace(bpm * 0.997, bpm * 1.003, 31):
    per = 60 / b_
    for ph in np.linspace(0, per, 96, endpoint=False):
        s = align_score(b_, ph)
        if best is None or s > best[0]: best = (s, b_, ph)
_, bpm, phase = best
# round bpm to nice value if very close
for q in (1.0, 0.5, 0.25, 0.1):
    r = round(bpm / q) * q
    if abs(r - bpm) / bpm < 0.0004:
        # re-fit phase for rounded bpm
        per = 60 / r
        cands = [(align_score(r, ph), ph) for ph in np.linspace(0, per, 192, endpoint=False)]
        s_r, ph_r = max(cands)
        if s_r > 0.985 * best[0]: bpm, phase = r, ph_r
        break
period = 60 / bpm

# beat vs off-beat: the half-beat comb above can't tell them apart. Kicks+snares sit on the beats.
ks_env = env['kick'] / (np.percentile(env['kick'], 99) + 1e-9) + env['snare'] / (np.percentile(env['snare'], 99) + 1e-9)
def phase_strength(ph):
    ts = np.arange(ph, dur, period)
    fi = np.clip(np.round(ts * SR / HOP).astype(int), 0, nfr - 1)
    return float(np.mean([ks_env[max(0, i - 3):i + 4].max() for i in fi]))
qs = [phase_strength(phase + q * period / 4) for q in range(4)]
phase_shift_q = int(np.argmax(qs)) if max(qs) > 1.12 * qs[0] else 0
phase = (phase + phase_shift_q * period / 4) % period

# downbeat: which of 4 beat phases has strongest kick+bass + harmonic change
beat_times = np.arange(phase, dur, period)
fi = np.clip(np.round(beat_times * SR / HOP).astype(int), 0, nfr - 1)
chroma = librosa.feature.chroma_cqt(y=stems['other'] + stems['bass'], sr=SR, hop_length=512)
cf = np.clip(np.round(beat_times * SR / 512).astype(int), 0, chroma.shape[1] - 1)
ch = chroma[:, cf]
chd = np.r_[0, np.linalg.norm(np.diff(ch, axis=1), axis=0)]
kick_b = np.array([env['kick'][max(0, i - 3):i + 4].max() for i in fi])
bass_b = np.array([env['bass'][max(0, i - 3):i + 4].max() for i in fi])
crash = np.array([env['hat'][max(0, i - 3):i + 4].max() for i in fi])
sc = []
for k in range(4):
    sel = np.arange(k, len(beat_times), 4)
    sc.append(np.mean(kick_b[sel]) / (kick_b.mean() + 1e-9) + np.mean(bass_b[sel]) / (bass_b.mean() + 1e-9) + 1.5 * np.mean(chd[sel]) / (chd.mean() + 1e-9))
db = int(np.argmax(sc))
first_downbeat = phase + db * period

# ---------- slot features (1/16 beat grid, measured from first_downbeat - 8 bars) ----------
SUB = 16
grid0 = first_downbeat - np.floor(first_downbeat / (4 * period)) * 4 * period  # earliest downbeat >= 0
nslots = int((dur - grid0) / period * SUB) + 1

# ---------- beat warp: follow real drum attacks (time domain) ----------
import soundfile as sf
yd, srd = sf.read(f'{stem_dir}/drums.wav')
if yd.ndim > 1: yd = yd.mean(1)
whop, wwin = 32, 256
we = np.sqrt(np.convolve(yd ** 2, np.ones(wwin) / wwin, mode='valid')[::whop])
wd = np.diff(np.log(we + 1e-5), prepend=0)
e60 = np.percentile(we, 60)
nbt = int((dur - grid0) / period) + 3
ub = grid0 + np.arange(nbt) * period
obs = []  # (beat index float, offset seconds)
r = int(min(0.12, period * 0.24) * srd / whop)
for k in range(nbt):
    for sub_ in (0.0, 0.5):
        tt = ub[k] + sub_ * period
        c = int(tt * srd / whop)
        a, b = max(0, c - r), min(len(wd) - 1, c + r)
        if b <= a: continue
        seg = wd[a:b]; j = int(np.argmax(seg))
        if seg[j] > 0.15 and we[a + j] > e60:
            obs.append((k + sub_, (a + j) * whop / srd + wwin / 2 / srd - tt))
obs = np.array(obs) if obs else np.zeros((0, 2))
delta = np.full(nbt, np.nan)
for k in range(nbt):
    sel = obs[np.abs(obs[:, 0] - k) <= 6, 1] if len(obs) else []
    if len(sel) >= 5: delta[k] = np.median(sel)
if np.isnan(delta).all(): delta[:] = 0.0
else:
    ok = ~np.isnan(delta)
    delta = np.interp(np.arange(nbt), np.arange(nbt)[ok], delta[ok])
delta = np.convolve(np.pad(delta, 3, mode='edge'), np.ones(7) / 7, mode='valid')
gdelta = float(np.median(obs[:, 1])) if len(obs) else 0.0
warped = '--warp' in sys.argv and bool(np.std(delta) > 0.007)
lin_b = 0.0
if len(obs) > 30:
    kx, oy = obs[:, 0], obs[:, 1]
    msk = np.abs(oy - np.median(oy)) < 0.03
    for _ in range(4):
        cb, ca = np.polyfit(kx[msk], oy[msk], 1)
        res_ = oy - (ca + cb * kx)
        msk = np.abs(res_) < max(0.012, 2.5 * np.median(np.abs(res_[msk])))
    lin_b = float(cb)
    lin = ca + cb * np.arange(nbt)
else:
    lin = np.full(nbt, gdelta)
if not warped:
    delta = lin if abs(lin_b) * nbt > 0.012 else np.full(nbt, gdelta)
beat_t = ub + delta
# feature envelopes (spectral flux) peak ~gdelta earlier than true attacks -> sample them shifted
bt_feat = beat_t - gdelta
kk = np.arange(nslots) / SUB
slot_t = np.interp(kk, np.arange(nbt), bt_feat)

def sample(e, win=3):
    e = e / (np.percentile(e, 99.5) + 1e-9)
    out = np.zeros(nslots)
    idx = np.round(slot_t * SR / HOP).astype(int)
    for i, c in enumerate(idx):
        a, b = max(0, c - win), min(nfr, c + win + 1)
        out[i] = e[a:b].max() if b > a else 0
    return np.clip(out, 0, 2)

feat = {k: sample(v) for k, v in env.items()}
def sample_lvl(e):
    idx = np.clip(np.round(slot_t * SR / HOP).astype(int), 0, nfr - 1)
    return e[idx]
feat['vrms'] = sample_lvl(vrms / (np.percentile(vrms, 98) + 1e-9))
feat['mrms'] = sample_lvl(mrms / (np.percentile(mrms, 98) + 1e-9))
feat['drms'] = sample_lvl(drms / (np.percentile(drms, 98) + 1e-9))
feat['brms'] = sample_lvl(brms / (np.percentile(brms, 98) + 1e-9))

# ---------- bar level ----------
bar_len = SUB * 4
nbars = nslots // bar_len
def barmean(a): return np.array([a[i * bar_len:(i + 1) * bar_len].mean() for i in range(nbars)])
bars = {k: barmean(feat[k]).tolist() for k in ['mrms', 'vrms', 'drms', 'brms']}
# bar-synchronous chroma+mfcc for similarity
mf = librosa.feature.mfcc(y=mix, sr=SR, hop_length=512, n_mfcc=20)
chm = librosa.feature.chroma_cqt(y=mix, sr=SR, hop_length=512)
def barvec(M):
    v = []
    for i in range(nbars):
        a = int((grid0 + i * 4 * period) * SR / 512); b = int((grid0 + (i + 1) * 4 * period) * SR / 512)
        a = min(a, M.shape[1] - 1); b = max(a + 1, min(b, M.shape[1]))
        # 4 sub-vectors (per beat) to keep rhythm/harmony order
        segs = np.array_split(M[:, a:b], 4, axis=1)
        v.append(np.concatenate([s.mean(axis=1) if s.shape[1] else np.zeros(M.shape[0]) for s in segs]))
    return np.array(v)
BC = barvec(chm); BM = barvec(mf[1:])
def zn(X):
    X = X - X.mean(axis=0); X = X / (np.linalg.norm(X, axis=1, keepdims=True) + 1e-9); return X
BV = np.hstack([zn(BC), 0.7 * zn(BM)])
BV = BV / (np.linalg.norm(BV, axis=1, keepdims=True) + 1e-9)
SIM = BV @ BV.T
# onset-rhythm similarity of bars (drums+vocals)
rv = np.array([np.concatenate([feat['kick'][i*bar_len:(i+1)*bar_len], feat['snare'][i*bar_len:(i+1)*bar_len], feat['vocals'][i*bar_len:(i+1)*bar_len]]) for i in range(nbars)])
rv = rv / (np.linalg.norm(rv, axis=1, keepdims=True) + 1e-9)
RSIM = rv @ rv.T

# novelty (checkerboard on SIM) for section boundaries
K = 4
nov = np.zeros(nbars)
for i in range(nbars):
    a, b = max(0, i - K), min(nbars, i + K)
    if i - a < 2 or b - i < 2: continue
    within = (SIM[a:i, a:i].mean() + SIM[i:b, i:b].mean()) / 2
    across = SIM[a:i, i:b].mean()
    nov[i] = within - across
# energy jumps
en = np.array(bars['mrms']) + 0.5 * np.array(bars['drms'])
ej = np.zeros(nbars)
for i in range(1, nbars):
    ej[i] = abs(np.mean(en[i:i + 2]) - np.mean(en[max(0, i - 2):i]))
score = nov / (nov.max() + 1e-9) + ej / (ej.max() + 1e-9)
bounds = [0]
cands = sorted(range(1, nbars), key=lambda i: -score[i])
for i in cands:
    if score[i] < 0.35: break
    if all(abs(i - b) >= 4 for b in bounds): bounds.append(i)
bounds = sorted(bounds) + [nbars]

json.dump(dict(bpm=bpm, period=period, grid0=grid0, first_downbeat=first_downbeat, dur=dur, sub=SUB, nslots=nslots,
               nbars=nbars, bars=bars, beat_times=beat_t.tolist(), warped=warped, gdelta=gdelta, delta_std=float(np.std(delta)), bounds=bounds, novelty=score.tolist(), downbeat_scores=sc),
          open(outp + '.json', 'w'), indent=1, default=float)
np.savez_compressed(outp + '.npz', SIM=SIM, RSIM=RSIM, **{k: v.astype(np.float32) for k, v in feat.items()})
print(f'phaseq={phase_shift_q} qs={[round(q, 3) for q in qs]} bpm={bpm:.3f} effbpm={60/np.mean(np.diff(beat_t)):.3f} slope_total={lin_b*nbt*1000:.1f}ms grid0={grid0:.3f} bars={nbars} warped={warped} gdelta={gdelta*1000:.1f}ms dstd={np.std(delta)*1000:.1f}ms bounds={bounds}')


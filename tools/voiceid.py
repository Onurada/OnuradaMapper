"""Find a singer's parts by voice: ECAPA speaker embeddings on the vocal stem, compared with a reference passage.
usage: voiceid.py S ref_id ref_start ref_end [neg_start neg_end] -- id [id ...]
Prints per song the segments whose voice matches the reference (song seconds) and writes voice/<id>.json."""
import sys, os, json, numpy as np, torch, librosa
sys.stdout.reconfigure(encoding='utf-8')
from speechbrain.inference.speaker import EncoderClassifier
from speechbrain.utils.fetching import LocalStrategy
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bslib import device

S = sys.argv[1]
sep = sys.argv.index('--')
ref_id, rs, re_ = sys.argv[2], float(sys.argv[3]), float(sys.argv[4])
neg = (float(sys.argv[5]), float(sys.argv[6])) if sep > 5 else None
ids = sys.argv[sep + 1:]
enc = EncoderClassifier.from_hparams('speechbrain/spkrec-ecapa-voxceleb', savedir=os.path.join(S, 'models', 'ecapa'),
                                     run_opts={'device': device()}, local_strategy=LocalStrategy.COPY)
WIN, HOP, SR = 2.0, 0.5, 16000


def windows(sid):
    y, _ = librosa.load(f'{S}/stems/htdemucs/{sid}/vocals.wav', sr=SR, mono=True)
    n, h = int(WIN * SR), int(HOP * SR)
    starts = np.arange(0, max(1, len(y) - n), h)
    rms = np.array([np.sqrt(np.mean(y[s:s + n] ** 2)) for s in starts])
    act = rms > max(0.004, 0.25 * np.percentile(rms, 90))
    embs = np.zeros((len(starts), 192), np.float32)
    idx = np.nonzero(act)[0]
    for k in range(0, len(idx), 64):
        b = idx[k:k + 64]
        batch = torch.tensor(np.stack([y[starts[i]:starts[i] + n] for i in b])).float().to(device())
        with torch.no_grad():
            e = enc.encode_batch(batch).squeeze(1).cpu().numpy()
        embs[b] = e / np.linalg.norm(e, axis=1, keepdims=True)
    return starts / SR + WIN / 2, act, embs


t0, a0, e0 = windows(ref_id)
sel = (t0 >= rs) & (t0 <= re_) & a0
ref = e0[sel].mean(0); ref /= np.linalg.norm(ref)
negv = None
if neg:
    seln = (t0 >= neg[0]) & (t0 <= neg[1]) & a0
    negv = e0[seln].mean(0); negv /= np.linalg.norm(negv)
os.makedirs(f'{S}/voice', exist_ok=True)
for sid in ids:
    t, act, emb = windows(sid)
    score = emb @ ref - (0.6 * (emb @ negv) if negv is not None else 0)
    score[~act] = np.nan
    # smooth over ~4 s, ignoring silent windows
    k = 8
    sm = np.array([np.nanmean(score[max(0, i - k):i + k + 1]) if np.isfinite(score[max(0, i - k):i + k + 1]).any() else np.nan for i in range(len(score))])
    fin = sm[np.isfinite(sm)]
    thr = max(np.percentile(fin, 75), np.nanmean(fin) + 0.8 * np.nanstd(fin)) if len(fin) else 1
    on = np.nan_to_num(sm, nan=-9) > thr
    segs, i = [], 0
    while i < len(on):
        if on[i]:
            j = i
            while j + 1 < len(on) and (on[j + 1] or (j + 3 < len(on) and on[j + 3])): j += 1
            if t[j] - t[i] >= 6: segs.append([round(float(t[i]) - 1, 2), round(float(t[j]) + 1, 2), round(float(np.nanmean(sm[i:j + 1])), 3)])
            i = j + 1
        else: i += 1
    json.dump({'t': [round(float(x), 2) for x in t], 'score': [None if not np.isfinite(x) else round(float(x), 3) for x in sm], 'thr': float(thr), 'segments': segs},
              open(f'{S}/voice/{sid}.json', 'w'), indent=0)
    print(sid, 'thr %.3f' % thr, 'segments', segs, flush=True)

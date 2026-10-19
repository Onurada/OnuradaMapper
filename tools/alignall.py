"""Refine whisper word times with MMS forced alignment for every song's word file.
usage: alignall.py S id:wordsfile [...]   -> writes words/<wordsfile>_al.json (same schema, corrected starts)"""
import sys, os, json, re, numpy as np, torch, torchaudio, librosa
sys.stdout.reconfigure(encoding='utf-8')
sys.path.insert(0, os.path.dirname(__file__))
from bslib import ascii_only, device
S = sys.argv[1]
dev = device()
bundle = torchaudio.pipelines.MMS_FA
model = bundle.get_model(with_star=False).to(dev)
tok = bundle.get_tokenizer(); aligner = bundle.get_aligner()


def clean(w): return re.sub(r"[^a-z']", '', ascii_only(w).lower())


for spec in sys.argv[2:]:
    sid, wf = spec.split(':')
    J = json.load(open(f'{S}/words/{wf}.json', encoding='utf-8'))
    words = J['words']
    y, _ = librosa.load(f'{S}/stems/htdemucs/{sid}/vocals.wav', sr=16000, mono=True)
    chunks, cur = [], []
    for i, w in enumerate(words):
        if cur and (w['start'] - words[cur[-1]]['start'] > 1.6 or len(cur) >= 14):
            chunks.append(cur); cur = []
        cur.append(i)
    if cur: chunks.append(cur)
    aligned = {}
    for ch in chunks:
        a = max(0.0, words[ch[0]]['start'] - 0.5); b = min(len(y) / 16000, words[ch[-1]]['start'] + 1.6)
        keep = [i for i in ch if clean(words[i]['word'])]
        if not keep or b - a < 0.3: continue
        seg = torch.tensor(y[int(a * 16000):int(b * 16000)]).unsqueeze(0).to(dev)
        try:
            with torch.inference_mode():
                em, _ = model(seg)
            em = torch.log_softmax(em, dim=-1)
            spans = aligner(em[0], tok([clean(words[i]['word']) for i in keep]))
        except Exception as e:
            print(sid, 'chunk fail', e); continue
        ratio = seg.shape[1] / em.shape[1] / 16000
        for j, i in enumerate(keep):
            aligned[i] = (spans[j][0].start * ratio + a, spans[j][-1].end * ratio + a)
    diffs = np.array([aligned[i][0] - words[i]['start'] for i in aligned])
    med = float(np.median(diffs)) if len(diffs) else 0.15
    out = []
    bad = 0
    for i, w in enumerate(words):
        w2 = dict(w)
        if i in aligned and abs(aligned[i][0] - w['start'] - med) < 0.6:
            w2['start'], w2['end'] = round(aligned[i][0], 3), round(aligned[i][1], 3)
        else:
            w2['start'] = round(w['start'] + med, 3); bad += 1
        out.append(w2)
    # keep order monotonic
    for i in range(1, len(out)):
        if out[i]['start'] < out[i - 1]['start']: out[i]['start'] = out[i - 1]['start'] + 0.01
    J2 = dict(J); J2['words'] = out
    json.dump(J2, open(f'{S}/words/{wf}_al.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    print(f'{sid} {wf}: {len(words)} words, aligned {len(aligned)}, fallback {bad}, median shift {med:+.3f}s', flush=True)

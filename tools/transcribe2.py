"""Targeted transcription. spec: id|lang|path|start|end|vad(0/1)|prompt"""
import sys, os, json, site, glob
for sp in site.getsitepackages():
    for d in glob.glob(os.path.join(sp, 'nvidia', '*', 'bin')):
        os.add_dll_directory(d); os.environ['PATH'] = d + os.pathsep + os.environ['PATH']
from faster_whisper import WhisperModel
import librosa
sys.stdout.reconfigure(encoding='utf-8')
out = sys.argv[1]
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bslib import device
dev = device()
model = WhisperModel(os.environ.get('BSM_WHISPER', 'large-v3'), device=dev, compute_type='float16' if dev == 'cuda' else 'int8')
for spec in sys.argv[2:]:
    sid, lang, path, st, en, vad, prompt = spec.split('|')
    st, en = float(st), float(en)
    audio, _ = librosa.load(path, sr=16000, mono=True, offset=st, duration=(en - st) if en > 0 else None)
    segs, info = model.transcribe(audio, language=None if lang == 'auto' else lang, word_timestamps=True, vad_filter=vad == '1',
                                  beam_size=8, condition_on_previous_text=False, initial_prompt=prompt or None,
                                  no_speech_threshold=0.9, log_prob_threshold=-2.0, compression_ratio_threshold=3.0)
    words, lines = [], []
    for s in segs:
        lines.append(dict(start=s.start + st, end=s.end + st, text=s.text.strip()))
        for w in (s.words or []):
            words.append(dict(start=w.start + st, end=w.end + st, word=w.word.strip(), p=w.probability))
    json.dump(dict(lang=info.language, words=words, lines=lines), open(os.path.join(out, sid + '.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    print('=====', sid, info.language, len(words), flush=True)
    for l in lines: print(f"{l['start']:6.1f}-{l['end']:6.1f} {l['text']}", flush=True)

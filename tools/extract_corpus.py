"""Extract beatmaps / lightshows / audio data of all official levels into corpus/<level>/"""
import sys, os, gzip, json, glob, UnityPy
root = sys.argv[1]   # Beat Saber dir
out = sys.argv[2]
srcs = []
for d in glob.glob(os.path.join(root, 'DLC', 'Levels', '*')):
    for f in glob.glob(os.path.join(d, '*')):
        srcs.append((os.path.basename(d), f))
for f in glob.glob(os.path.join(root, 'Beat Saber_Data', 'StreamingAssets', 'BeatmapLevelsData', '*')):
    srcs.append((os.path.basename(f), f))
ok = 0
for name, f in srcs:
    od = os.path.join(out, name)
    if os.path.isdir(od) and os.listdir(od):
        ok += 1; continue
    try:
        env = UnityPy.load(f)
    except Exception as e:
        print('LOADFAIL', name, e); continue
    os.makedirs(od, exist_ok=True)
    for obj in env.objects:
        if obj.type.name == 'TextAsset':
            data = obj.read()
            raw = data.m_Script
            if isinstance(raw, str): raw = raw.encode('utf-8', 'surrogateescape')
            nm = data.m_Name
            if nm.endswith('.gz'):
                try: raw = gzip.decompress(raw); nm = nm[:-3]
                except Exception: pass
            open(os.path.join(od, nm + '.json'), 'wb').write(raw)
        elif obj.type.name == 'MonoBehaviour':
            try:
                tree = obj.read_typetree()
                if '_difficultyBeatmapSets' in tree:
                    json.dump(tree, open(os.path.join(od, 'leveldata.json'), 'w'), default=str)
            except Exception:
                pass
    ok += 1
print('done', ok, 'of', len(srcs))

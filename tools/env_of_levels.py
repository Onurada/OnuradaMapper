"""Collect metadata (environment, colors, njs) of every official level."""
import sys, os, glob, json, UnityPy
aa = sys.argv[1]; out = sys.argv[2]
levels = {}
for f in glob.glob(os.path.join(aa, '*pack_assets*.bundle')):
    bn = os.path.basename(f)
    try: env = UnityPy.load(f)
    except Exception: continue
    for obj in env.objects:
        if obj.type.name != 'MonoBehaviour': continue
        try: tree = obj.read_typetree()
        except Exception: continue
        if '_levelID' not in tree: continue
        envs = [e['_environmentName'] for e in tree.get('_environmentNames', [])]
        if not envs and tree.get('_environmentName', {}).get('_environmentName'):
            envs = [tree['_environmentName']['_environmentName']]
        diffs = []
        for s in tree.get('_previewDifficultyBeatmapSets', []):
            for d in s['_previewDifficultyBeatmaps']:
                diffs.append({k.strip('_'): v for k, v in d.items()})
        levels[tree['_levelID']] = {
            'pack': bn.split('_assets')[0], 'bpm': tree.get('_beatsPerMinute'), 'author': tree.get('_songAuthorName'),
            'name': tree.get('_songName'), 'envs': envs, 'colorSchemes': tree.get('_colorSchemes', []),
            'diffs': diffs, 'duration': tree.get('_songDuration')}
json.dump(levels, open(out, 'w'), indent=1)
import collections
print(len(levels))
print(collections.Counter(e for v in levels.values() for e in v['envs']))

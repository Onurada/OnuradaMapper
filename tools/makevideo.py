"""Turn a video file into the OBC3 frame sequence the mod's video billboard plays.
usage: makevideo.py <video> <out.bc3> [seconds=10] [fps=24] [max_side=640] [start=0]
The clip is scaled to fit max_side (sides rounded to multiples of 4 for BC3) and played ping-pong in the dream section."""
import sys, os, subprocess, tempfile, shutil, glob
from PIL import Image

src, out = sys.argv[1], sys.argv[2]
secs = float(sys.argv[3]) if len(sys.argv) > 3 else 10.0
fps = int(sys.argv[4]) if len(sys.argv) > 4 else 24
side = int(sys.argv[5]) if len(sys.argv) > 5 else 640
start = float(sys.argv[6]) if len(sys.argv) > 6 else 0.0
tmp = tempfile.mkdtemp(prefix='bsm_vid_')
try:
    subprocess.run(['ffmpeg', '-y', '-loglevel', 'error', '-ss', str(start), '-t', str(secs), '-i', src,
                    '-vf', f'fps={fps},scale={side}:{side}:force_original_aspect_ratio=decrease', os.path.join(tmp, 'f_%05d.png')], check=True)
    frames = sorted(glob.glob(os.path.join(tmp, '*.png')))
    if not frames: sys.exit('no frames decoded from ' + src)
    w, h = Image.open(frames[0]).size
    W, H = max(4, w // 4 * 4), max(4, h // 4 * 4)
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    subprocess.run([sys.executable, os.path.join(os.path.dirname(os.path.abspath(__file__)), 'packbc3.py'), tmp, out, str(W), str(H), str(fps)], check=True)
finally:
    shutil.rmtree(tmp, ignore_errors=True)

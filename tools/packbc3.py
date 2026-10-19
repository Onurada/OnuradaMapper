"""Pack RGBA PNG frames into OBC3 (BC3/DXT5, Unity bottom-up row order). usage: packbc3.py <rgba_dir> <out.bc3> <width> <height> <fps>"""
import sys, glob, struct, numpy as np, etcpak
from PIL import Image
src, out, W, H, fps = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5])
files = sorted(glob.glob(src + '/*.png'))
with open(out, 'wb') as f:
    f.write(b'OBC3'); f.write(struct.pack('<iiii', W, H, len(files), fps))
    for fn in files:
        im = Image.open(fn).convert('RGBA').resize((W, H), Image.LANCZOS)
        a = np.asarray(im)[::-1].copy()          # Unity textures start at the bottom row
        # avoid dark/white halos: bleed colour into fully transparent pixels
        from scipy import ndimage
        holes = a[..., 3] < 8
        if holes.any() and (~holes).any():
            idx = ndimage.distance_transform_edt(holes, return_distances=False, return_indices=True)
            a[..., :3] = a[idx[0], idx[1], :3]
        f.write(etcpak.compress_bc3(a.tobytes(), W, H))
print('packed', len(files), 'frames', W, 'x', H)

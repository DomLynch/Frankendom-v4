#!/usr/bin/env python3
"""magenta_gate.py (GOOSEBACKLOG2): Goose 07:10 gate rule. FAIL when any PNG under the given folders has pixels within CIE76 deltaE < --de of (255,0,255) OR have tone-mapped magenta-ish pixels (R,B>=100, G<35% of min(R,B), 0.5<B/R<2)
(HDRP missing-shader error colour). Threshold values are arguments, nothing hard-coded to a character.
  python magenta_gate.py <dir> [<dir>...] [--de 10] [--max-px 0] [--glob *.png]
exit 1 = at least one FAIL. Read-only."""
import argparse, sys
from pathlib import Path
import numpy as np
from PIL import Image


def lab(rgb):
    c = rgb / 255.0
    c = np.where(c > 0.04045, ((c + 0.055) / 1.055) ** 2.4, c / 12.92)
    m = np.array([[0.4124564, 0.3575761, 0.1804375], [0.2126729, 0.7151522, 0.0721750], [0.0193339, 0.1191920, 0.9503041]])
    xyz = c @ m.T / np.array([0.95047, 1.0, 1.08883])
    f = np.where(xyz > 0.008856, np.cbrt(xyz), 7.787 * xyz + 16 / 116)
    return np.stack([116 * f[..., 1] - 16, 500 * (f[..., 0] - f[..., 1]), 200 * (f[..., 1] - f[..., 2])], -1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('dirs', nargs='+')
    ap.add_argument('--de', type=float, default=10.0)
    ap.add_argument('--max-px', type=int, default=0, help='allowed magenta pixels per image')
    ap.add_argument('--near-max-px', type=int, default=100, help='allowed tone-mapped magenta-ish pixels per image (v100 purple build measured 270-500, mean RGB 206/24/146)')
    ap.add_argument('--glob', default='*.png')
    a = ap.parse_args()
    target = lab(np.array([255.0, 0.0, 255.0]))
    fails = 0
    n = 0
    for d in a.dirs:
        for p in sorted(Path(d).rglob(a.glob)):
            try:
                im = np.asarray(Image.open(p).convert('RGB'), dtype=np.float64)
            except Exception as e:
                print('SKIP', p, e)
                continue
            n += 1
            px = int((np.linalg.norm(lab(im) - target, axis=-1) < a.de).sum())
            r, g, b = im[..., 0], im[..., 1], im[..., 2]
            lo = np.minimum(r, b)
            near = int(((lo >= 100) & (g < 0.35 * lo) & (b / np.maximum(r, 1) > 0.5) & (b / np.maximum(r, 1) < 2.0)).sum())
            if px > a.max_px or near > a.near_max_px:
                fails += 1
                print('FAIL pure %6d px, magenta-ish %6d px  %s' % (px, near, p))
    print('magenta_gate: %d images, %d FAIL (deltaE<%g of 255,0,255, allowed %d px)' % (n, fails, a.de, a.max_px))
    return 1 if fails else 0


if __name__ == '__main__':
    sys.exit(main())

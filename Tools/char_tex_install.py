"""Regenerate the per-part character textures from the installed combat GLBs (LOOK helper, 2026-11-04).

Why: Assets/Characters/<Name>/Mats/*.mat (the materials the scene really uses on the fighters) point at the per-part
Tex/*.jpg files, NOT at the GLB textures. After a GLB texture fix those jpgs must be re-extracted.

What it does (data: Assets/Data/hdrp_char_mats.json, section "extract"):
  - copies the raw JPEG bytes of the base / normal / metalRough images of the named GLB material over the part jpgs
    (same file names, so the .meta files and GUIDs stay);
  - bakes an HDRP mask map PNG from the glTF ORM image:  R = metallic (ORM.B), G = AO (ORM.R), B = 0, A = smoothness (255 - ORM.G);
  - copies the sheen masks (single channel, linear) next to the textures.
Backups: pass --backup <dir>; every file it overwrites is copied there first.
Run:  python Tools/char_tex_install.py            (from the HDRP project root)
"""
import argparse, io, json, os, shutil, struct, sys
from PIL import Image
import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def load_glb(path):
    d = open(path, 'rb').read()
    n = struct.unpack('<I', d[12:16])[0]
    j = json.loads(d[20:20 + n])
    off = 20 + n
    bl = struct.unpack('<I', d[off:off + 4])[0]
    return j, d[off + 8:off + 8 + bl]


def image_bytes(j, b, tex_ref):
    if tex_ref is None:
        return None, None
    im = j['images'][j['textures'][tex_ref['index']]['source']]
    bv = j['bufferViews'][im['bufferView']]
    o = bv.get('byteOffset', 0)
    return b[o:o + bv['byteLength']], im.get('mimeType', '')


def backup(path, bdir):
    if bdir and os.path.exists(path):
        dst = os.path.join(bdir, os.path.relpath(path, ROOT).replace(os.sep, '__'))
        if not os.path.exists(dst):
            shutil.copy2(path, dst)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--data', default=os.path.join(ROOT, 'Assets', 'Data', 'hdrp_char_mats.json'))
    ap.add_argument('--backup', default='')
    a = ap.parse_args()
    cfg = json.load(open(a.data))
    if a.backup:
        os.makedirs(a.backup, exist_ok=True)
    for ch in cfg['extract']:
        j, b = load_glb(os.path.join(ROOT, ch['glb']))
        mats = {m.get('name'): m for m in j['materials']}
        tex_dir = os.path.join(ROOT, ch['texDir'])
        for g in ch['groups']:
            m = mats[g['glbMaterial']]
            pbr = m.get('pbrMetallicRoughness', {})
            srcs = {'base': pbr.get('baseColorTexture'), 'normal': m.get('normalTexture'), 'mr': pbr.get('metallicRoughnessTexture')}
            blobs = {}
            for k, ref in srcs.items():
                data, mime = image_bytes(j, b, ref)
                if data is None:
                    continue
                if mime != 'image/jpeg':
                    print('SKIP non-jpeg', g['glbMaterial'], k, mime)
                    continue
                blobs[k] = data
            for part in g['parts']:
                for k, data in blobs.items():
                    fn = part.get(k)
                    if not fn:
                        continue
                    dst = os.path.join(tex_dir, fn)
                    backup(dst, a.backup)
                    open(dst, 'wb').write(data)
                    print('wrote', os.path.relpath(dst, ROOT), len(data))
            mk = g.get('maskOut')
            if mk and 'mr' in blobs:
                orm = Image.open(io.BytesIO(blobs['mr'])).convert('RGB')
                size = g.get('maskSize', 0)
                if size and orm.size[0] != size:
                    orm = orm.resize((size, size), Image.LANCZOS)
                o = np.array(orm)
                out = np.zeros(o.shape[:2] + (4,), np.uint8)
                out[..., 0] = o[..., 2]          # metallic
                out[..., 1] = o[..., 0]          # AO
                out[..., 2] = 0
                out[..., 3] = 255 - o[..., 1]    # smoothness = 1 - roughness
                dst = os.path.join(tex_dir, mk)
                backup(dst, a.backup)
                Image.fromarray(out, 'RGBA').save(dst, optimize=False)
                print('mask', os.path.relpath(dst, ROOT), orm.size, 'metal mean %.3f smooth mean %.3f' % (out[..., 0].mean() / 255, out[..., 3].mean() / 255))
    for s in cfg.get('copy', []):
        src, dst = s['from'], os.path.join(ROOT, s['to'])
        backup(dst, a.backup)
        shutil.copyfile(src, dst)
        print('copied', os.path.relpath(dst, ROOT))


if __name__ == '__main__':
    main()

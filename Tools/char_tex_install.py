"""Regenerate the per-part character textures from a GLB (LOOK helper; V6CTEX rewrite 2026-11-05).

Mats/*.mat point at Assets/Characters/<Name>/Tex/*, NOT at GLB textures, so after a GLB texture change they must be re-extracted.
Data: Assets/Data/hdrp_char_mats.json  section "extract" (one row per character, "id" = roster id) and "copy" (rows with "id").

  python Tools/char_tex_install.py --character Joan --dry-run     list every file that would be written, write nothing
  python Tools/char_tex_install.py --character Joan [--backup DIR]

Rules:
  - --character <rosterId> is REQUIRED. Only that character's extract row and copy rows (same "id") are touched; every
    written path is checked to sit inside the row's texDir (a path outside it aborts before anything is written).
  - Format follows the destination file name in the data row (.jpg -> JPEG q95, .png -> PNG). If the GLB image already has
    that format its bytes are copied untouched, otherwise it is decoded and re-encoded (so PNG GLBs work).
  - GLB material names are matched robustly: exact, then without a numeric ".001" suffix, then without a "_<guid>" suffix,
    then a unique name-prefix. Failure lists the GLB material names (no KeyError).
  - A group may list "override": {"base"|"normal"|"mr": "<path>"} (project-relative or absolute, optional ${kitsOut}); an existing
    override file replaces that GLB image (used for a repainted head texture). Missing override files are ignored with a note.
  - A GLB path may start with ${kitsOut} (see --kits-out).
  - Mask PNG (if "maskOut"): R = metallic (ORM.B), G = AO (ORM.R), B = 0, A = smoothness (255 - ORM.G).
"""
import argparse, io, json, os, re, shutil, struct, sys
from PIL import Image
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))


def default_root():
    """Project root = the folder that contains _agents (so a lane copy still targets the real project), else parent of Tools."""
    p = HERE
    while True:
        par = os.path.dirname(p)
        if os.path.basename(p) == '_agents':
            return par
        if par == p:
            return os.path.dirname(HERE)
        p = par


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
    data = b[o:o + bv['byteLength']]
    mime = im.get('mimeType', '')
    if not mime:
        mime = 'image/png' if data[:4] == b'\x89PNG' else 'image/jpeg'
    return data, mime


def norm_name(n):
    n = re.sub(r'\.\d+$', '', n or '')
    n = re.sub(r'_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$', '', n)
    return n


def find_material(j, want):
    mats = j.get('materials', [])
    for m in mats:
        if m.get('name') == want:
            return m
    nw = norm_name(want)
    hits = [m for m in mats if norm_name(m.get('name')) == nw]
    if not hits:
        hits = [m for m in mats if (m.get('name') or '').startswith(nw)]
    if len(hits) == 1:
        return hits[0]
    names = [m.get('name') for m in mats]
    sys.exit('ERROR material "%s" %s in GLB; materials: %s' % (want, 'ambiguous' if hits else 'not found', names))


def fmt_of(fn):
    e = os.path.splitext(fn)[1].lower()
    return {'.jpg': 'JPEG', '.jpeg': 'JPEG', '.png': 'PNG'}.get(e)


def mime_fmt(mime):
    return {'image/jpeg': 'JPEG', 'image/png': 'PNG'}.get(mime)


def encode(data, mime, fn):
    want = fmt_of(fn)
    if want is None or want == mime_fmt(mime):
        return data, 'copy'
    im = Image.open(io.BytesIO(data))
    buf = io.BytesIO()
    if want == 'JPEG':
        im.convert('RGB').save(buf, 'JPEG', quality=95, subsampling=0)
    else:
        im.save(buf, 'PNG')
    return buf.getvalue(), '%s->%s' % (mime_fmt(mime), want)


def backup(path, bdir, root):
    if bdir and os.path.exists(path):
        dst = os.path.join(bdir, os.path.relpath(path, root).replace(os.sep, '__'))
        if not os.path.exists(dst):
            shutil.copy2(path, dst)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--character', required=True, help='roster id, e.g. Joan')
    ap.add_argument('--root', default=default_root())
    ap.add_argument('--data', default='')
    ap.add_argument('--kits-out', default=r'C:\Users\dom12\Documents\kits_out')
    ap.add_argument('--backup', default='')
    ap.add_argument('--dry-run', action='store_true')
    a = ap.parse_args()
    root = os.path.abspath(a.root)
    data_path = a.data or os.path.join(root, 'Assets', 'Data', 'hdrp_char_mats.json')
    cfg = json.load(open(data_path))
    sub = lambda s: s.replace('${kitsOut}', a.kits_out.replace('\\', '/'))
    resolve = lambda p: os.path.normpath(os.path.join(root, sub(p)))
    rows = [r for r in cfg['extract'] if r.get('id') == a.character]
    copies = [c for c in cfg.get('copy', []) if c.get('id') == a.character]
    if not rows:
        sys.exit('ERROR no extract row with id "%s" (ids: %s)' % (a.character, [r.get('id') for r in cfg['extract']]))
    skipped = [r.get('id') or r.get('texDir') for r in cfg['extract'] if r.get('id') != a.character]
    print('character', a.character, '| dry-run' if a.dry_run else '| WRITE', '| untouched rows:', skipped)
    plan = []   # (dst, bytes, note)
    for ch in rows:
        tex_dir = resolve(ch['texDir'])
        j, b = load_glb(resolve(ch['glb']))
        print('glb', resolve(ch['glb']))
        for g in ch['groups']:
            m = find_material(j, g['glbMaterial'])
            print('  group', g['glbMaterial'], '->', m.get('name'))
            pbr = m.get('pbrMetallicRoughness', {})
            srcs = {'base': pbr.get('baseColorTexture'), 'normal': m.get('normalTexture'), 'mr': pbr.get('metallicRoughnessTexture')}
            blobs = {}
            for k, ref in srcs.items():
                data, mime = image_bytes(j, b, ref)
                ov = (g.get('override') or {}).get(k)
                if ov:
                    op = resolve(ov)
                    if os.path.exists(op):
                        data = open(op, 'rb').read()
                        mime = 'image/png' if data[:4] == b'\x89PNG' else 'image/jpeg'
                        print('    OVERRIDE', k, op)
                    else:
                        print('    override missing, using GLB image:', k, op)
                if data is not None:
                    blobs[k] = (data, mime)
            for part in g['parts']:
                for k, (data, mime) in blobs.items():
                    fn = part.get(k)
                    if fn:
                        out, note = encode(data, mime, fn)
                        plan.append((os.path.join(tex_dir, fn), out, '%s %s' % (k, note)))
            mk = g.get('maskOut')
            if mk and 'mr' in blobs:
                orm = Image.open(io.BytesIO(blobs['mr'][0])).convert('RGB')
                size = g.get('maskSize', 0)
                if size and orm.size[0] != size:
                    orm = orm.resize((size, size), Image.LANCZOS)
                o = np.array(orm)
                out = np.zeros(o.shape[:2] + (4,), np.uint8)
                out[..., 0] = o[..., 2]
                out[..., 1] = o[..., 0]
                out[..., 3] = 255 - o[..., 1]
                buf = io.BytesIO()
                Image.fromarray(out, 'RGBA').save(buf, 'PNG', optimize=False)
                plan.append((os.path.join(tex_dir, mk), buf.getvalue(),
                             'mask %s metal %.3f smooth %.3f' % (orm.size, out[..., 0].mean() / 255, out[..., 3].mean() / 255)))
        for c in copies:
            src = resolve(c['from'])
            dst = resolve(c['to'])
            if os.path.exists(src):
                plan.append((dst, open(src, 'rb').read(), 'copy of ' + src))
            else:
                print('  copy source missing, skipped:', src)
        td = os.path.normcase(tex_dir) + os.sep
        for dst, _, _ in plan:
            if not os.path.normcase(dst).startswith(td):
                sys.exit('ERROR %s is outside the %s Tex folder %s: nothing written' % (dst, a.character, tex_dir))
    if a.backup and not a.dry_run:
        os.makedirs(a.backup, exist_ok=True)
    for dst, data, note in plan:
        rel = os.path.relpath(dst, root)
        if a.dry_run:
            print('WOULD WRITE %-60s %9d B  %s%s' % (rel, len(data), note, '' if os.path.exists(dst) else '  (new)'))
        else:
            backup(dst, a.backup, root)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            open(dst, 'wb').write(data)
            print('wrote', rel, len(data), note)
    print('%d file(s) %s, all inside the %s Tex folder' % (len(plan), 'listed' if a.dry_run else 'written', a.character))


if __name__ == '__main__':
    main()

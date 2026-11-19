#!/usr/bin/env python
r"""gfx_offenders.py: OFFENDER LIST for the Frankendom graphics doctrine (numbers, not eyeballing). READ-ONLY on the game.

Reads (never writes) Assets\StreamingAssets json rows, Assets\Env glb headers/accessors, groundmat PNGs, the FK1 camtrace path and
the four gfx_fix_* kits; writes only to _agents\laneLG\gfx_offenders\ (OFFENDERS.md, OFFENDERS.csv, cache.json).

  python Tools\gfx_offenders.py                       (defaults: this repo, FK1 run fk1_20261118_171013, radius 10 m)
  python Tools\gfx_offenders.py --trace <camtrace.csv> --radius 10 --out <dir> --nocache

MEASURED = read from a file header / accessor / pixel / row (exact for that file).
ESTIMATE = derived with a stated model (no engine access): LOD choice, screen px per metre, display brightness, kit coverage by key match.
Every CSV column is tagged in OFFENDERS.md section 0.
"""
import argparse, csv, glob, io, json, math, os, re, struct, sys, time
import numpy as np
from PIL import Image
Image.MAX_IMAGE_PIXELS = None

HERE = os.path.dirname(os.path.abspath(__file__))
ap = argparse.ArgumentParser()
ap.add_argument('--repo', default=os.path.dirname(HERE))
ap.add_argument('--trace', default=r'D:\FrankendomQA\fk1_20261118_171013\run\trace\camtrace.csv')
ap.add_argument('--radius', type=float, default=10.0)
ap.add_argument('--out', default=None)
ap.add_argument('--nocache', action='store_true')
A = ap.parse_args()
REPO = A.repo
SA = os.path.join(REPO, 'Assets', 'StreamingAssets')
ENV = os.path.join(REPO, 'Assets', 'Env')
LG = os.path.join(REPO, '_agents', 'laneLG')
OUT = A.out or os.path.join(LG, 'gfx_offenders')
os.makedirs(OUT, exist_ok=True)
CACHE_F = os.path.join(OUT, 'cache.json')
CACHE = {} if A.nocache or not os.path.exists(CACHE_F) else json.load(open(CACHE_F))
FOV_V = 50.0           # camtrace fov column (vertical, deg)
SCREEN_H = 1080.0      # FK1 run frame height; the ship baseline is 1080p (upscaling-on-by-default note)
PPM_K = SCREEN_H / (2 * math.tan(math.radians(FOV_V / 2)))   # screen px per metre at 1 m distance = 1158


def J(path):
    t = open(path, encoding='utf-8-sig').read().lstrip('\ufeff')
    return json.loads(t)


def sa(name):
    return J(os.path.join(SA, name))


# ------------------------------------------------------------------ path (camtrace)
rows = list(csv.DictReader(open(A.trace)))
PL = np.array([[float(r['px']), float(r['pz'])] for r in rows])
CM = np.array([[float(r['cx']), float(r['cy']), float(r['cz'])] for r in rows])
PZ = PL[:, 1]
# subsample the path to <=3000 pts for speed (dense enough: steps are cm apart)
sel = np.unique(np.linspace(0, len(rows) - 1, min(len(rows), 3000)).astype(int))
PLs, CMs = PL[sel], CM[sel]
CAM_Y_MIN = float(CM[:, 1].min()); CAM_Y_MED = float(np.median(CM[:, 1]))

LZ = sa('look_zones.json')
ZONES = [(r['id'], r['zFrom'], r['zTo']) for r in LZ['rows']]
ZNUM = {'pit_yard': 'z1', 'sobek_reach': 'z2', 'hub_camp': 'z3', 'grendel': 'z4', 'z5_humbaba': 'z5', 'z6_erlking': 'z6', 'z7_market2': 'z7'}


def zone_of(z):
    for zid, a, b in ZONES:
        if a <= z < b: return zid
    return ZONES[-1][0]


def path_dist(x, z):
    d = np.hypot(PLs[:, 0] - x, PLs[:, 1] - z)
    return float(d.min())


def cam_dist(x, y, z):
    d = np.sqrt((CMs[:, 0] - x) ** 2 + (CMs[:, 1] - y) ** 2 + (CMs[:, 2] - z) ** 2)
    i = int(d.argmin())
    return float(d[i]), i


# ------------------------------------------------------------------ small image helpers
def srgb2lin(v):
    v = np.asarray(v, dtype=np.float64)
    return np.where(v <= 0.04045, v / 12.92, ((v + 0.055) / 1.055) ** 2.4)


def img_stats(im):
    """median linear albedo (rgb), luma p99, bright fraction, seam and streak metrics on a PIL image (MEASURED)."""
    im = im.convert('RGB')
    w, h = im.size
    big = im.resize((min(w, 512), min(h, 512)), Image.BOX) if max(w, h) > 512 else im
    a = np.asarray(big, dtype=np.float64) / 255.0
    lin = srgb2lin(a)
    luma = lin.mean(axis=2)
    med = np.median(lin.reshape(-1, 3), axis=0)
    # tile seam: edge mismatch (opposite edges) vs ordinary neighbour difference
    n = a.shape[0]
    interior = (np.abs(np.diff(a, axis=0)).mean() + np.abs(np.diff(a, axis=1)).mean()) / 2 + 1e-6
    seam = (np.abs(a[0] - a[-1]).mean() + np.abs(a[:, 0] - a[:, -1]).mean()) / 2
    # streaks: variance of row means vs column means (anisotropy) on luma, and 32px block variance (macro blotch)
    rm, cm = luma.mean(axis=1), luma.mean(axis=0)
    aniso = float(max(rm.std(), cm.std()) / (luma.std() + 1e-6))
    k = max(1, n // 16)
    blocks = luma[:n // k * k, :n // k * k].reshape(n // k, k, n // k, k).mean(axis=(1, 3))
    macro = float(blocks.std() / (luma.mean() + 1e-6))
    return {'medLin': [round(float(x), 4) for x in med], 'lumaMed': round(float(np.median(luma)), 4),
            'lumaP99': round(float(np.percentile(luma, 99)), 4), 'brightFrac': round(float((luma > 0.5).mean()), 4),
            'seamRatio': round(float(seam / interior), 2), 'aniso': round(aniso, 2), 'macroCV': round(macro, 2)}


def png_dims(path):
    with open(path, 'rb') as f:
        b = f.read(32)
    if b[:8] == b'\x89PNG\r\n\x1a\n':
        return struct.unpack('>II', b[16:24])
    im = Image.open(path); return im.size


# ------------------------------------------------------------------ glb reader (JSON chunk + accessors)
CT = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
NC = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}


def glb_open(path):
    f = open(path, 'rb')
    magic, ver, tot = struct.unpack('<4sII', f.read(12))
    cl, ct = struct.unpack('<II', f.read(8))
    js = json.loads(f.read(cl))
    binoff = 20 + cl + 8
    return f, js, binoff


def acc_read(f, js, binoff, idx):
    a = js['accessors'][idx]; bv = js['bufferViews'][a['bufferView']]
    dt = np.dtype(CT[a['componentType']]); nc = NC[a['type']]
    off = binoff + bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    stride = bv.get('byteStride') or dt.itemsize * nc
    f.seek(off)
    if stride == dt.itemsize * nc:
        arr = np.frombuffer(f.read(a['count'] * stride), dtype=dt).reshape(a['count'], nc)
    else:
        raw = np.frombuffer(f.read(a['count'] * stride), dtype=np.uint8).reshape(a['count'], stride)
        arr = raw[:, :dt.itemsize * nc].copy().view(dt).reshape(a['count'], nc)
    return arr


def image_bytes(f, js, binoff, ii, maxb=None):
    im = js['images'][ii]; bv = js['bufferViews'][im['bufferView']]
    f.seek(binoff + bv.get('byteOffset', 0))
    return f.read(bv['byteLength'] if maxb is None else min(maxb, bv['byteLength'])), bv['byteLength']


def tex_dims(f, js, binoff, ii):
    b, n = image_bytes(f, js, binoff, ii, 400000)
    try:
        im = Image.open(io.BytesIO(b)); return im.size, n
    except Exception:
        return None, n


def glb_light(path):
    """JSON only: tri counts per mesh and material names (cheap)."""
    key = path + '|L'
    st = os.stat(path)
    c = CACHE.get(key)
    if c and c['m'] == st.st_mtime: return c
    f, js, binoff = glb_open(path)
    tris = 0
    for m in js.get('meshes', []):
        for p in m['primitives']:
            if 'indices' in p and p.get('mode', 4) == 4: tris += js['accessors'][p['indices']]['count'] // 3
            elif 'POSITION' in p['attributes']: tris += js['accessors'][p['attributes']['POSITION']]['count'] // 3
    f.close()
    c = {'m': st.st_mtime, 'tris': tris}
    CACHE[key] = c
    return c


def glb_full(path, want_median=True):
    """tris, bbox, materials (name, base-colour tex dims, normal present, mr present, factors), area-weighted texels per metre. MEASURED."""
    key = path + '|F'
    st = os.stat(path)
    c = CACHE.get(key)
    if c and c['m'] == st.st_mtime: return c
    f, js, binoff = glb_open(path)
    mats = js.get('materials', [])
    texdim = {}
    def td(ti):
        if ti is None: return None
        if ti in texdim: return texdim[ti]
        t = js['textures'][ti]
        src = t.get('source')
        if src is None:
            for ext in (t.get('extensions') or {}).values(): src = ext.get('source')
        d, n = tex_dims(f, js, binoff, src) if src is not None else (None, 0)
        texdim[ti] = (d, src, n)
        return texdim[ti]
    mrec = []
    for mi, m in enumerate(mats):
        pbr = m.get('pbrMetallicRoughness', {})
        bt = pbr.get('baseColorTexture', {}).get('index')
        nt = m.get('normalTexture', {}).get('index')
        mt = pbr.get('metallicRoughnessTexture', {}).get('index')
        d = td(bt)
        rec = {'name': m.get('name', 'mat%d' % mi), 'baseDim': list(d[0]) if d and d[0] else None, 'normal': nt is not None,
               'mr': mt is not None, 'rough': pbr.get('roughnessFactor', 1.0), 'metal': pbr.get('metallicFactor', 1.0),
               'baseFactor': pbr.get('baseColorFactor', [1, 1, 1, 1]), 'alphaMode': m.get('alphaMode', 'OPAQUE'),
               'emissive': any(x > 0 for x in m.get('emissiveFactor', [0, 0, 0])) or 'emissiveTexture' in m,
               'tris': 0, 'wArea': 0.0, 'uvArea': 0.0}
        if want_median and d and d[1] is not None and d[0] and max(d[0]) >= 16:
            try:
                b, n = image_bytes(f, js, binoff, d[1])
                im = Image.open(io.BytesIO(b)); im.draft('RGB', (256, 256))
                rec['albedo'] = img_stats(im)
            except Exception as e:
                rec['albedo'] = None
        mrec.append(rec)
    lo = np.array([1e9] * 3); hi = np.array([-1e9] * 3)
    tris = 0
    rng = np.random.default_rng(1)
    for mesh in js.get('meshes', []):
        for p in mesh['primitives']:
            at = p['attributes']
            if 'POSITION' not in at: continue
            pa = js['accessors'][at['POSITION']]
            if 'min' in pa: lo = np.minimum(lo, pa['min']); hi = np.maximum(hi, pa['max'])
            if 'indices' in p: nt = js['accessors'][p['indices']]['count'] // 3
            else: nt = pa['count'] // 3
            tris += nt
            mi = p.get('material')
            if mi is None or mi >= len(mrec): continue
            mrec[mi]['tris'] += nt
            if 'TEXCOORD_0' not in at or 'indices' not in p: continue
            try:
                pos = acc_read(f, js, binoff, at['POSITION']).astype(np.float64)
                uva = js['accessors'][at['TEXCOORD_0']]
                uv = acc_read(f, js, binoff, at['TEXCOORD_0']).astype(np.float64)
                if uva.get('normalized'): uv = uv / np.iinfo(CT[uva['componentType']]).max
                idx = acc_read(f, js, binoff, p['indices']).reshape(-1, 3).astype(np.int64)
                if len(idx) > 200000: idx = idx[rng.choice(len(idx), 200000, replace=False)]
                P = pos[idx]; U = uv[idx]
                wa = 0.5 * np.linalg.norm(np.cross(P[:, 1] - P[:, 0], P[:, 2] - P[:, 0]), axis=1)
                ua = 0.5 * np.abs((U[:, 1, 0] - U[:, 0, 0]) * (U[:, 2, 1] - U[:, 0, 1]) - (U[:, 2, 0] - U[:, 0, 0]) * (U[:, 1, 1] - U[:, 0, 1]))
                ok = wa > 1e-9
                mrec[mi]['wArea'] += float(wa[ok].sum()); mrec[mi]['uvArea'] += float(ua[ok].sum())
            except Exception:
                pass
    f.close()
    for r in mrec:
        if r['wArea'] > 0 and r['uvArea'] > 0 and r['baseDim']:
            r['tpm'] = round(math.sqrt(r['uvArea'] / r['wArea']) * max(r['baseDim']), 1)   # texels per metre at node scale 1
        else:
            r['tpm'] = None
    c = {'m': st.st_mtime, 'tris': tris, 'bbox': [list(map(float, lo)), list(map(float, hi))] if lo[0] < 1e8 else None, 'mats': mrec}
    CACHE[key] = c
    return c


# ------------------------------------------------------------------ matAdjust (hdrp_fixes + matadjust_runtime, file order, later wins)
HF = sa('hdrp_fixes.json')
MAR = sa('matadjust_runtime.json')
KIT_MAT = []     # filled below from the materials kit (rows tagged _gfx1)
BASE_ROWS = [dict(r, _src='hdrp_fixes.json matAdjust') for r in HF.get('matAdjust', [])] + [dict(r, _src='matadjust_runtime.json') for r in MAR['rows']]


def mat_effect(matname, rows_):
    eff = {'color': None, 'smoothMax': None, 'smoothMin': None, 'normalScale': None, 'metalMax': None, 'emissive': None, 'srcs': []}
    for r in rows_:
        if r['match'].lower() in matname.lower():
            if r.get('color'): eff['color'] = r['color']
            if r.get('smoothnessMax', -1) >= 0: eff['smoothMax'] = r['smoothnessMax']
            if r.get('smoothnessMin', -1) >= 0: eff['smoothMin'] = r['smoothnessMin']
            if r.get('normalScale', -1) >= 0: eff['normalScale'] = r['normalScale']
            if r.get('metallicMax', -1) >= 0: eff['metalMax'] = r['metallicMax']
            if r.get('emissiveScale', -1) >= 0: eff['emissive'] = r['emissiveScale']
            eff['srcs'].append(r['match'] + ('[kit]' if r.get('_gfx1') else ''))
    return eff


# ------------------------------------------------------------------ the four fix kits: what they change (diff of kit twins vs live)
def jdiff(a, b, path, out):
    """collect (path,key) that differ between live a and kit b."""
    if isinstance(a, dict) and isinstance(b, dict):
        for k in set(a) | set(b):
            if k.startswith('_gfxfix'): continue
            if k not in a: out.add(path + '/' + k + ' (added)')
            elif k not in b: out.add(path + '/' + k + ' (removed)')
            else: jdiff(a[k], b[k], path + '/' + k, out)
    elif isinstance(a, list) and isinstance(b, list):
        def ident(x, i):
            if isinstance(x, dict):
                for k in ('id', 'name', 'match'):
                    if k in x: return str(x[k])
            return '#%d' % i
        da = {ident(x, i): x for i, x in enumerate(a)}; db = {ident(x, i): x for i, x in enumerate(b)}
        added = [k for k in db if k not in da]
        if added: out.add(path + '[+%d rows: %s]' % (len(added), ','.join(added[:6]) + ('...' if len(added) > 6 else '')))
        for k in da:
            if k in db: jdiff(da[k], db[k], path + '[' + k + ']', out)
            else: out.add(path + '[' + k + '] (removed)')
    elif a != b:
        out.add(path)


KITS = {}
for kit in ('ground', 'atmosphere', 'materials', 'props'):
    d = os.path.join(LG, 'gfx_fix_' + kit, 'Assets', 'StreamingAssets')
    ch = set()
    if os.path.isdir(d):
        for f in os.listdir(d):
            if f.endswith('.json') and os.path.exists(os.path.join(SA, f)):
                try: jdiff(J(os.path.join(SA, f)), J(os.path.join(d, f)), f, ch)
                except Exception as e: ch.add(f + ' (diff failed %s)' % e)
        for g in glob.glob(os.path.join(d, 'PropsRuntime', '*.glb')): ch.add('PropsRuntime/' + os.path.basename(g) + ' (new glb)')
    KITS[kit] = ch
# materials kit rows live in the kit's matadjust_runtime.json tagged _gfx1
try:
    kmr = J(os.path.join(LG, 'gfx_fix_materials', 'Assets', 'StreamingAssets', 'matadjust_runtime.json'))
    KIT_MAT = [dict(r, _src='gfx_fix_materials') for r in kmr['rows'] if r.get('_gfx1')]
except Exception:
    KIT_MAT = []
ALL_MAT_ROWS = BASE_ROWS + KIT_MAT


def kit_touch(kit, needle):
    return [c for c in KITS.get(kit, []) if needle in c]


# ------------------------------------------------------------------ SURFACE TABLE
OFF = []          # offender rows


def new(zone, kind, ident, **kw):
    r = {'zone': ZNUM.get(zone, zone), 'zoneId': zone, 'kind': kind, 'id': ident, 'distPathM': None, 'camDistM': None, 'tpm': None,
         'pxPerTexel': None, 'tpmSrc': '', 'smoothMin': None, 'smoothMax': None, 'tint': '', 'normal': '', 'tris': None, 'lod': '',
         'albedoLin': None, 'flags': [], 'score': 0.0, 'fix': '', 'fixFile': '', 'fixKey': '', 'covered': '', 'light': '', 'notes': '', 'areaW': 1.0, 'fixclass': ''}
    r.update(kw); OFF.append(r); return r


# ---- A. ground rows (zone_ground_mat.json)
ZG = sa('zone_ground_mat.json')
ground_png = {}
def gstat(fn):
    if fn in ground_png: return ground_png[fn]
    p = os.path.join(SA, 'groundmat', fn)
    if not os.path.exists(p): ground_png[fn] = None; return None
    key = 'g|' + fn
    st = os.stat(p)
    c = CACHE.get(key)
    if not c or c['m'] != st.st_mtime:
        im = Image.open(p); dims = im.size
        s = img_stats(im); s['dims'] = list(dims); s['m'] = st.st_mtime
        CACHE[key] = c = s
    ground_png[fn] = c; return c


def zone_of_row(r):
    if 'zMin' in r: return zone_of((r['zMin'] + r['zMax']) / 2)
    m = {'Arena': 'pit_yard', 'Zone2': 'sobek_reach', 'HubZone': 'hub_camp', 'Zone4': 'grendel', 'Zone5': 'z5_humbaba', 'Zone6': 'z6_erlking', 'Zone7': 'z7_market2'}
    return m.get(r['zoneRoot'], 'pit_yard')


# zone look numbers used for the light estimate
LROW = {r['id']: r for r in LZ['rows']}
LD = LZ['defaults']
KEY_LUX = 7000.0 * LZ.get('keyGroundMul', 0.95)
EV = sa('look.json')['exposure']['ev']


def aces(x):
    x = np.maximum(x, 0); return np.clip((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0, 1)


def disp8(albedo_lin, E_lux, pe=0.0):
    """ESTIMATE: scene-referred display value (0-255) of a Lambert surface: L = a*E/pi, HDRP fixed EV100, ACES, sRGB."""
    Lm = albedo_lin * E_lux / math.pi
    v = Lm * (2 ** pe) / (1.2 * (2 ** EV))
    t = float(aces(v))
    s = 12.92 * t if t <= 0.0031308 else 1.055 * t ** (1 / 2.4) - 0.055
    return round(255 * s, 0)


# ---- MEASURED per-zone frame luma from the FK1 run frames (the real render): crushed-black share, median, p95 (ground band = lower 45 % of the frame)
FRAME_ZONE = {}
try:
    fdir = os.path.join(os.path.dirname(A.trace), 'frames')
    fmap = [(float(x['t']), x['file']) for x in csv.DictReader(open(os.path.join(os.path.dirname(A.trace), 'frames.csv')))]
    rt = np.array([float(r['t']) for r in rows])
    byz = {}
    for t_, fn in fmap:
        k_ = int(np.argmin(np.abs(rt - t_))); byz.setdefault(rows[k_]['zone'], []).append(fn)
    for zid_, fl_ in byz.items():
        pick = [fl_[i] for i in np.unique(np.linspace(0, len(fl_) - 1, min(len(fl_), 14)).astype(int))]
        meds, p5s, p95s, cr16, cr32, blow, gmed = [], [], [], [], [], [], []
        used = 0
        for fn in pick:
            fp_ = os.path.join(os.path.dirname(A.trace), fn)
            if not os.path.exists(fp_): continue
            im_ = Image.open(fp_).convert('L'); im_ = im_.resize((480, 270), Image.BOX)
            a_ = np.asarray(im_, dtype=np.float64)
            if a_.mean() < 6: continue                      # black harness frames
            used += 1
            meds.append(np.median(a_)); p5s.append(np.percentile(a_, 5)); p95s.append(np.percentile(a_, 95))
            cr16.append((a_ < 16).mean()); cr32.append((a_ < 32).mean()); blow.append((a_ > 235).mean())
            gmed.append(np.median(a_[int(270 * 0.55):, :]))
        if used:
            FRAME_ZONE[zid_] = {'n': used, 'med': round(float(np.mean(meds)), 1), 'p5': round(float(np.mean(p5s)), 1), 'p95': round(float(np.mean(p95s)), 1),
                                'crush16': round(float(np.mean(cr16)) * 100, 1), 'crush32': round(float(np.mean(cr32)) * 100, 1), 'blown': round(float(np.mean(blow)) * 100, 2), 'groundMed': round(float(np.mean(gmed)), 1)}
except Exception as e:
    FRAME_ZONE = {'error': str(e)}


def _inv_E(alb, target8, pe):
    lo, hi = 1.0, 2e6
    for _ in range(60):
        mid = (lo + hi) / 2
        if disp8(alb, mid, pe) < target8: lo = mid
        else: hi = mid
    return (lo + hi) / 2


CAL = {}


def calib(zone, alb_zone):
    """scale k so the model's sun+bounce lit ground matches the MEASURED ground-band median of this zone's frames (adds the missing sky ambient / local lights)."""
    if zone in CAL: return CAL[zone]
    fz = FRAME_ZONE.get(zone) if isinstance(FRAME_ZONE, dict) else None
    lr = LROW.get(zone, LD)
    elev = lr.get('keyElevationDeg', -1); elev = 30 if elev < 0 else elev
    E0 = KEY_LUX * math.sin(math.radians(max(elev, LZ.get('keyMinElevationDeg', 12)))) * float(np.mean(lr.get('keyColor', [1, 1, 1]))) + lr.get('bounceLux', 600) * float(np.mean(lr.get('bounceColor', [0.6] * 3)))
    k = 1.0
    if fz and alb_zone:
        Ee = _inv_E(alb_zone, fz['groundMed'], lr.get('postExposure', 0))
        k = max(0.05, min(40.0, Ee / E0))
    CAL[zone] = k
    return k


def light_est(zone, albedo_lin):
    lr = LROW.get(zone, LD)
    elev = lr.get('keyElevationDeg', -1)
    if elev < 0: elev = 30
    keyc = np.mean(lr.get('keyColor', [1, 1, 1]))
    E_sun = KEY_LUX * math.sin(math.radians(max(elev, LZ.get('keyMinElevationDeg', 12)))) * keyc
    E_sh = lr.get('bounceLux', 600) * np.mean(lr.get('bounceColor', [0.6, 0.6, 0.6])) * 1.0
    pe = lr.get('postExposure', 0)
    k = CAL.get(zone, 1.0)
    return disp8(albedo_lin, k * (E_sun + E_sh), pe), disp8(albedo_lin, k * E_sh, pe)


GROUND_ROWS = []
for r in ZG['rows']:
    if not r.get('enabled') or r.get('hide') or not r.get('baseColorMap') and r['id'].startswith(('z1_hide',)): continue
    if not r.get('enabled'): continue
    if r.get('hide'): continue
    zone = zone_of_row(r)
    mix = r.get('mix', 1.0)
    tm = r.get('tilingMul', 1.0)
    tm_eff = tm ** mix if tm > 0 else 1.0
    tint = r.get('tint', [1, 1, 1])
    tint_eff = [t ** mix for t in tint] if tint else [1, 1, 1]
    tex = r.get('baseColorMap')
    gs = gstat(tex) if tex else None
    nmap = r.get('normalMap')
    ns = gstat(nmap) if nmap else None
    # terrain UV 1 = 64 m (look.json _notes); base texture period = 64/tilingMul metres
    if gs:
        dim = max(gs['dims']); period = 64.0 / tm_eff; tpm = dim / period
        alb = [gs['medLin'][i] * tint_eff[i] for i in range(3)]
        alb_l = float(np.mean(alb))
    else:
        dim = None; period = None; tpm = None; alb = None; alb_l = None
    GROUND_ROWS.append((r, zone, tpm, period, gs, alb_l, tm_eff, tint_eff, bool(nmap)))

for (r_, zone_, tpm_, per_, gs_, alb_, tm_, ti_, hn_) in GROUND_ROWS:
    if r_['id'].startswith('t_') and alb_: calib(zone_, alb_)
# per-zone closest visible ground: slant distance to the bottom frame edge (cy / sin(pitch + fov/2)), 10th percentile over the zone steps.
zone_cam = {}
zone_steps = {}
for i, rw in enumerate(rows):
    zid = zone_of(float(rw['pz']))
    zone_steps.setdefault(zid, []).append(i)
for zid, ix in zone_steps.items():
    cy = CM[ix, 1]
    pitch = np.array([float(rows[i]['pitch']) for i in ix])
    slant = cy / np.sin(np.radians(np.clip(pitch + FOV_V / 2, 5, 80)))      # nearest ground visible at the bottom frame edge (camera pitch down + half fov)
    zone_cam[zid] = (float(np.percentile(slant, 10)), float(np.median(slant)))
SMOOTH_NOTE = 'smoothness = row remap (ground clone), measured from row'
for (r, zone, tpm, period, gs, alb_l, tm_eff, tint_eff, hasn) in GROUND_ROWS:
    if r['id'].startswith(('z3_apothecary', 'z7_apothecary')):
        continue
    cmin, cmed = zone_cam.get(zone, (CAM_Y_MIN, CAM_Y_MED))
    dcl = max(cmin, 1.0)
    ppm = PPM_K / dcl
    rec = new(zone, 'ground', r['id'], distPathM=0.0, camDistM=round(dcl, 2), tpm=round(tpm, 1) if tpm else None,
              pxPerTexel=round(ppm / tpm, 2) if tpm else None, tpmSrc='MEASURED png dims %s x tilingMul^mix %.2f over 64 m terrain UV; period %.1f m' % (gs['dims'] if gs else '?', tm_eff, period or 0),
              smoothMin=r.get('smoothMin'), smoothMax=r.get('smoothMax'), tint='x'.join('%.2f' % t for t in tint_eff) + (' mix %.2f' % r.get('mix', 1)),
              normal=('map %s ns %s' % (r.get('normalMap'), r.get('normalScale'))) if hasn else 'engine default only',
              albedoLin=round(alb_l, 4) if alb_l else None, areaW=3.0, notes='tex %s; detailAlbedo %s detailTilingMul %s; %s' % (r.get('baseColorMap'), r.get('detailAlbedo'), r.get('detailTilingMul'), r.get('_look', '')[:90]))
    rec['_row'] = r; rec['_gs'] = gs
    fl = rec['flags']
    if tpm and tpm < 256: fl.append('low-res')
    if rec['pxPerTexel'] and rec['pxPerTexel'] > 4: fl.append('magnified>4x')
    if (r.get('smoothMin') or 0) > 0.7: fl.append('mirror/sparkle')
    if not hasn: fl.append('flat(no normal)')
    if (r.get('normalScale', 1) in (-1, None)) is False and r.get('normalScale', 1) < 0.7: fl.append('weak-normal')
    if gs and gs.get('seamRatio', 0) > 3.0: fl.append('seam %.1fx' % gs['seamRatio'])
    if gs and gs.get('aniso', 0) > 0.9: fl.append('streaky-albedo')
    # light check on this ground
    if alb_l:
        lit, sh = light_est(zone, alb_l)
        rec['light'] = 'lit~%d sh~%d /255 (ESTIMATE)' % (lit, sh)
        if lit > 215: fl.append('overbright')
        if lit < 16: fl.append('near-black(lit~%d)' % lit)

# ---- A2. the z4 far terrain chunks (README visual_w6_kit): the silt clone on the coarse b2 chunks. ROW 1.
z4r = next((x for x in GROUND_ROWS if x[0]['id'] == 't_z4_grendel_bone_silt'), None)
if z4r:
    r, zone, tpm, period, gs, alb_l, tm_eff, tint_eff, hasn = z4r
    # 1815 verts over 334 m -> about 12 m triangles (visual_w6_kit README, measured by the Lead from the GLB); b2 is the far ring
    # white-streak band measured in the f_000400 frame (below)
    rec = new('grendel', 'ground-far', 'terrain_s*_b2 + silt clone (t_z4_grendel_bone_silt)', distPathM=20.0, camDistM=20.0,
              tpm=round(tpm, 1), pxPerTexel=round(PPM_K / 20.0 / tpm, 2), tpmSrc='MEASURED png 2048 x tilingMul 3.0 / 64 m = 96 tpm; period %.1f m (the ~21 m tile-seam grid in the README)' % period,
              smoothMin=r['smoothMin'], smoothMax=r['smoothMax'], tint='x'.join('%.2f' % t for t in tint_eff), normal='map %s ns %s' % (r['normalMap'], r['normalScale']),
              albedoLin=round(alb_l, 4), areaW=3.5, notes='far chunks b2: 1815 verts over 334 m (~12 m triangles), UV density 0.0156 uv/m in b0/b1/b2 (README: not UV stretch). The lit band sits at a depth where the near ring ends and b2 starts.')
    rec['_row'] = r; rec['_gs'] = gs; rec['pinned'] = 1
    rec['flags'] += ['WHITE-STREAKS (seen f_000400)', 'low-res']
    if gs and gs.get('seamRatio', 0) > 3.0: rec['flags'].append('seam %.1fx' % gs['seamRatio'])
    lit, sh = light_est('grendel', alb_l); rec['light'] = 'lit~%d sh~%d /255 (ESTIMATE)' % (lit, sh)

# ---- measure the f_000400 frame: white streak share in the lower ground band (MEASURED on the jpg)
FRAME_META = {}
try:
    fp = os.path.join(os.path.dirname(A.trace), 'frames', 'f_000400.jpg')
    im = Image.open(fp).convert('RGB'); w, h = im.size
    a = np.asarray(im, dtype=np.float64) / 255.0
    lum = (0.2126 * srgb2lin(a[..., 0]) + 0.7152 * srgb2lin(a[..., 1]) + 0.0722 * srgb2lin(a[..., 2]))
    band = lum[int(h * 0.55):int(h * 0.97), :]
    # streaks = pixels that are very bright (>=0.6 linear) in the ground band, counted per row stripe
    bright = (band > 0.45)
    FRAME_META = {'w': w, 'h': h, 'bandMeanLin': round(float(band.mean()), 4), 'bandP99': round(float(np.percentile(band, 99)), 4),
                  'brightFrac': round(float(bright.mean()), 4), 'bandRows': [int(h * 0.55), int(h * 0.97)]}
    # which trace row / zone this frame belongs to
    fr = [x for x in csv.DictReader(open(os.path.join(os.path.dirname(A.trace), 'frames.csv'))) if x['idx'] == '400']
    if fr:
        t = float(fr[0]['t']); k = int(np.argmin([abs(float(x['t']) - t) for x in rows]))
        FRAME_META.update({'t': t, 'zone': rows[k]['zone'], 'px': rows[k]['px'], 'pz': rows[k]['pz'], 'cx': rows[k]['cx'], 'cy': rows[k]['cy'], 'cz': rows[k]['cz']})
except Exception as e:
    FRAME_META = {'error': str(e)}

# ---- B. props from every layout (+ overlay adds/moves), within radius of the walked path
LAYOUTS = ['props_layout.json', 'props_layout_hub.json', 'props_layout_zone2.json', 'props_layout_grendel.json', 'props_layout_zone5.json', 'props_layout_zone6.json', 'props_layout_zone7.json']
PROPS = {}
LIBS = {}
LODP = {}
seen_sig = set()
for fn in LAYOUTS:
    d = sa(fn)
    for l in d['libraries']: LIBS[l['name']] = l['pattern']
    for l in d['lodProfiles']: LODP.setdefault(l['name'], l['screenHeights'])
    for p in d['props']:
        sig = (p['asset'], round(p['pos'][0], 2), round(p['pos'][2], 2))
        if sig in seen_sig: continue
        seen_sig.add(sig)
        q = dict(p); q['_layout'] = fn; PROPS[p['id']] = q
ov = sa('props_overlay.json')
for r in ov['rows']:
    if not r.get('enabled', True): continue
    if r['op'] == 'move' and r['id'] in PROPS:
        PROPS[r['id']].update({k: r[k] for k in ('pos', 'scale') if k in r}); PROPS[r['id']]['_moved'] = True
    elif r['op'] == 'add' and r.get('src') in PROPS:
        q = dict(PROPS[r['src']]); q.update({k: r[k] for k in ('pos', 'scale', 'rotEuler') if k in r}); q['id'] = r['id']; q['_layout'] = 'props_overlay add'
        PROPS[r['id']] = q


def glb_for(p, lod):
    pat = LIBS.get(p['library'])
    if not pat: return None
    path = os.path.join(REPO, pat.replace('{asset}', p['asset']).replace('{lod}', str(lod)).replace('/', os.sep))
    return path if os.path.exists(path) else None


def pick_lod(p, height_m, d):
    sh = LODP.get(p['lodProfile'], [0.25, 0.08, 0.02])
    frac = height_m / (2 * max(d, 0.5) * math.tan(math.radians(FOV_V / 2)))
    for i, h in enumerate(sh):
        if frac >= h: return i, frac
    return len(sh), frac     # culled beyond last


NEAR = []
for pid, p in PROPS.items():
    x, y, z = p['pos'][0], p['pos'][1], p['pos'][2]
    if p.get('scale', 1) <= 0.011: continue            # overlay-hidden (scale 0.01)
    dp = path_dist(x, z)
    if dp > A.radius + 6: continue                    # prefilter by centre; footprint subtract below
    NEAR.append((pid, p, dp))

for pid, p, dp in NEAR:
    x, y, z = p['pos'][0], p['pos'][1], p['pos'][2]
    g0 = glb_for(p, 0)
    sc = p.get('scale', 1.0)
    info = None
    if g0:
        try: info = glb_full(g0)
        except Exception as e: info = None
    bb = info['bbox'] if info and info['bbox'] else None
    if bb:
        ext = [(bb[1][i] - bb[0][i]) * sc for i in range(3)]; hgt = ext[1] * (p.get('scaleYMul', 1) or 1); rad = 0.5 * math.sqrt(ext[0] ** 2 + ext[2] ** 2)
    else:
        ext = [1, 1, 1]; hgt = 1.0; rad = 0.5
    dsurf = max(dp - rad, 0.0)
    if dsurf > A.radius: continue
    cy = y + hgt / 2
    dcam, ci = cam_dist(x, cy, z)
    dclose = max(dcam - 0.5 * math.sqrt(ext[0] ** 2 + ext[1] ** 2 + ext[2] ** 2), 1.0)
    lod, frac = pick_lod(p, hgt, dclose)
    lod_i = min(lod, 2)
    gl = glb_for(p, lod_i) if p['library'] not in ('blocks',) else g0
    tris = None
    try:
        if gl: tris = glb_light(gl)['tris']
    except Exception: pass
    zone = zone_of(z)
    rec = new(zone, 'prop', pid, distPathM=round(dp, 2), camDistM=round(dclose, 2), tris=tris, lod='LOD%d (frac %.2f) ESTIMATE' % (lod_i, frac),
              areaW=min(3.0, max(0.3, (ext[0] * ext[1] / 25.0) ** 0.5)), notes='%s %s x%.2f ext %.1fx%.1fx%.1f m (%s)' % (p['library'], p['asset'], sc, ext[0], ext[1], ext[2], p['_layout']))
    rec['_p'] = p; rec['_info'] = info
    fl = rec['flags']
    if info:
        ms = info['mats']
        base = [m for m in ms if m['baseDim']]
        bestm = max(ms, key=lambda m: m['tris']) if ms else None
        # tpm area-weighted over materials that have a tpm
        num = sum(m['tpm'] * m['tris'] for m in ms if m['tpm']); den = sum(m['tris'] for m in ms if m['tpm'])
        tpm = (num / den) / sc if den else None
        if tpm:
            rec['tpm'] = round(tpm, 1); rec['pxPerTexel'] = round((PPM_K / dclose) / tpm, 2)
            rec['tpmSrc'] = 'MEASURED glb UV/world area ratio of LOD0 (%s) / layout scale %.2f' % (', '.join('%s %sx%s' % (m['name'], *m['baseDim']) for m in base[:2]), sc)
        norm = [m for m in ms if m['tris'] > 0]
        rec['normal'] = 'present' if norm and all(m['normal'] for m in norm) else ('MISSING on ' + ','.join(m['name'] for m in norm if not m['normal'])[:60])
        if rec['normal'].startswith('MISSING'): fl.append('flat(no normal)')
        # effective material adjustments (matAdjust rows incl. kits)
        effs = [(m, mat_effect(m['name'], BASE_ROWS)) for m in ms if m['tris'] > 0]
        effk = [(m, mat_effect(m['name'], ALL_MAT_ROWS)) for m in ms if m['tris'] > 0]
        kitcover = sorted({s for m, e in effk for s in e['srcs'] if s.endswith('[kit]')})
        if bestm:
            e = dict(effs[[m['name'] for m, _ in effs].index(bestm['name'])][1]) if effs else {}
            smax = e.get('smoothMax'); smin = e.get('smoothMin')
            rough = bestm['rough'] if bestm['mr'] is False else None
            est_s = (1 - rough) if rough is not None else None
            rec['smoothMin'] = smin
            rec['smoothMax'] = smax if smax is not None else (round(est_s, 2) if est_s is not None else None)
            col = e.get('color')
            alb = bestm.get('albedo')
            if alb:
                a_l = float(np.mean(alb['medLin'])) * (float(np.mean(col)) if col else 1.0)
                rec['albedoLin'] = round(a_l, 4)
                lit, sh = light_est(zone, a_l); rec['light'] = 'lit~%d sh~%d /255 (ESTIMATE)' % (lit, sh)
                if lit < 16: fl.append('near-black(lit~%d)' % lit)
                pass
            rec['tint'] = ('x'.join('%.2f' % c for c in col)) if col else 'none'
            if e.get('srcs'): rec['notes'] += ' | matAdjust ' + ','.join(e['srcs'])
            if kitcover: rec['covered'] = 'gfx_fix_materials (' + ','.join(kitcover) + ')'
            if (e.get('smoothMin') or 0) > 0.7: fl.append('mirror/sparkle')
            if est_s is not None and est_s > 0.7 and smax is None and bestm['metal'] < 0.2: fl.append('mirror/sparkle(est)')
    if tpm_ := rec['tpm']:
        if tpm_ < 256: fl.append('low-res')
        if rec['pxPerTexel'] and rec['pxPerTexel'] > 4: fl.append('magnified>4x')
    if tris is not None and tris < 1500 and max(ext) >= 1.5: fl.append('blobby(low tri)')
    elif tris is not None and tris < 4000 and max(ext) >= 4.0: fl.append('blobby(low tri)')

# ---- C. foliage variants per zone (foliage.json; live enabled flag)
FO = sa('foliage.json')
fvar = {v['id']: v for v in FO['variants']}
for zid_row in FO['zones']:
    zid = zid_row['id']
    cl = [c for c in FO['clumps'] if c['zone'] == zid]
    for vid, v in fvar.items():
        cv = [c for c in cl if c['v'] == vid]
        if not cv: continue
        near = [(path_dist(c['x'], c['z']), c) for c in cv]
        near = [n for n in near if n[0] <= A.radius]
        if not near: continue
        dmin = min(n[0] for n in near)
        tp = os.path.join(SA, 'Foliage', v['tex'] + '.png')
        dims = png_dims(tp) if os.path.exists(tp) else None
        tpm = (dims[0] / v['widthM']) if dims else None
        rec = new(zid, 'foliage', 'foliage:%s (%d clumps near path)' % (vid, len(near)), distPathM=round(dmin, 2), camDistM=round(max(dmin, 1.5), 2),
                  tpm=round(tpm, 1) if tpm else None, pxPerTexel=round(PPM_K / max(dmin, 1.5) / tpm, 2) if tpm else None,
                  tpmSrc='MEASURED png %s / card widthM %.2f' % (dims, v['widthM']), smoothMax=v['smoothness'], tint='x'.join('%.2f' % c for c in v['tint']),
                  normal='none (alpha-cut cards, no normal map)', areaW=0.6, notes='live foliage.json enabled=%s (master switch)' % FO.get('enabled'))
        rec['flags'].append('flat(no normal)')
        if tpm and tpm < 256: rec['flags'].append('low-res')
        if not FO.get('enabled'): rec['flags'].append('DISABLED-live (nothing renders)')

# ---- D. hub / camp NPC bodies and bosses (glb under StreamingAssets)
HUB = sa('hub.json'); ZJ = sa('zones.json')
spots = {}
for q in ZJ['zones']:
    for s in q['spots']:
        spots[(q['id'], s['id'])] = s['pos']
npc_rows = []
for n in HUB['npcs']:
    z = spots.get(('hub_camp', n['spot']))
    npc_rows.append(('hub_camp', n['id'], n['runtime']['glb'], n['runtime'].get('scale', 1.0), z))
for n in HUB['camps'][0]['npcs']:
    z = spots.get(('z7_market2', n['spot']))
    npc_rows.append(('z7_market2', n['id'], n['runtime']['glb'], n['runtime'].get('scale', 1.0), z))
for zid, nid, g, sc, pos in npc_rows:
    if not pos or len(pos) < 2: continue
    gp = os.path.join(SA, g.replace('/', os.sep))
    if not os.path.exists(gp): continue
    x, zz = pos[0], pos[1]
    dp = path_dist(x, zz)
    if dp > A.radius: continue
    info = glb_full(gp)
    ms = [m for m in info['mats'] if m['tris'] > 0]
    num = sum(m['tpm'] * m['tris'] for m in ms if m['tpm']); den = sum(m['tris'] for m in ms if m['tpm'])
    tpm = (num / den) / sc if den else None
    dcl = max(dp - 0.3, 1.0)
    dcam, _ = cam_dist(x, 1.0, zz)
    rec = new(zid, 'npc', nid + ' ' + os.path.basename(g), distPathM=round(dp, 2), camDistM=round(max(dcam - 0.4, 1.0), 2), tris=info['tris'],
              tpm=round(tpm, 1) if tpm else None, tpmSrc='MEASURED glb UV/world ratio / scale %.2f' % sc, areaW=0.8,
              notes='runtime glb; textures %s' % ', '.join('%s %sx%s' % (m['name'], *(m['baseDim'] or ['?', '?'])) for m in ms[:3]))
    rec['pxPerTexel'] = round(PPM_K / rec['camDistM'] / tpm, 2) if tpm else None
    rec['normal'] = 'present' if ms and all(m['normal'] for m in ms) else 'MISSING'
    rec['_info'] = info; rec['_p'] = {'asset': nid}
    if rec['normal'] == 'MISSING': rec['flags'].append('flat(no normal)')
    if tpm and tpm < 256: rec['flags'].append('low-res')
    # matAdjust by material name
    for m in ms[:1]:
        e = mat_effect(m['name'], ALL_MAT_ROWS)
        rec['smoothMax'] = e['smoothMax']; rec['tint'] = 'x'.join('%.2f' % c for c in e['color']) if e['color'] else 'none'
        if e['srcs']: rec['notes'] += ' | matAdjust ' + ','.join(e['srcs'])
        if any(s.endswith('[kit]') for s in e['srcs']): rec['covered'] = 'gfx_fix_materials'
        if m.get('albedo'):
            a_l = float(np.mean(m['albedo']['medLin'])) * (float(np.mean(e['color'])) if e['color'] else 1.0)
            rec['albedoLin'] = round(a_l, 4); lit, sh = light_est(zid, a_l); rec['light'] = 'lit~%d sh~%d /255 (ESTIMATE)' % (lit, sh)
            if lit < 16: rec['flags'].append('near-black(lit~%d)' % lit)
    if rec['tris'] and rec['tris'] < 20000: rec['flags'].append('blobby(low tri)')

# bosses from MonsterLook (bc7 blobs: 1 byte/texel +mips -> dims ESTIMATE; glb embedded dims MEASURED)
BOSS = [('pit_yard', 'boss:NpcAshReaver', 'NpcAshReaver', ('pit_yard', 'pitborn_stand')), ('sobek_reach', 'boss:Sobek', 'Sobek', ('sobek_reach', 'sobek_stand')),
        ('grendel', 'npc:NpcFenWitch (z4 position ASSUMED = grendel_stand)', 'NpcFenWitch', ('grendel', 'grendel_stand')),
        ('grendel', 'npc:NpcMarshWarden (z4 position ASSUMED = grendel_stand)', 'NpcMarshWarden', ('grendel', 'grendel_stand'))]
for zid, nid, folder, spot in BOSS:
    d = os.path.join(SA, 'MonsterLook', folder)
    if not os.path.isdir(d): continue
    glbs = [f for f in os.listdir(d) if f.endswith('.glb')]
    bc7 = [(f, os.path.getsize(os.path.join(d, f))) for f in os.listdir(d) if f.endswith('.bc7')]
    pos = spots.get(spot)
    if not pos: continue
    dp = path_dist(pos[0], pos[1]); dcam, _ = cam_dist(pos[0], 1.2, pos[1])
    tri = None; tpm = None; dimtxt = ''
    if glbs:
        try:
            info = glb_full(os.path.join(d, glbs[0]), want_median=False)
            ms = [m for m in info['mats'] if m['tris'] > 0]
            num = sum(m['tpm'] * m['tris'] for m in ms if m['tpm']); den = sum(m['tris'] for m in ms if m['tpm'])
            tpm = num / den if den else None; tri = info['tris']
            dimtxt = ','.join('%s %s' % (m['name'], m['baseDim']) for m in ms[:2])
        except Exception as e:
            dimtxt = 'glb parse failed'
    bcs = ','.join('%s %.1fMB' % (f, s / 1e6) for f, s in bc7)
    rec = new(zid, 'boss', nid, distPathM=round(dp, 2), camDistM=round(max(dcam - 0.6, 2.0), 2), tris=tri, tpm=round(tpm, 1) if tpm else None,
              tpmSrc='MEASURED glb UV/world ratio (embedded tex %s); bc7 blobs %s (dims ESTIMATE from byte size: bc7 = 1 B/texel)' % (dimtxt, bcs),
              normal='bc7 normal present' if any('normal' in f for f, _ in bc7) else 'see glb', areaW=1.5, notes='MonsterLook %s' % folder)
    rec['pxPerTexel'] = round(PPM_K / rec['camDistM'] / tpm, 2) if tpm else None
    if tpm and tpm < 256: rec['flags'].append('low-res')
    if tri and tri < 30000: rec['flags'].append('blobby(low tri)')
    mp = {'NpcAshReaver': 'Ash Reaver', 'Sobek': 'sobek', 'NpcFenWitch': 'fenwitch', 'NpcMarshWarden': 'marshwarden'}
    e = mat_effect(mp[folder], ALL_MAT_ROWS)
    if e['srcs']: rec['notes'] += ' | matAdjust ' + ','.join(e['srcs'])
    if any(s.endswith('[kit]') for s in e['srcs']): rec['covered'] = 'gfx_fix_materials'

# ---- E. density rows: props within 10 m per 100 m of path (measured count of layout rows) and foliage presence; the "empty zone" offender
for zid, a, b in ZONES:
    if zid not in zone_steps: continue
    zl = [r for r in OFF if r['zoneId'] == zid and r['kind'] == 'prop']
    zlen = max(1.0, (max(CM[zone_steps[zid], 2]) - min(CM[zone_steps[zid], 2])))
    dens = len(zl) / zlen * 100
    trees = [r for r in zl if re.search(r'tree|cedar|trunk|root', r['notes'], re.I)]
    rec = new(zid, 'density', 'prop density within %g m of path: %d rows over %.0f m = %.1f per 100 m' % (A.radius, len(zl), zlen, dens), distPathM=0.0, camDistM=8.0, areaW=1.0,
              notes='trees/cedars near path: %d; foliage clumps live enabled=%s' % (len(trees), FO.get('enabled')))
    rec['_dens'] = dens
    if dens < 12: rec['flags'].append('sparse(<12/100m)')
    if not trees and zid in ('z5_humbaba', 'z6_erlking'): rec['flags'].append('no-trees')

# ------------------------------------------------------------------ scoring
def score(r):
    s = 0.0
    fl = ' '.join(r['flags'])
    dcl = r['camDistM'] or 10
    close = min(2.0, max(0.35, 8.0 / max(dcl, 1.0)))
    if r['tpm'] is not None and r['tpm'] < 256: s += min(3.0, (256 - r['tpm']) / 256 * 3.0)
    if r['pxPerTexel']: s += min(3.0, max(0.0, r['pxPerTexel'] - 1) * 0.35)
    if 'flat(no normal)' in fl: s += 1.0
    if 'weak-normal' in fl: s += 0.6
    if 'mirror/sparkle' in fl: s += 2.5
    if 'near-black' in fl: s += 1.6
    if 'overbright' in fl: s += 1.2
    if 'blobby' in fl: s += 1.5
    if 'seam' in fl: s += 1.0
    if 'streaky' in fl: s += 0.8
    if 'LIGHT-FIRST' in fl: s += 1.0
    if 'WHITE-STREAKS' in fl: s += 6.0
    if 'DISABLED-live' in fl: s += 1.4
    if 'sparse' in fl: s += 2.2
    if 'no-trees' in fl: s += 1.5
    if r['smoothMax'] is not None and r['smoothMax'] > 0.85 and r['kind'] == 'ground': s += 1.2
    return round(s * close * (0.6 + 0.4 * r['areaW']), 2)


for r in OFF: r['score'] = score(r)

# ------------------------------------------------------------------ proposed HOT fix per row (exact file + key) and kit coverage
KG = KITS['ground']; KA = KITS['atmosphere']; KM = KITS['materials']; KP = KITS['props']


def gcover(rid, keys):
    tc = [c for c in KG if '[' + rid + ']' in c]
    return tc


try:
    KGR = {x['id']: x for x in J(os.path.join(LG, 'gfx_fix_ground', 'Assets', 'StreamingAssets', 'zone_ground_mat.json'))['rows']}
except Exception:
    KGR = {}
AUDIT_ROWS = {
    't_z1_pit_ash_slag': {'detailAlbedo': 0.0, 'normalScale': 0.6, 'tilingMul': 3.0, 'smoothMax': 0.3},
    't_z2_sobek_wet_mud': {'smoothMin': 0.6, 'smoothMax': 0.9, 'normalScale': 1.0, 'mix': 1.0},
    't_z3_camp_trodden_earth': {'normalScale': 0.5, 'smoothMax': 0.35},
    't_z6_erlking_ash': {'smoothMax': 0.1, 'normalScale': 1.1, 'detailAlbedo': 1.2, 'detailTilingMul': 1.0},
}
for r in OFF:
    k = r['kind']; fx = ''; ff = ''; fk = ''; cov = r['covered']; fc = ''
    if k.startswith('ground'):
        row = r['_row']; rid = row['id']
        kg = gcover(rid, None)
        kr = KGR.get(rid, {})
        kchg = {kk: kr[kk] for kk in kr if not kk.startswith('_') and kk in row and kr[kk] != row[kk]}
        post = dict(row); post.update(kchg)
        gs_ = r.get('_gs')
        dim_ = max(gs_['dims']) if gs_ else None
        mixp = post.get('mix', 1.0)
        tm_post = (post.get('tilingMul', 1.0) or 1.0) ** mixp
        tpm_post = dim_ * tm_post / 64.0 if dim_ else None
        r['tpmAfter'] = round(tpm_post, 1) if tpm_post else None
        r['smoothAfter'] = '%s/%s ns %s' % (post.get('smoothMin'), post.get('smoothMax'), post.get('normalScale'))
        cov = ('gfx_fix_ground sets ' + ', '.join('%s %s->%s' % (kk, row[kk], kchg[kk]) for kk in kchg)) if kchg else 'none'
        AUD = AUDIT_ROWS.get(rid, {})
        unc = {kk: vv for kk, vv in AUD.items() if post.get(kk) != vv}
        if r.get('pinned') == 1:
            sv = {}
            for tag_, nm in (('v1', 'grendel_silt_albedo.png'), ('v2', 'grendel_silt_albedo_v2.png'), ('v3', 'grendel_silt_albedo_v3.png')):
                g_ = gstat(nm)
                if g_: sv[tag_] = g_
            v3 = sv.get('v3') or {}
            tmean = float(np.mean(post.get('tint', [1, 1, 1]))) if post.get('tint') else 1.0
            p99_eff = v3.get('lumaP99', 0) * tmean
            ratios = ', '.join('%s %.1f' % (k_, g_['lumaP99'] / max(g_['lumaMed'], 1e-4)) for k_, g_ in sv.items())
            fx = ('MEASURED cause candidate: the live silt albedo v3 has %.1f %% of texels above 0.5 linear (v1 %.2f %%), luma p99 %.2f x tint %.2f = %.2f linear on screen; tile seams are NOT the cause (seam ratio %.2f, 1 = seamless); '
                  'p99/median is about the same in every variant (%s), so a variant swap does not help; the f_000400 ground band has %.1f %% of pixels above 0.45 linear. '
                  'HOT: tint x0.8 (0.50,0.48,0.46 -> 0.40,0.38,0.37, p99 %.2f -> %.2f linear; look change, one-row revert); OR the opt-in zone_ground_mat.E1_exclude_b2.json (needs Dom OK). Tried and not the cause (README): smoothMax 0.12, tilingMul 1.0, normalScale 0.3, split row.' %
                  (v3.get('brightFrac', 0) * 100, (sv.get('v1') or {}).get('brightFrac', 0) * 100, v3.get('lumaP99', 0), tmean, p99_eff, v3.get('seamRatio', 0), ratios, (FRAME_META.get('brightFrac') or 0) * 100, p99_eff, p99_eff * 0.8))
            ff = 'Assets' + chr(92) + 'StreamingAssets' + chr(92) + 'zone_ground_mat.json'; fk = 'rows[t_z4_grendel_bone_silt].tint | excludeRenderer=["_b2"]'; fc = 'ground-albedo'
        else:
            if unc:
                fx = 'audit values not yet in the kit: ' + ', '.join('%s %s->%s' % (kk, post.get(kk), vv) for kk, vv in unc.items())
                ff = 'Assets\\StreamingAssets\\zone_ground_mat.json'; fk = 'rows[%s].%s' % (rid, ','.join(unc)); fc = 'ground-audit'
            elif AUD:
                fx = 'audit values all set by gfx_fix_ground (no new row)'
            if (post.get('smoothMin') or 0) > 0.7:
                fx += ' | still mirror after kit: smoothMin %.2f -> 0.55' % post['smoothMin']; ff = ff or 'Assets\\StreamingAssets\\zone_ground_mat.json'; fk = (fk + ',smoothMin') if fk else 'rows[%s].smoothMin' % rid; fc = fc or 'ground-spec'
            if tpm_post and tpm_post < 256:
                tm256 = 256 * 64.0 / dim_
                fx += (' | residual after kit: %.0f tpm (%s px, period %.1f m, %.1fx magnified): no data key adds texels; asset step = 2x-4x upsize of groundmat\\%s then HOT drop-in; '
                       'data-only trade: tilingMul %.1f would give 256 tpm but a %.1f m repeat' % (tpm_post, dim_, 64.0 / tm_post, (PPM_K / max(r['camDistM'] or 2, 1)) / tpm_post, row.get('baseColorMap'), tm256, 64.0 / tm256))
                if not fc: fc = 'ground-upsize'; ff = 'Assets\\StreamingAssets\\groundmat'; fk = row.get('baseColorMap') or ''
        if 'near-black' in ' '.join(r['flags']):
            fx += ' | in the crushed-black band: the zone LIGHT row (section 4) goes first'
    elif k == 'prop' or k == 'npc' or k == 'boss':
        fl = ' '.join(r['flags'])
        info = r.get('_info'); p = r.get('_p') or {}
        mname = ''
        if info:
            ms = [m for m in info['mats'] if m['tris'] > 0]
            mname = max(ms, key=lambda m: m['tris'])['name'] if ms else ''
        if 'near-black' in fl:
            fx = 'colour multiplier up (x1.5-2.4) on %s: matadjust row match "%s" color [1.5,1.45,1.4], smoothnessMax 0.3' % (p.get('asset', r['id']), mname)
            ff = 'Assets\\StreamingAssets\\matadjust_runtime.json'; fk = 'rows[+] match=%s color,smoothnessMax' % mname; fc = 'matadjust'
        elif 'mirror/sparkle' in fl:
            fx = 'matte %s: match "%s" smoothnessMax 0.30, metallicMax 0.05' % (p.get('asset', r['id']), mname)
            ff = 'Assets\\StreamingAssets\\matadjust_runtime.json'; fk = 'rows[+] match=%s smoothnessMax,metallicMax' % mname; fc = 'matadjust'
        elif 'flat(no normal)' in fl and k == 'prop':
            fx = 'no normal map in glb: no data fix adds one; HOT stand-in = grime decal row + matte (smoothnessMax 0.3); real fix = Blender normal bake (not HOT)'
            ff = 'Assets\\StreamingAssets\\prop_grime_decals.json'; fk = 'zones[%s] rows (+) / matadjust smoothnessMax' % r['zone']; fc = 'decal'
        elif 'blobby' in fl and k == 'prop' and info and p:
            l0 = info['tris']; cur = r.get('tris') or 0
            if cur and l0 > cur * 1.5:
                fx = 'blobby at the estimated LOD (%d tris vs %d at LOD0): props_overlay move (scale 0.01) of %s + props_runtime row with %s_LOD0.glb at the same pos/scale' % (cur, l0, r['id'], p.get('asset', '?'))
                ff = 'Assets\\StreamingAssets\\props_overlay.json + props_runtime.json'; fk = 'rows[+] op=move scale=0.01 ; props_runtime rows[+] glb=%s_LOD0.glb' % p.get('asset', '?'); fc = 'propswap'
            else:
                fx = 'blobby already at LOD0 (%d tris): needs a regen (Blender/Tripo), not HOT; HOT stand-in = matAdjust normalScale 1.5 on "%s"' % (l0, mname)
                ff = 'Assets\\StreamingAssets\\matadjust_runtime.json'; fk = 'rows[+] match=%s normalScale=1.5' % mname; fc = 'matadjust'
        elif 'low-res' in fl or 'blobby' in fl:
            fx = ('low texel density (%s tpm, %s px/texel): a higher LOD adds no texels; HOT stand-in = matAdjust normalScale 1.5 on "%s" (micro relief); real fix = texture upsize of the glb (Blender headless 2x-4x), then props_runtime / hub.json runtime GLB' % (r['tpm'], r['pxPerTexel'], mname))
            ff = 'Assets\\StreamingAssets\\matadjust_runtime.json'; fk = 'rows[+] match=%s normalScale=1.5' % mname; fc = 'matadjust'
        if not cov:
            cov = 'none'
        if fc == 'matadjust' and cov.startswith('gfx_fix_materials'):
            fx = '[KIT COVERS: ' + cov + '; re-measure after the flip] ' + fx
    elif k == 'foliage':
        kf = [c for c in KP if c.startswith('foliage.json')]
        cov = ('gfx_fix_props: foliage.json enabled=true + clumps added' if kf else 'none')
        if 'DISABLED-live' in ' '.join(r['flags']):
            fx = 'foliage.json enabled true (master switch)'; ff = 'Assets\\StreamingAssets\\foliage.json'; fk = 'enabled'; fc = 'foliage'
        else:
            fx = 'raise tpm: upsize Foliage\\%s.png 2x drop-in' % r['id'].split(':')[1].split(' ')[0]; ff = 'Assets\\StreamingAssets\\Foliage'; fk = 'png'; fc = 'foliage'
    elif k == 'density':
        kp = [c for c in KP if c.startswith(('props_runtime.json', 'foliage.json'))]
        if r['zone'] in ('z5', 'z6'): cov = 'gfx_fix_props: props_runtime trees/boulders + foliage clumps' if kp else 'none'
        else: cov = 'gfx_fix_props: foliage clumps (z4/z5/z6 only)' if kp else 'none'
        fx = 'add rows to props_runtime.json (enabled true) from existing GLBs (dead_tree_1a/b, hero_boulder_*_lite) off the lane; foliage.json clumps'
        ff = 'Assets\\StreamingAssets\\props_runtime.json'; fk = 'rows[+] zone=%s' % ZNUM.get(r['zoneId'], r['zoneId']); fc = 'propsadd'
    r['fix'] = fx; r['fixFile'] = ff; r['fixKey'] = fk; r['fixclass'] = fc; r['covered'] = cov

# ground-row coverage nuance for kit overlap: mark which keys the kit changes on each row
for r in OFF:
    if r['kind'].startswith('ground') and r.get('_row'):
        kg = gcover(r['_row']['id'], None)
        r['kitKeys'] = [c.split('/')[-1] for c in kg]
    else:
        r['kitKeys'] = []

# ------------------------------------------------------------------ LIGHT CHECKS per zone
CL = sa('camp_lights.json'); ACC = sa('accent_lights.json'); ATM = sa('atmosphere.json'); LU = sa('hdrp_lighting_ultra.json')
LIGHT = []
for zid, a, b in ZONES:
    lr = dict(LD); lr.update(LROW[zid])
    zn = ZNUM[zid]
    camp = [x for x in CL['rows'] if a <= x['pos'][2] < b]
    acc = [x for x in ACC['rows'] if x.get('zone') == zn or (zn == 'z3' and x.get('zone') == 'camp')]
    # note: accent_lights zone ids: z1..z7, camp
    elev = lr['keyElevationDeg'] if lr['keyElevationDeg'] > 0 else 30
    E_sun = KEY_LUX * math.sin(math.radians(max(elev, LZ['keyMinElevationDeg'])))
    zg = [g for g in GROUND_ROWS if zone_of_row(g[0]) == zid and g[5]]
    alb = zg[0][5] if zg else 0.1
    lit, sh = light_est(zid, alb)
    ratio = E_sun / max(lr['bounceLux'], 1)
    flags = []
    fz = FRAME_ZONE.get(zid) if isinstance(FRAME_ZONE, dict) else None
    if fz:
        if fz['crush16'] > 20: flags.append('NEAR-BLACK AREAS MEASURED: %.0f %% of pixels < 16/255' % fz['crush16'])
        if fz['med'] < 60: flags.append('LIGHT BEFORE ASSETS: frame median %.0f/255 (measured)' % fz['med'])
        if fz['p95'] < 120: flags.append('no highlights: p95 %.0f/255 (flat low key)' % fz['p95'])
        if fz['blown'] > 1.0: flags.append('blown %.1f %% > 235' % fz['blown'])
    if sh < 30 and not fz: flags.append('NEAR-BLACK SHADOWS (est %d/255)' % sh)
    if ratio > 6: flags.append('key/fill ratio %.1f:1 hard' % ratio)
    if lr['fogMeanFreePath'] < 60: flags.append('heavy fog (mfp %.0f m)' % lr['fogMeanFreePath'])
    if len(camp) + len(acc) < 4 and zid not in ('hub_camp', 'z7_market2'): flags.append('few local lights (%d)' % (len(camp) + len(acc)))
    if lit > 215: flags.append('overbright ground est %d' % lit)
    nz = [r for r in OFF if r['zoneId'] == zid and r['kind'] in ('prop', 'npc', 'boss')]
    dark = [r for r in nz if any(f.startswith('near-black') for f in r['flags'])]
    if len(dark) >= 3: flags.append('%d near-black props/NPCs: light/exposure before textures' % len(dark))
    keyc = lr['keyColor']; rim = lr.get('moonRimColor')
    LIGHT.append({'zone': zn, 'zoneId': zid, 'keyLuxEst': round(E_sun), 'keyElev': elev, 'keyColor': keyc, 'bounceLux': lr['bounceLux'], 'keyFillRatio': round(ratio, 1),
                  'postExp': lr['postExposure'], 'ev': EV, 'fogMFP': lr['fogMeanFreePath'], 'fogMaxH': lr['fogMaxHeight'], 'vignette': lr['vignette'],
                  'campLights': len(camp), 'accentLights': len(acc), 'accentMaxCd': max([x['candela'] for x in acc] or [0]), 'campMaxCd': max([x['candela'] for x in camp] or [0]),
                  'rimPresent': 'moonRim %s; hero Rim_Character %s lux; Rim_Player/Rim_Enemy rigs (global)' % (rim, LZ['hero']['rimCharacterLux']), 'litEst': lit, 'shEst': sh,
                  'flags': flags, 'nDark': len(dark), 'frame': fz})

# ---- light rows: zones whose MEASURED frame median is low get a look_zones row proposal (conflict-aware against the atmosphere kit)
def kit_has(rowid, key):
    return any(c.endswith('rows[%s]/%s' % (rowid, key)) for c in KITS['atmosphere'])


for lz in LIGHT:
    fz = lz['frame']
    if not fz or fz['med'] >= 45: continue
    zid = lz['zoneId']; lr = LROW[zid]
    ev_need = min(1.0, math.log2((55.0 / max(fz['med'], 1)) ** 2.2))
    ch = []; kitk = [k for k in ('postExposure', 'bounceLux', 'vignette') if kit_has(zid, k)]
    if not kit_has(zid, 'postExposure'): ch.append(('postExposure', round(lr['postExposure'] + ev_need, 2), lr['postExposure']))
    if lz['keyFillRatio'] > 5 and not kit_has(zid, 'bounceLux'): ch.append(('bounceLux', round(min(lr['bounceLux'] * 1.8, lz['keyLuxEst'] / 4.0)), lr['bounceLux']))
    if not ch and not kit_has(zid, 'vignette'): ch.append(('vignette', 0.30, lr['vignette']))
    rec = new(zid, 'light', 'zone light row %s: frame median %.0f/255, %.0f %% of pixels < 16/255, p95 %.0f' % (zid, fz['med'], fz['crush16'], fz['p95']), distPathM=0.0, camDistM=None, areaW=3.0,
              notes='measured over %d FK1 frames; key:fill %.1f:1; fog mfp %.0f m' % (fz['n'], lz['keyFillRatio'], lz['fogMFP']))
    rec['flags'] += ['LIGHT-BEFORE-ASSETS (measured)']
    rec['score'] = round(14 + (60 - fz['med']) / 60 * 10 + fz['crush16'] / 10, 2)
    rec['_light'] = True
    if ch:
        rec['fix'] = '; '.join('%s %s -> %s' % (c[0], c[2], c[1]) for c in ch) + ' (about +%.2f EV; look change, one-row revert, judge with matched f_000xxx frames)' % (ev_need if ch[0][0] == 'postExposure' else 0)
        rec['fixKey'] = 'rows[%s].%s' % (zid, ','.join(c[0] for c in ch))
        rec['fixFile'] = 'Assets'+chr(92)*2+'StreamingAssets'+chr(92)*2+'look_zones.json'; rec['fixclass'] = 'zonelight'
        rec['covered'] = ('gfx_fix_atmosphere edits %s on this row (keys avoided)' % ','.join(kitk)) if kitk else 'none'
    else:
        rec['fix'] = 'all candidate keys are already edited by gfx_fix_atmosphere; re-measure after that kit flips'; rec['fixclass'] = 'zonelight'
        rec['covered'] = 'gfx_fix_atmosphere'
    rec['kitKeys'] = []

# ------------------------------------------------------------------ pinned top rows (audit): z1 pit floor, z2 mud, z3 camp ground, z6 glossy band
PIN = {'t_z1_pit_ash_slag': 2, 't_z2_sobek_wet_mud': 3, 't_z3_camp_trodden_earth': 4, 't_z6_erlking_ash': 5}
for r in OFF:
    if r['kind'] == 'ground' and r['_row']['id'] in PIN: r['pinned'] = PIN[r['_row']['id']]
    r.setdefault('pinned', 0)
# specific audit text for the pinned rows (numbers measured in the table, fix keys from the audit)
PINFIX = {
    't_z1_pit_ash_slag': ('detailAlbedo 1.5 -> 0, normalScale 0.9 -> 0.6, tilingMul 4.4 -> 3.0, smoothMax 0.4 -> 0.3', 'rows[t_z1_pit_ash_slag].detailAlbedo,normalScale,tilingMul,smoothMax'),
    't_z2_sobek_wet_mud': ('smoothMin 0.85 -> 0.6, smoothMax 1.0 -> 0.9, normalScale 0.9 -> 1.0 (mix 0.8 -> 1.0)', 'rows[t_z2_sobek_wet_mud].smoothMin,smoothMax,normalScale,mix'),
    't_z3_camp_trodden_earth': ('normalScale -1 -> 0.5, smoothMax 0.5 -> 0.35 (corduroy relief + sparkle + band); tilingMul 1.5 stays', 'rows[t_z3_camp_trodden_earth].normalScale,smoothMax'),
    't_z6_erlking_ash': ('smoothMax 0.25 -> 0.1, normalScale 0.9 -> 1.1, detailAlbedo 2.0 -> 1.2, detailTilingMul 0.25 -> 1.0 (glossy streaks)', 'rows[t_z6_erlking_ash].smoothMax,normalScale,detailAlbedo,detailTilingMul'),
}
for r in OFF:
    if r.get('pinned') and r['kind'] == 'ground' and r['_row']['id'] in PIN: r['flags'].append('AUDIT top item')

# ------------------------------------------------------------------ outputs
def srt(rs): return sorted(rs, key=lambda r: -r['score'])


def row_label(r):
    return r['id'] if len(r['id']) < 60 else r['id'][:57] + '...'


def short(x, n=95): return x if len(x) <= n else x[:n - 3] + '...'


ordered_all = srt([r for r in OFF if not r['pinned']])
pinned = sorted([r for r in OFF if r['pinned']], key=lambda r: r['pinned'])
TOP = pinned + ordered_all
for i, r in enumerate(TOP): r['rank'] = i + 1

# CSV
cols = ['rank', 'zone', 'kind', 'id', 'score', 'distPathM', 'camDistM', 'tpm', 'pxPerTexel', 'smoothMin', 'smoothMax', 'tint', 'normal', 'tris', 'lod', 'albedoLin', 'flags', 'light', 'covered_by_kit', 'fix_class', 'fix', 'fix_file', 'fix_key', 'tpm_source', 'notes']
with open(os.path.join(OUT, 'OFFENDERS.csv'), 'w', newline='', encoding='utf-8') as f:
    w = csv.writer(f); w.writerow(cols)
    for r in TOP:
        w.writerow([r['rank'], r['zone'], r['kind'], r['id'], r['score'], r['distPathM'], r['camDistM'], r['tpm'], r['pxPerTexel'], r['smoothMin'], r['smoothMax'], r['tint'], r['normal'], r['tris'], r['lod'], r['albedoLin'],
                    '|'.join(r['flags']), r['light'], r['covered'], r['fixclass'], r['fix'], r['fixFile'], r['fixKey'], r['tpmSrc'], r['notes']])

# ------------------------------------------------------------------ ROUND PLAN (5-8 rows, HOT, no (file,row,key) already touched by a kit)
def touched_by_kits(r):
    """True if the proposed (file,row,key) overlaps a kit edit. ground rows: any kit key in the row. matadjust: kit rows matching the material."""
    if r['fixclass'] in ('ground-spec', 'ground-detail', 'ground-audit', 'ground-albedo'):
        mykeys = set(re.findall(r'[A-Za-z]+', r['fixKey'].split('].')[-1] if '].' in r['fixKey'] else r['fixKey']))
        kk = set(r.get('kitKeys') or [])
        return sorted(mykeys & kk)
    if r['fixclass'] == 'matadjust':
        return ['matadjust kit rows match this material'] if r['covered'].startswith('gfx_fix_materials') else []
    if r['fixclass'] in ('propsadd', 'foliage'):
        return ['gfx_fix_props'] if r['covered'].startswith('gfx_fix_props') else []
    if r['fixclass'] == 'light':
        return []
    return []


ROUNDS = []
cand = [r for r in TOP[:60] if r['fix']]
# split candidates: ones fully covered by a kit keys -> "covered" list; others -> plan
covered_rows = []
free_rows = []
for r in cand:
    t = touched_by_kits(r)
    if t and r['fixclass'] in ('matadjust', 'propsadd', 'foliage'):
        covered_rows.append((r, t)); continue
    free_rows.append((r, t))
NOT_HOT = ('ground-upsize', 'propup')
asset_rows = [(r, t) for r, t in free_rows if r['fixclass'] in NOT_HOT]
free_rows = [(r, t) for r, t in free_rows if r['fixclass'] not in NOT_HOT and not (r['fixclass'] == 'matadjust' and 'match= ' in r['fixKey'] + ' ')]
seenk = set(); uniq = []
for r, t in sorted(free_rows, key=lambda x: x[0]['rank']):
    kk = (r['fixFile'], r['fixKey'] if r['fixclass'] in ('propswap', 'decal', 'propsadd', 'foliage') else r['fixKey'] + '|' + r['fixclass'])
    if kk in seenk: continue
    seenk.add(kk); uniq.append((r, t))
n = 0
for i in range(0, len(uniq), 7):
    chunk = uniq[i:i + 7]
    if len(chunk) < 5 and ROUNDS and len(ROUNDS[-1]['rows']) + len(chunk) <= 8:
        ROUNDS[-1]['rows'] += chunk; continue
    n += 1; ROUNDS.append({'n': n, 'rows': chunk})
for rd in ROUNDS:
    rd['title'] = 'rank %d-%d, files: %s' % (rd['rows'][0][0]['rank'], rd['rows'][-1][0]['rank'], ', '.join(sorted({x[0]['fixFile'].split(chr(92))[-1] for x in rd['rows']})))

# ------------------------------------------------------------------ markdown
def f(v, nd=1):
    if v is None: return '-'
    return ('%.*f' % (nd, v)) if isinstance(v, float) else str(v)


L = []
now = time.strftime('%Y-%m-%d %H:%M')
L.append('# Frankendom OFFENDER LIST (gfx_offenders.py, %s)\n' % now)
L.append('Tool: `Tools\\gfx_offenders.py` (read-only; reruns in about 1 min with `cache.json`). Path: FK1 run `fk1_20261118_171013` camtrace (%d steps, camera and player positions), radius %g m. Data: live `Assets\\StreamingAssets` rows (repo twin of the shipped build), glb headers/accessors in `Assets\\Env` and `StreamingAssets\\Npc|MonsterLook`, `groundmat\\*.png`.\n' % (len(rows), A.radius))
L.append('## 0. What is measured and what is an estimate (no engine access)\n')
L.append('| Column / claim | Status | How |\n|---|---|---|')
L.append('| Walk path, camera distance (3-D, bbox-corrected), path distance | MEASURED positions + ESTIMATED surface offset | camtrace.csv positions; prop footprint from the glb POSITION min/max x layout scale; distance = centre distance minus half the bbox diagonal (floor 1 m) |')
L.append('| Texture size (base colour, normal present/missing, mr present) | MEASURED | PNG/JPEG header inside the glb, groundmat PNG header, material JSON |')
L.append('| Texels per metre (tpm) props/NPCs | MEASURED on the LOD0 glb at node scale 1, divided by the layout scale | sqrt(sum UV area / sum world area) x texture edge, area-weighted over materials (up to 200 k triangles sampled per primitive). Not run through the real import (no glTFast, no node transforms) |')
L.append('| Texels per metre (ground) | MEASURED from rows | texture edge x tilingMul^mix / 64 m (terrain UV 1 = 64 m, look.json note). Matches the audit (mud 1254 px x 1.45 = 28 tpm) |')
L.append('| px-per-texel (screen px per texel at the closest camera distance) | ESTIMATE | 1080p, vertical fov 50 deg: 1158 px per metre at 1 m; >1 means magnified (blurry), >4 flagged |')
L.append('| Smoothness min/max, tint, normalScale | MEASURED from rows (ground clone rows; matAdjust rows in file order, later wins, kit rows included, match = case-insensitive substring of the glb material name as in MatAdjustRuntime.NameHas) | props without a smoothness row show the glb roughness factor (est. = 1 - roughness) only when there is no MR texture |')
L.append('| LOD in use and triangle count | ESTIMATE of the LOD (lodProfile screenHeights x object height / screen height at the closest distance); triangle count of that LOD file MEASURED | |')
L.append('| Blobby / low-res / mirror / flat flags | HEURISTIC thresholds on the measured numbers: blobby = LOD tris < 1500 with size >= 1.5 m (or < 4000 with size >= 4 m; NPC/boss < 20-30 k); low-res = tpm < 256; mirror/sparkle = smoothMin > 0.7 (or glb smoothness > 0.7, est.); flat = no normal map | |')
L.append('| Albedo (linear) | MEASURED median of the base-colour texture (downsampled) x the row colour/tint | |')
L.append('| lit~ / sh~ display values /255 | ESTIMATE only | Lambert L = albedo x E / pi, E = key 7000 lux x keyGroundMul 0.95 x sin(elevation) x key colour + bounceLux x bounce colour (shadow = bounce only; sky ambient and the local lights NOT included), fixed EV %.1f + zone postExposure, ACES, sRGB. Use it to rank, not as a pixel prediction |' % EV)
L.append('| Shadow strength | NOT IN DATA | no data key found (grep of StreamingAssets); engine default assumed, listed as n/a |')
L.append('| Kit coverage | MEASURED diff | each `gfx_fix_*` kit twin json diffed against the live json (row id + key); materials kit rows are the `_gfx1` rows |')
L.append('| White streaks, frame f_000400 | MEASURED on the jpg | ground band pixels of the frame (rows %s) |\n' % (FRAME_META.get('bandRows')))
if FRAME_META and 'error' not in FRAME_META:
    L.append('Frame f_000400 measured: zone %s, player (%s, %s), camera (%s, %s, %s), ground band mean linear luma %.3f, p99 %.3f, share of pixels above 0.45 linear (the white streaks) %.1f %%.\n' %
             (FRAME_META.get('zone'), FRAME_META.get('px'), FRAME_META.get('pz'), FRAME_META.get('cx'), FRAME_META.get('cy'), FRAME_META.get('cz'), FRAME_META['bandMeanLin'], FRAME_META['bandP99'], FRAME_META['brightFrac'] * 100))
L.append('Scoring (rank only, higher = worse): sum of tpm deficit (up to 3), magnification (up to 3), no normal 1, mirror 2.5, near-black 1.6, blobby 1.5, seam 1, light-first 1, sparse zone 2.2, disabled system 1.4, white streaks 6; x closeness (8 m / distance, 0.35-2) x area weight. Rows 1-5 are pinned by order from the brief; the rest are ranked by score.\n')

# section 1 top-30
L.append('## 1. TOP 30 across zones (row 1 = the z4 white streaks, rows 2-5 = the audit top items)\n')
L.append('| # | Zone | Surface | Cam m | tpm | px/texel | smooth min/max | tint | normal | Flags | Score | Kit coverage | Proposed HOT fix (file : key) |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|')
for r in TOP[:30]:
    L.append('| %d | %s | %s %s | %s | %s | %s | %s/%s | %s | %s | %s | %s | %s | %s : `%s` |' % (r['rank'], r['zone'], r['kind'], short(row_label(r), 55), f(r['camDistM']), f(r['tpm'], 0), f(r['pxPerTexel']), f(r['smoothMin'], 2), f(r['smoothMax'], 2), short(r['tint'], 22), short(r['normal'], 24),
                                                                                  ', '.join(r['flags']) or '-', r['score'], short(r['covered'], 60), short(r['fix'], 150) or '-', (r['fixFile'].split('\\')[-1] + ' ' + r['fixKey'])[:110] if r['fixFile'] else '-'))
L.append('')
# section 2 : ground albedo table
L.append('## 2. Ground textures, measured (the z4 streak decision table)\n')
L.append('Seam ratio = opposite-edge mismatch / ordinary neighbour difference (1 = seamless, >3 visible grid). Aniso = row-or-column mean variance / total variance (streakiness). Bright = share of texels above 0.5 linear. MacroCV = 32 px block-mean variation.\n')
L.append('| PNG | dims | median linear rgb | luma p99 | bright share | seam ratio | aniso | macroCV | used by (enabled rows) |\n|---|---|---|---|---|---|---|---|---|')
users = {}
for (r, zone, tpm, period, gs, alb_l, tm_eff, tint_eff, hasn) in GROUND_ROWS:
    for k in ('baseColorMap', 'normalMap', 'maskMap'):
        if r.get(k): users.setdefault(r[k], []).append(ZNUM[zone] + ':' + r['id'])
for fn in sorted(set(glob.glob(os.path.join(SA, 'groundmat', '*_albedo*.png')))):
    b = os.path.basename(fn); gs = gstat(b)
    if not gs: continue
    L.append('| %s | %sx%s | %s | %s | %s | %s | %s | %s | %s |' % (b, gs['dims'][0], gs['dims'][1], gs['medLin'], gs['lumaP99'], gs['brightFrac'], gs['seamRatio'], gs['aniso'], gs['macroCV'], ', '.join(sorted(set(users.get(b, [])))) or '(unused)'))
L.append('')
# section 3 : per-zone ranked tables
L.append('## 3. Per-zone ranked offenders (worst first; every surface within %g m of the path)\n' % A.radius)
for zid, a, b in ZONES:
    zr = srt([r for r in OFF if r['zoneId'] == zid])
    L.append('### %s %s (z %g..%g), %d surfaces\n' % (ZNUM[zid], zid, a if a > -1e5 else 0, b if b < 1e5 else 362, len(zr)))
    L.append('| # | Kind | Surface | Path m | Cam m | tpm | px/tex | smooth | tint | normal | tris | Flags | Score | Kit | Proposed HOT fix |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    for i, r in enumerate(zr[:25]):
        L.append('| %d | %s | %s | %s | %s | %s | %s | %s/%s | %s | %s | %s | %s | %s | %s | %s |' % (i + 1, r['kind'], short(row_label(r), 50), f(r['distPathM']), f(r['camDistM']), f(r['tpm'], 0), f(r['pxPerTexel']), f(r['smoothMin'], 2), f(r['smoothMax'], 2), short(r['tint'], 20),
                                                                                       short(r['normal'], 20), r['tris'] if r['tris'] is not None else '-', ', '.join(r['flags']) or '-', r['score'], short(r['covered'], 40), short(r['fix'], 110) or '-'))
    if len(zr) > 25: L.append('\n(+%d more rows in OFFENDERS.csv)' % (len(zr) - 25))
    L.append('')
# section 4 : light checks
L.append('## 4. LIGHT checks per zone (estimates from rows; no engine)\n')
L.append('Global: Key_ColdSun 7000 lux (hdrp_lighting_ultra.json), keyGroundMul 0.95, fixed exposure EV %.1f (look.json), shadows: 2 cascades, max distance 40 m, 2048 map, contact shadows on; shadow strength has no data key (n/a). Rim: hero Rim_Character %s lux (look_zones.json), Rim_Player / Fill_Player / Rim_Enemy spot rigs (hdrp_fixes.json), moonRimColor per zone.\n' % (EV, LZ['hero']['rimCharacterLux']))
L.append('| Zone | Key lux (est, x sin elev) | Elev | Key colour | Bounce lux | Key:fill | postExp | Fog mfp / maxH | Camp lights | Accent lights | MEASURED frames n / median / p5 / p95 /255 | MEASURED <16 / <32 / >235 % | Sun-lit ground est (calibrated) | Shadow ground est | Flags |'+chr(10)+'|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
for lz in LIGHT:
    fz = lz['frame'] or {}
    L.append('| %s %s | %d | %s | %s | %s | %s:1 | %s | %s / %s | %d (max %d cd) | %d (max %d cd) | %s / %s / %s / %s | %s / %s / %s | %d/255 | %d/255 | %s |' % (lz['zone'], lz['zoneId'], lz['keyLuxEst'], lz['keyElev'], lz['keyColor'], lz['bounceLux'], lz['keyFillRatio'], lz['postExp'], lz['fogMFP'], lz['fogMaxH'],
                                                                                      lz['campLights'], lz['campMaxCd'], lz['accentLights'], lz['accentMaxCd'], fz.get('n', '-'), fz.get('med', '-'), fz.get('p5', '-'), fz.get('p95', '-'), fz.get('crush16', '-'), fz.get('crush32', '-'), fz.get('blown', '-'), lz['litEst'], lz['shEst'], '; '.join(lz['flags']) or 'ok'))
L.append('')
lf = [lz for lz in LIGHT if any('LIGHT BEFORE' in x or 'near-black props' in x or 'NEAR-BLACK' in x for x in lz['flags'])]
L.append('"Light before assets" zones (a light or exposure row is the cheaper first fix than a texture): %s.\n' % (', '.join('%s (%s)' % (z['zone'], '; '.join(x for x in z['flags'] if 'LIGHT' in x or 'near-black' in x or 'NEAR-BLACK' in x)) for z in lf) or 'none by the estimate'))
L.append('Local-light fix rows (HOT): `Assets\\StreamingAssets\\look_zones.json` rows[<zone id>].postExposure / bounceLux / keyElevationDeg / fogMeanFreePath; `camp_lights.json` rows[+] (z3/z7), `accent_lights.json` rows[+] (z1/z2/z4/z5/z6). The atmosphere kit already changes: %s.\n' % ('; '.join(sorted(KA))[:700] or 'nothing'))
try:
    spec = J(os.path.join(LG, 'gfx_fix_atmosphere', 'spec_v1.json'))['look_zones.json']['rows']
    notes_ = []
    for zid_, ch_ in spec.items():
        if 'postExposure' in ch_ and zid_ in LROW:
            d_ = ch_['postExposure'] - LROW[zid_]['postExposure']
            fzz = FRAME_ZONE.get(zid_, {})
            notes_.append('%s postExposure %+.2f EV (%s -> %s) vs measured frame median %s/255' % (ZNUM[zid_], d_, LROW[zid_]['postExposure'], ch_['postExposure'], fzz.get('med', '?')))
    L.append('**Atmosphere kit vs the measured darkness:** ' + '; '.join(notes_) + '. A negative EV on a zone whose frames already have 23-39/255 median and 23-37 % crushed pixels pushes the wrong way; the light rows above avoid those keys and the Lead should decide the kit values with this table.' + chr(10))
except Exception as e_:
    pass
# section 5 : kits
L.append('## 5. What the four running kits already change (diff vs live)\n')
for kit in ('ground', 'atmosphere', 'materials', 'props'):
    items = sorted(KITS[kit])
    L.append('- **gfx_fix_%s** (%d changes): %s' % (kit, len(items), '; '.join(short(i, 120) for i in items[:14]) + (' ...' if len(items) > 14 else '')))
if KIT_MAT: L.append('- materials kit `_gfx1` rows: %d (%s)' % (len(KIT_MAT), ', '.join(r['match'] for r in KIT_MAT)))
L.append('')
# section 6 : rounds
L.append('## 6. ROUND PLAN (HOT only, 5-8 rows per round, nothing in a round edits a (row,key) a kit already edits)\n')
L.append('Rule: each round ships after the kit it sits next to has flipped; every apply is a rebase-safe key edit on the CURRENT json (same method as the kits), and each row records the old value for a one-row revert. Rows already covered by a kit are listed at the end, not planned.\n')
for rd in ROUNDS:
    L.append('**Round %d: %s (%d rows)**\n' % (rd['n'], rd['title'], len(rd['rows'])))
    L.append('| Rank | Zone | Surface | File | Key | Change | Kit overlap |\n|---|---|---|---|---|---|---|')
    for r, t in rd['rows']:
        L.append('| %d | %s | %s | `%s` | `%s` | %s | %s |' % (r['rank'], r['zone'], short(row_label(r), 45), r['fixFile'].split('\\')[-1], short(r['fixKey'], 70), short(r['fix'], 170), ('KEYS ALSO IN KIT: ' + ','.join(t)) if t else 'none'))
    L.append('')
L.append('**Already covered by a kit (no new round):**\n')
for r, t in covered_rows[:20]:
    L.append('- #%d %s %s: %s' % (r['rank'], r['zone'], short(row_label(r), 55), r['covered']))
L.append('')
L.append('**Asset steps (not HOT-data; each ends in a HOT drop-in):** ' + '; '.join('#%d %s %s' % (r['rank'], r['zone'], short(row_label(r), 40)) for r, _ in asset_rows[:12]) + '.' + chr(10))
L.append('**Not HOT (needs an asset or Dom):** z4 far-chunk root cause (silt albedo seams on the coarse b2 mesh: seamless albedo or finer b2 mesh = baked terrain; the E1 exclude-b2 file changes the look and needs Dom OK); groundmat upscales (Blender/Flux step, then HOT drop-in, 3 files per ground); props with no normal map in the glb (Blender bake); blobby baked props (props_layout LOD swap = FULL, HOT stand-in = props_overlay + props_runtime).\n')
open(os.path.join(OUT, 'OFFENDERS.md'), 'w', encoding='utf-8').write('\n'.join(L))
json.dump(CACHE, open(CACHE_F, 'w'))
print('rows', len(OFF), 'zones', {z: sum(1 for r in OFF if r['zone'] == z) for z in ZNUM.values()})
print('rounds', [(r['n'], len(r['rows'])) for r in ROUNDS], 'covered', len(covered_rows))

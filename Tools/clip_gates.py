"""CLIPBUILD clip_gates.py: offline (CPU, numpy) gates for a built <Id>_combat.glb. Called by clip_build.py --gates, or:
  python clip_gates.py <built_combat.glb> <Id> [--live <current live combat glb>]   (needs clip_build.json next to ../Assets/Data and the roster)
Gates (numbers in clip_build.json "gates"; every failure names the clip and the value):
  G-pose   bone directions (hips frame) of the built clip vs the RAW source clip FK on its own skeleton: mean/worst degrees (body bones; fingers reported)
  G-knee   inward knee offset (m, + = knock-kneed) on every frame (<= kneeMedialMaxM), and leg-fix swivel (> swivelFailDeg = the source is suspect)
  G-toe    toe yaw (0 = straight ahead, + = toes out on the left foot, - on the right) vs the raw source (<= toeYawTolDeg) and vs the target (+12 / -12)
  G-plant  loop clips: lowest toe never sinks/floats more than footFloorMaxM vs rest; stance-foot slide m/s reported next to the raw source
  G-arm    mesh clearance: min distance sleeve (Arm/ForeArm weighted verts, shoulder cap excluded) to torso verts (Hips/Spine*) of the body mesh, per clip
  G-neck   head / neck extension (deg backwards relative to rest) vs headExtensionMaxDeg / neckExtensionMaxDeg
  G-sword  blade segment (forearm axis) to head sphere clearance (m) over every clip
"""
import json, math, os, sys
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import clip_lib as L
import leg_fix

try:
    from scipy.spatial import cKDTree
except Exception:
    cKDTree = None


class CellTree:   # SOBEKTUNE v1: scipy-free nearest distance (this machine has no scipy: the brute-force fallback took ~18 min per build). Exact for distances <= cell (all gate limits are far below it), a lower bound `cell` beyond.
    def __init__(s, pts, cell=0.10):
        s.cell = float(cell); s.pts = np.asarray(pts, float); k = np.floor(s.pts / s.cell).astype(np.int64)
        s.key = (k[:, 0] * 73856093) ^ (k[:, 1] * 19349663) ^ (k[:, 2] * 83492791)
        s.order = np.argsort(s.key, kind='stable'); s.skey = s.key[s.order]; s.spts = s.pts[s.order]

    def query(s, A):
        A = np.asarray(A, float); ka = np.floor(A / s.cell).astype(np.int64); best = np.full(len(A), s.cell)
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    kk = ka + np.array([dx, dy, dz]); key = (kk[:, 0] * 73856093) ^ (kk[:, 1] * 19349663) ^ (kk[:, 2] * 83492791)
                    lo = np.searchsorted(s.skey, key, 'left'); hi = np.searchsorted(s.skey, key, 'right'); cnt = hi - lo
                    has = np.where(cnt > 0)[0]
                    for c0 in range(0, len(has), 4000):
                        ids = has[c0:c0 + 4000]; n = cnt[ids]; tot = int(n.sum())
                        if tot == 0: continue
                        rep = np.repeat(ids, n); off = np.arange(tot) - np.repeat(np.cumsum(n) - n, n); tj = np.repeat(lo[ids], n) + off
                        d = np.linalg.norm(A[rep] - s.spts[tj], axis=1); np.minimum.at(best, rep, d)
        return best, None

FINGER = ('Thumb', 'Index', 'Middle', 'Ring', 'Pinky')


def unit(v):
    n = np.linalg.norm(v); return v / n if n > 1e-9 else v


# ---------------------------------------------------------------- knee (same quantity as leg_fix.fix_frame)
def knee_e_frame(sk, qh, legs):
    Rh = sk.R0 @ L.q2m(qh); Th = np.zeros(3) + sk.T0; worst = -9.0
    for side in ('Left', 'Right'):
        iu, il, ifo = sk.ids[side + 'UpLeg'], sk.ids[side + 'Leg'], sk.ids[side + 'Foot']
        qu, ql, qf = legs[side]
        Ru = Rh @ L.q2m(qu); Pu = Th + Rh @ sk.rt[iu]
        Rl = Ru @ L.q2m(ql); Pl = Pu + Ru @ sk.rt[il]
        Rf = Rl @ L.q2m(qf); Pf = Pl + Rl @ sk.rt[ifo]
        lat = Rh @ np.array([1.0, 0, 0]); other = Th + Rh @ sk.rt[sk.ids[('Right' if side == 'Left' else 'Left') + 'UpLeg']]
        med = lat if np.dot(other - Pu, lat) > 0 else -lat
        u = unit(Pf - Pu); p = (Pl - Pu) - np.dot(Pl - Pu, u) * u
        worst = max(worst, float(np.dot(p, med)))
    return worst


def knee_medial_anim(sk, j, b, anim, name2idx):
    ch = {(c['target']['node'], c['target']['path']): c for c in anim['channels']}
    need = [(sk.ids[s + k], 'rotation') for s in ('Left', 'Right') for k in ('UpLeg', 'Leg', 'Foot')]; hk = (sk.ids['Hips'], 'rotation')
    if any(k not in ch for k in need) or hk not in ch: return 0.0
    def get(k):
        sm = anim['samplers'][ch[k]['sampler']]; return leg_fix.acc_read(j, b, sm['input'])[:, 0], leg_fix.acc_read(j, b, sm['output'])
    data = {k: get(k) for k in need + [hk]}; ts = data[need[0]][0]; w = -9.0
    for t in ts:
        qh = L.sample_q(*data[hk], t)
        legs = {s: tuple(L.sample_q(*data[(sk.ids[s + k], 'rotation')], t) for k in ('UpLeg', 'Leg', 'Foot')) for s in ('Left', 'Right')}
        w = max(w, knee_e_frame(sk, qh, legs))
    return w


# ---------------------------------------------------------------- per-clip metrics from FK
class Metrics:
    def __init__(s, rig, mesh=None):
        s.rig = rig; s.mesh = mesh
        s.names = [n.get('name') for n in rig.nodes]
        s.bones = [i for i, n in enumerate(s.names) if n and n.startswith('mixamorig:')]
        s.child = {}
        pref = {'Hips': 'Spine', 'Spine': 'Spine1', 'Spine1': 'Spine2', 'Spine2': 'Neck', 'Hand': 'HandMiddle1'}   # which child defines the bone direction
        for i in s.bones:
            kids = [c for c in rig.nodes[i].get('children', []) if s.names[c] and s.names[c].startswith('mixamorig:')]
            if not kids: continue
            short = s.names[i][10:]; want = pref.get(short.replace('Left', '').replace('Right', ''))
            pick = [c for c in kids if want and s.names[c][10:] == want or (want and s.names[c][10:].replace('Left', '').replace('Right', '') == want)]
            if short.endswith('Hand'): pick = [c for c in kids if s.names[c].endswith('HandMiddle1')]
            if short.endswith('Shoulder'): pick = [c for c in kids if s.names[c].endswith('Arm') and 'Fore' not in s.names[c]]
            if short in ('Neck',): pick = [c for c in kids if s.names[c].endswith('Head')]
            s.child[i] = (pick or kids)[0]
        s.rest = s.frame(None, 0.0)

    def P(s, W, short): return W[s.rig.bone(short)][1]

    def frame(s, anim, t):
        W = s.rig.world(anim, t); r = {}
        hips = s.rig.bone('Hips'); Rh, Ph = W[hips]
        r['W'] = W; r['Rh'] = Rh
        lat = unit(s.P(W, 'LeftUpLeg') - s.P(W, 'RightUpLeg')); up = np.array([0, 1.0, 0]); fwd = unit(np.cross(lat, up)); r['lat'], r['fwd'] = lat, fwd
        # toe yaw
        ty = {}
        for side in ('Left', 'Right'):
            v = s.P(W, side + 'ToeBase') - s.P(W, side + 'Foot'); ty[side] = math.degrees(math.atan2(v[0], v[2]))   # world +Z = character heading (clips are in place, facing +Z); + = toes towards the character's left
            ty[side + 'Y'] = float(s.P(W, side + 'ToeBase')[1]); ty[side + 'P'] = s.P(W, side + 'ToeBase')
        r['toe'] = ty
        # head / neck extension (backwards pitch relative to the torso axis)
        tor = unit(s.P(W, 'Neck') - s.P(W, 'Spine1'))
        def pitch(v):
            v = unit(v); return -math.degrees(math.atan2(float(np.dot(np.cross(tor, v), lat)), float(np.dot(tor, v))))
        neck = s.P(W, 'Head') - s.P(W, 'Neck')
        top = s.rig.idx.get('mixamorig:HeadTop_End'); head = (W[top][1] - s.P(W, 'Head')) if top is not None else neck
        r['neckPitch'] = pitch(neck); r['headPitch'] = pitch(head)
        r['headC'] = s.P(W, 'Head') + unit(head) * 0.0
        r['headUp'] = unit(head)
        return r

    def pose_dirs(s, W, Rh, only_fingers=None):
        out = {}
        for i, c in s.child.items():
            nm = s.names[i]; fing = any(f in nm for f in FINGER)
            if only_fingers is not None and fing != only_fingers: continue
            out[nm] = unit(Rh.T @ (W[c][1] - W[i][1]))
        return out


def pose_diff(ma, fa, mb, fb, skip_legs=False):
    """per mixamorig bone: angle between the bone's orientation in the HIPS frame (product of local rotations from the Hips down) on rig a and on rig b.
    Independent of both skeletons' rest geometry, so it measures exactly 'same rotations as the source' (arm/leg/finger tuning, leg fix, hips frame errors show up).
    Returns (body_mean, body_max, finger_mean, per-group means)"""
    body = []; fing = []; grp = {}
    for i, nm in enumerate(ma.names):
        if not nm or not nm.startswith('mixamorig:') or nm not in mb.rig.idx: continue
        if 'ToeBase' in nm: continue   # raw clips often do not key the toes (rest differs between rigs)
        if skip_legs and any(t in nm for t in ('UpLeg', 'Leg', 'Foot', 'Toe')): continue
        Qa = fa['Rh'].T @ fa['W'][i][0]; j = mb.rig.idx[nm]; Qb = fb['Rh'].T @ fb['W'][j][0]
        a = math.degrees(math.acos(max(-1, min(1, (float(np.trace(Qa.T @ Qb)) - 1) / 2))))
        if any(f in nm for f in FINGER): fing.append(a)
        else:
            body.append(a)
            g = 'arms' if nm.endswith(('LeftArm', 'RightArm')) else 'forearms' if 'ForeArm' in nm else 'UpLeg' if 'UpLeg' in nm else 'neckhead' if nm.endswith(('Neck', 'Head')) else 'body'
            grp.setdefault(g, []).append(a)
    return float(np.mean(body)), float(np.max(body)), (float(np.mean(fing)) if fing else 0.0), {k: float(np.mean(v)) for k, v in grp.items()}


# ---------------------------------------------------------------- skinned mesh clearance
def _raw(j, b, ai):
    a = j['accessors'][ai]; bv = j['bufferViews'][a['bufferView']]
    n = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}[a['type']]
    dt = {5120: 'i1', 5121: 'u1', 5122: '<i2', 5123: '<u2', 5125: '<u4', 5126: '<f4'}[a['componentType']]
    off = bv.get('byteOffset', 0) + a.get('byteOffset', 0); sz = np.dtype(dt).itemsize
    stride = bv.get('byteStride') or n * sz
    if stride == n * sz:
        arr = np.frombuffer(bytes(b[off:off + a['count'] * n * sz]), dtype=dt).reshape(a['count'], n)
    else:
        arr = np.stack([np.frombuffer(bytes(b[off + i * stride: off + i * stride + n * sz]), dtype=dt) for i in range(a['count'])])
    arr = arr.astype(np.float64)
    if a.get('normalized'): arr = arr / {'u1': 255.0, '<u2': 65535.0, 'i1': 127.0, '<i2': 32767.0}[dt]
    return arr


class Mesh:
    """body mesh (the skinned primitive that has the most arm+torso weight) prepared for clearance tests"""
    def __init__(s, rig, j, b, bind_names=None, restSepM=0.0, withHead=False):
        s.rig = rig; skin = j['skins'][0]; s.joints = skin['joints']
        names = [j['nodes'][n].get('name') for n in s.joints]
        best = None
        for m in j['meshes']:
            for p in m['primitives']:
                if 'JOINTS_0' not in p['attributes']: continue
                mat = str(j['materials'][p['material']]['name']) if 'material' in p else ''
                if 'body' in mat.lower(): best = p
        if best is None: raise RuntimeError('no skinned body primitive (material containing "body") in the glb')
        at = best['attributes']; s.pos = _raw(j, b, at['POSITION']); J = _raw(j, b, at['JOINTS_0']).astype(int); Wt = _raw(j, b, at['WEIGHTS_0'])
        if withHead:   # SOBEKNEW v1: a character whose head is its own primitive (material *head*) and is big enough to meet the floor in lying clips: judge it too (data: scaleProfiles.<id>.floorIncludesHead)
            for m_ in j['meshes']:
                for p_ in m_['primitives']:
                    mt_ = str(j['materials'][p_['material']]['name']).lower() if 'material' in p_ else ''
                    if 'JOINTS_0' in p_['attributes'] and 'head' in mt_ and 'body' not in mt_:
                        a_ = p_['attributes']; s.pos = np.concatenate([s.pos, _raw(j, b, a_['POSITION'])]); J = np.concatenate([J, _raw(j, b, a_['JOINTS_0']).astype(int)]); Wt = np.concatenate([Wt, _raw(j, b, a_['WEIGHTS_0'])])
        s.ibm = _raw(j, b, skin['inverseBindMatrices']).reshape(-1, 4, 4).transpose(0, 2, 1)   # glTF is column-major
        dom = J[np.arange(len(J)), np.argmax(Wt, 1)]
        def grp(shorts):
            idx = [i for i, n in enumerate(names) if n and any(n == 'mixamorig:' + x for x in shorts)]
            return np.isin(dom, idx)
        s.sel = {'L': grp(['LeftArm', 'LeftForeArm']), 'R': grp(['RightArm', 'RightForeArm']), 'T': grp(['Hips', 'Spine', 'Spine1', 'Spine2'])}
        # drop the shoulder cap: sleeve verts within 0.10 m of the arm joint at rest
        for sd, bn in (('L', 'LeftArm'), ('R', 'RightArm')):
            jn = s.joints.index(rig.bone(bn)); jp = np.linalg.inv(s.ibm[jn])[:3, 3]
            far = np.linalg.norm(s.pos - jp, axis=1) > 0.10; s.sel[sd] = s.sel[sd] & far
        s.J, s.Wt = J, Wt
        s.idx = {k: np.where(v)[0] for k, v in s.sel.items()}
        if restSepM > 0:   # SOBEKTUNE v1: judge only arm verts that are >= restSepM from the torso at REST. Verts at the arm/torso weight boundary touch the torso at rest (T-pose gap 0.002 m), so they say nothing about a pose.
            Wr = rig.world(None, 0.0); Tr = s.skinned(Wr, s.idx['T']); tr = cKDTree(Tr) if cKDTree else CellTree(Tr, max(0.12, restSepM * 1.5))
            for sd in ('L', 'R'):
                ids = s.idx[sd]; d, _ = tr.query(s.skinned(Wr, ids)); s.idx[sd] = ids[d >= restSepM]

    def skinned(s, W, ids):
        out = np.zeros((len(ids), 3)); Ji = s.J[ids]; Wi = s.Wt[ids]; P = s.pos[ids]
        mats = []
        for k, node in enumerate(s.joints):
            R, p = W[node]; M = np.eye(4); M[:3, :3] = R; M[:3, 3] = p; mats.append(M @ s.ibm[k])
        mats = np.array(mats)
        for c in range(4):
            w = Wi[:, c]
            if not w.any(): continue
            Mk = mats[Ji[:, c]]; v = np.einsum('nij,nj->ni', Mk[:, :3, :3], P) + Mk[:, :3, 3]
            out += w[:, None] * v
        return out

    def gaps(s, W):
        T = s.skinned(W, s.idx['T']); res = {}
        tree = cKDTree(T) if cKDTree else CellTree(T, 0.12)   # SOBEKTUNE v1
        for sd in ('L', 'R'):
            A = s.skinned(W, s.idx[sd])
            if tree is not None: d, _ = tree.query(A)
            else: d = np.array([np.min(np.linalg.norm(T - a, axis=1)) for a in A])
            res[sd] = float(d.min())
        return res


# ---------------------------------------------------------------- sword to head
def sword_clear(M, W, g):
    best = 9.0; sw = g['sword']
    for side in ('Right',):
        hand = M.P(W, side + 'Hand'); fa = M.P(W, side + 'ForeArm'); u = unit(hand - fa)
        a = hand - u * sw['tangBackM']; b_ = hand + u * sw['bladeLengthM']
        head = M.P(W, 'Head'); up = unit(M.P(W, 'Head') - M.P(W, 'Neck')); c = head + up * sw['headCentreUpM']
        ab = b_ - a; t = max(0.0, min(1.0, float(np.dot(c - a, ab) / np.dot(ab, ab)))); d = float(np.linalg.norm(c - (a + ab * t)))
        best = min(best, d - sw['headRadiusM'])
    return best


# ---------------------------------------------------------------- driver
def st_floor(g, st): return st in g.get('floorGate', {}).get('clips', [])   # SOBEKGETUP v1


def clip_metrics(M, mesh, anim, g, n):
    dur = M.rig.duration(anim) if anim in M.rig.anims else 0.0
    ts = np.linspace(0, dur, n); out = {'knee': -9.0, 'toeL0': None, 'toeR0': None, 'toeLm': [], 'toeRm': [], 'neck': -99.0, 'head': -99.0, 'sword': 9.0, 'armL': 9.0, 'armR': 9.0, 'floorMin': 9.0, 'floorMax': -9.0, 'slide': 0.0}
    prev = None; slides = []
    for k, t in enumerate(ts):
        f = M.frame(anim, t); r = M.rest
        out['toeLm'].append(f['toe']['Left']); out['toeRm'].append(f['toe']['Right'])
        if k == 0: out['toeL0'], out['toeR0'] = f['toe']['Left'], f['toe']['Right']
        out['neck'] = max(out['neck'], f['neckPitch'] - r['neckPitch']); out['head'] = max(out['head'], f['headPitch'] - r['headPitch'])
        out['sword'] = min(out['sword'], sword_clear(M, f['W'], g))
        for side in ('Left', 'Right'):
            dy = f['toe'][side + 'Y'] - r['toe'][side + 'Y']; out['floorMin'] = min(out['floorMin'], dy); out['floorMax'] = max(out['floorMax'], dy)
        # SOBEKGETUP v1: lying/rising clips: lowest skinned body-mesh vertex above the rest-pose mesh floor (verts dominated by the excluded bone prefixes, e.g. cloth/tail fk_*, are left out)
        if mesh is not None and st_floor(g, anim) and k % 2 == 0:
            if not hasattr(mesh, '_flIdx'):
                ex = tuple(g['floorGate'].get('excludeBoneContains', [])); nm = [mesh.rig.nodes[n].get('name', '') for n in mesh.joints]
                dom = mesh.J[np.arange(len(mesh.J)), np.argmax(mesh.Wt, 1)]; bad_j = [i_ for i_, n_ in enumerate(nm) if any(e_ in n_ for e_ in ex)]
                mesh._flIdx = np.where(~np.isin(dom, bad_j))[0]; mesh._flG = float(mesh.skinned(M.rest['W'], mesh._flIdx)[:, 1].min())
            out['meshMin'] = min(out.get('meshMin', 9.0), float(mesh.skinned(f['W'], mesh._flIdx)[:, 1].min()) - mesh._flG)
        if mesh is not None and k % 2 == 0:
            gp = mesh.gaps(f['W']); out['armL'] = min(out['armL'], gp['L']); out['armR'] = min(out['armR'], gp['R'])
    return out


def knee_for(j, b, anim_name):
    sk, idx = leg_fix.skel_from(j)
    an = [a for a in j['animations'] if a['name'] == anim_name]
    return knee_medial_anim(sk, j, b, an[0], idx) if an else None


def rig_audit(cfg, proj, outglb):
    """G-rig: every Joan(character) glb must be on the same 106-joint Mixamo v2 skeleton as the body. Skinned glb: skin has exactly rigJoints joints and the same names.
    Animation-only glb (no skin): every one of the body's 106 joint names must exist as a node (the clips are baked on THAT skeleton; 52-bone library rigs fail)."""
    import glob
    ra = cfg['gates']['rigAudit']; jb, bb = L.load(outglb); req = [jb['nodes'][n]['name'] for n in jb['skins'][0]['joints']]
    rows = []; bad = []
    for pat in ra['files']:
        pth = pat if os.path.isabs(pat) else os.path.join(proj, pat)
        for f in sorted(glob.glob(pth)):
            j, b = L.load(f); names = set(n.get('name') for n in j['nodes']); sk = j.get('skins', [])
            if sk:
                got = [j['nodes'][n]['name'] for n in sk[0]['joints']]; ok = len(got) == ra['joints'] and set(got) == set(req); cnt = len(got); kind = 'skinned'
            else:
                cnt = len([r for r in req if r in names]); ok = cnt == ra['joints']; kind = 'anim-only (%d of %d body joints present)' % (cnt, ra['joints'])
            chans = sorted(set(len(a['channels']) for a in j.get('animations', [])))
            rows.append({'file': f, 'joints': cnt, 'kind': kind, 'is106': ok, 'channelsPerClip': chans[:6]})
            if not ok: bad.append('G-rig %s has %d/%d joints (%s)' % (os.path.relpath(f, proj) if f.startswith(proj) else f, cnt, ra['joints'], kind))
    return rows, bad


def _char_rows(cfg, cid):
    """MONSTER3 v1: the character's rows = base with its replaceRows swapped in + extraRows (same rule as clip_build.char_rows). Key absent = base."""
    spec = cfg.get('characters', {}).get(cid, {}); rep = spec.get('replaceRows', {})
    return [rep.get(r['state'], r) for r in cfg['base']] + spec.get('extraRows', [])


def run(cfg, roster, reps, proj, outdir, live_override=None):
    g = cfg['gates']; allbad = []; summary = {}
    for rep in reps:
        cid = rep['character']; rc = [c for c in roster['characters'] if c['id'] == cid][0]
        sp = g.get('scaleProfiles', {}).get(cid, {}); sfoot = float(sp.get('footLen', 1.0)); sarm = float(sp.get('meshClearance', 1.0)); ffm = g['footFloorMaxM'] * sfoot; acm = g['armClearanceMeshMinM'] * sarm   # SOBEKTUNE v1: own-scale limits (data)
        if sp: print('  %s scaleProfile foot x%.2f (floor limit %.3f m) meshClearance x%.2f (arm gap limit %.4f m)' % (cid, sfoot, ffm, sarm, acm))
        wv = g.get('waiver', {}).get(cid, {}); pm = wv.get('poseMeanDeg', g['poseMeanDeg']); px = wv.get('poseMaxDeg', g['poseMaxDeg'])
        jn, bn = L.load(rep['output']); rn = L.Rig(jn, bn); Mn = Metrics(rn); meshn = Mesh(rn, jn, bn, restSepM=float(sp.get('armRestSeparationM', 0.0)), withHead=bool(sp.get('floorIncludesHead', False)))
        livep = live_override or os.path.join(proj, rc['modelGlb'])
        jl, bl = L.load(livep); rl = L.Rig(jl, bl); Ml = Metrics(rl); meshl = Mesh(rl, jl, bl)
        corr = L.qmul(np.array(jn['nodes'][rn.idx['mixamorig:Hips']]['rotation']), L.qinv(np.array([-math.sqrt(.5), 0, 0, math.sqrt(.5)])))
        rows = []; bad = []
        for r in rep['clips']:
            st = r['state']; n = g['samplesPerClip']
            mn = clip_metrics(Mn, meshn, st, g, n); ml = clip_metrics(Ml, meshl, st, g, n)
            mn['knee'] = knee_for(jn, bn, st); ml['knee'] = knee_for(jl, bl, st)
            # pose vs raw (or vs ref for derived rows)
            refspec = None
            row_cfg = [x for x in _char_rows(cfg, cid) if x['state'] == st][0]  # MONSTER3 v1: own replaceRows / extraRows
            if 'src' in row_cfg and not row_cfg.get('foreign'): refspec = row_cfg['src']
            elif row_cfg.get('ref'): refspec = row_cfg['ref']
            pose = None; poseL = None
            if refspec:
                p = os.path.join(cfg['sources'][refspec.split(':')[0]], refspec.split(':')[1] + '.glb'); jr, br = L.load(p); rr = L.Rig(jr, br); Mr = Metrics(rr); an = list(rr.anims)[0]
                dur = min(rn.duration(st), rr.duration(an)); vals = []; valsL = []; yawr = []; fing = []
                for t in np.linspace(0, dur, n):
                    fa = Mn.frame(st, t); fr = Mr.frame(an, t); fl = Ml.frame(st, t) if st in rl.anims else None
                    vals.append(pose_diff(Mn, fa, Mr, fr, r.get('legFix') == 'applied')); yawr.append((fr['toe']['Left'], fr['toe']['Right']))
                    if fl is not None: valsL.append(pose_diff(Ml, fl, Mr, fr, r.get('legFix') == 'applied'))
                pose = {'mean': float(np.mean([v[0] for v in vals])), 'max': float(np.max([v[1] for v in vals])), 'finger': float(np.mean([v[2] for v in vals])),
                        'groups': {k: float(np.mean([v[3].get(k, 0) for v in vals])) for k in vals[0][3]}}
                if valsL: poseL = {'mean': float(np.mean([v[0] for v in valsL])), 'max': float(np.max([v[1] for v in valsL])), 'finger': float(np.mean([v[2] for v in valsL])),
                                   'groups': {k: float(np.mean([v[3].get(k, 0) for v in valsL])) for k in valsL[0][3]}}
                mn['rawToeL'] = float(np.mean([y[0] for y in yawr])); mn['rawToeR'] = float(np.mean([y[1] for y in yawr]))
            rowd = {'state': st, 'new': mn, 'live': ml, 'pose': pose, 'poseLive': poseL, 'legFix': r.get('legFix'), 'swivel': r.get('legFixMaxSwivelDeg', 0.0)}
            exc = g.get('exceptClips', {}).get(st)
            # gates
            tp = cfg.get('tuning', {}).get(rep.get('tuningProfile') or '', {})
            tol = abs(float(tp.get('clipAbductDeg', {}).get(st, tp.get('armAbductDeg', 0.0)))) if st in tp.get('clips', []) else 0.0   # SOBEKTUNE v1: per-clip abduction     # the character's OWN declared tuning is allowed in the pose gate, nothing else
            au_ = wv.get('authored', {}).get(st, {}); pm_s = float(au_.get('poseMeanDeg', pm)); px_s = float(au_.get('poseMaxDeg', px))   # MONSTERPOLISH v1: an AUTHORED clip edit (head pitch limit, pose blend) declared per state in data gates.waiver.<id>.authored, shown in the report
            if au_: print('  G-pose %-18s authored waiver: limits %.1f/%.1f (%s)' % (st, pm_s, px_s, au_.get('why', '')))
            if pose and (pose['mean'] > pm_s + tol or pose['max'] > px_s + tol): bad.append('G-pose %s mean %.2f max %.2f (limits %.1f/%.1f)' % (st, pose['mean'], pose['max'], pm_s, px_s))
            if st in rep.get('derivedRefLimit', {}): pass
            kmax_ = float(wv.get('kneeMedialMaxM', g['kneeMedialMaxM']))   # SOBEKNEW v1: per-character waiver value (data gates.waiver.<id>), absent = old behaviour
            if mn['knee'] is not None and mn['knee'] > kmax_: bad.append('G-knee %s inward %.3f m > %.3f' % (st, mn['knee'], kmax_))
            if rowd['swivel'] > g['swivelFailDeg'] and st in g['footPlantClips']: bad.append('G-swivel %s leg fix needed %.1f deg > %.0f (source suspect)' % (st, rowd['swivel'], g['swivelFailDeg']))
            if 'rawToeL' in mn and not exc and st in g['toeGateClips']:
                dl = abs(float(np.mean(mn['toeLm'])) - mn['rawToeL']); dr = abs(float(np.mean(mn['toeRm'])) - mn['rawToeR'])
                if dl > g['toeYawTolDeg'] or dr > g['toeYawTolDeg']: bad.append('G-toe %s yaw L %.1f/R %.1f deviates from raw %.1f/%.1f by %.1f/%.1f (> %.0f)' % (st, np.mean(mn['toeLm']), np.mean(mn['toeRm']), mn['rawToeL'], mn['rawToeR'], dl, dr, g['toeYawTolDeg']))
            if st in g['footPlantClips'] and (mn['floorMin'] < -ffm or mn['floorMax'] > ffm + 0.2 * sfoot): bad.append('G-plant %s toe height range %.3f..%.3f m vs rest' % (st, mn['floorMin'], mn['floorMax']))
            fg = g.get('floorGate', {})   # SOBEKGETUP v1: hips / toes / hands never go under the floor in lying and rising clips; limits are Joan-scale x the factor of the body part judged (scaleProfiles: hipsHeight, footLen, armLen)
            if st in fg.get('clips', []):
                ml_ = fg['meshSinkMaxM'] * float(sp.get('hipsHeight', 1.0)); tl = fg['toeSinkMaxM'] * sfoot
                if st in ('special_skullcrack', 'special_whirlwind'): ml_ = max(ml_, 0.10); tl = max(tl, 0.10)   # hotship v109hg: per-row exemption for the two old finisher-adjacent rows only (Lead 16:02)
                if mn.get('meshMin', 0.0) < -ml_: bad.append('G-floor %s body mesh sinks %.3f m under the floor > %.3f' % (st, -mn['meshMin'], ml_))
                if mn['floorMin'] < -tl: bad.append('G-floor %s toe sinks %.3f m > %.3f' % (st, -mn['floorMin'], tl))
                print('  G-floor %-12s meshSink %.3f (<=%.3f) toeSink %.3f (<=%.3f)' % (st, max(0, -mn.get('meshMin', 0.0)), ml_, max(0, -mn['floorMin']), tl))
            acm_ = float(wv.get('armClearanceMeshMinM', acm))   # SOBEKNEW v1
            if mn['armL'] < 9 and min(mn['armL'], mn['armR']) < acm_ and st in g['footPlantClips']: bad.append('G-arm %s sleeve-torso gap %.3f/%.3f m < %.4f' % (st, mn['armL'], mn['armR'], acm_))
            nx = g.get('neckExempt', {}).get(st)
            if mn['head'] > (55.0 if st.startswith('hit_') else g['headExtensionMaxDeg']) and not nx: bad.append('G-neck %s head extension %.1f deg > %.0f' % (st, mn['head'], (55.0 if st.startswith('hit_') else g['headExtensionMaxDeg'])))   # dev2: hit reactions 0.3-0.5 s, transient lean allowed to 55 deg (Lead 14:30)
            if mn['neck'] > (55.0 if st.startswith('hit_') else g['neckExtensionMaxDeg']) and not nx: bad.append('G-neck %s neck extension %.1f deg > %.0f' % (st, mn['neck'], (55.0 if st.startswith('hit_') else g['neckExtensionMaxDeg'])))
            if mn['sword'] < float(wv.get('swordMinClearanceM', g['sword']['minClearanceM'])): bad.append('G-sword %s blade segment %.3f m inside the head sphere' % (st, mn['sword']))   # SOBEKNEW v1
            rows.append(rowd)
            print('  %-20s pose %s knee %+.3f(live %+.3f) toe %+5.1f/%+5.1f (live %+5.1f/%+5.1f) neck %5.1f head %5.1f(live %5.1f) sword %+.2f(live %+.2f) arm %.3f/%.3f(live %.3f/%.3f)' % (
                st, ('%.1f/%.1f' % (pose['mean'], pose['max'])) if pose else '  n/a  ', mn['knee'], ml['knee'], mn['toeL0'], mn['toeR0'], ml['toeL0'], ml['toeR0'], mn['neck'], mn['head'], ml['head'], mn['sword'], ml['sword'],
                mn['armL'], mn['armR'], ml['armL'], ml['armR']))
        rr_rows = []
        if cid in cfg['gates']['rigAudit'].get('characters', []):
            rr_rows, rb = rig_audit(cfg, proj, rep['output']); bad += rb
            for x in rr_rows: print('  RIG %-4s %s  %s' % ('106' if x['is106'] else 'NOT', x['file'].replace(proj, '.'), x['kind']))
        gj = os.path.join(outdir, '%s_gates.json' % cid)
        json.dump({'character': cid, 'failed': bad, 'rig': rr_rows, 'rows': rows}, open(gj, 'w'), indent=1, default=lambda o: float(o) if hasattr(o, '__float__') else str(o))
        print('%s gates: %d failed -> %s' % (cid, len(bad), gj))
        for x in bad: print('  FAIL', x)
        allbad += ['%s: %s' % (cid, x) for x in bad]
    return allbad


if __name__ == '__main__':
    import argparse
    ap = argparse.ArgumentParser(); ap.add_argument('glb'); ap.add_argument('char'); ap.add_argument('--live'); ap.add_argument('--proj')
    a = ap.parse_args()
    here = os.path.dirname(os.path.abspath(__file__)); root = os.path.dirname(here)
    import clip_build
    proj = a.proj or clip_build.find_proj(root); cfg = clip_build.read_json(os.path.join(root, 'Assets', 'Data', 'clip_build.json')); roster = clip_build.read_json(os.path.join(proj, 'Assets', 'Data', 'roster.json'))
    rep = {'character': a.char, 'output': a.glb, 'clips': [{'state': r['state']} for r in cfg['base']]}
    bad = run(cfg, roster, [rep], proj, os.path.dirname(os.path.abspath(a.glb)), a.live)
    sys.exit(3 if bad else 0)

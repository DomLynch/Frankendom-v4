"""CLIPBUILD clip_build.py: roster-driven character clip build from the RAW Mixamo originals (HC2 proposal steps 1-3, base set).

  python clip_build.py --roster                 build every roster id (player + enemy) that has a model glb
  python clip_build.py --char Joan [--out DIR]  one character
  python clip_build.py --char Joan --check      validate config + sources only (no output written), exit 3 on error
  python clip_build.py --roster --gates         build, then run the offline gates (clip_gates.py), exit 3 on a failed gate

What it does (base set; attacks / dodges / finishers stay with bake_attack_clips.py / bake_dodge_clips.py / FinisherClipSetup until merged, see MANIFEST):
  1. roster.json -> ids (player, enemy, extra 'alwaysBuild'); modelGlb is the body. Its own animations are dropped and the buffer compacted.
  2. clip_build.json rows: every state comes from a RAW Mixamo library clip (sources.lib = _work/lib/<id>.glb), unmodified, unless
       - row.derive = a documented world-rotation recipe or frame-range/hold recipe on the RAW clip (data, not prose), or
       - row.foreign = an explicit exception (clip with no Mixamo original). A source path that names another roster character is an error unless row.foreign is set.
  3. a per-character tuning profile (characters.<id>.tuning). A profile is only legal for its owner: using another character's profile is a build error.
     Joan's profile has zero offsets (= raw Mixamo) unless a clearance value was measured and written into clip_build.json.
  4. Hips: Rx corr = rig hips rotation * inverse(Mixamo clip hips rotation), hips translation scaled by rig_z/clip_z and held in place (same maths as build_joan_combat.py).
  5. leg fix (clip_build.json tools.legFix): ONE shared leg_fix.py; mode auto = only clips whose knee-medial offset exceeds the trigger are touched, so raw clips stay raw.
Writes <outputDir>/<Id>_combat.glb (+ <Id>_anim.glb copy) and <outputDir>/clip_build_report.json. Never writes into Assets/ unless outputDir says so. numpy only, CPU only.
"""
import argparse, glob, hashlib, json, math, os, shutil, sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__)); ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import clip_lib as L
import leg_fix

TOOL_VERSION = 'clipbuild-1'


def fail(msg):
    print('CLIPBUILD ERROR:', msg); sys.exit(3)


def find_proj(start):
    d = start
    for _ in range(8):
        if os.path.exists(os.path.join(d, 'Assets', 'Data', 'roster.json')): return d
        d = os.path.dirname(d)
    return None


def read_json(p):
    return json.load(open(p, encoding='utf-8-sig'))


def m2q(m):
    return leg_fix.m2q(m)


def rotm(axis, deg):
    return L.q2m(L.qaxis(axis, deg))


# ---------------------------------------------------------------- glb helpers
def compact_without_animations(j, b):
    """drop all animations and every bufferView only they used; returns new (j, b)"""
    j = json.loads(json.dumps(j)); j['animations'] = []
    j.get('asset', {}).get('extras', {}).pop('clipBuild', None)     # our own stamp is not part of the body
    used = set()
    for m in j.get('meshes', []):
        for p in m['primitives']:
            used.update(p['attributes'].values())
            if 'indices' in p: used.add(p['indices'])
            for t in p.get('targets', []): used.update(t.values())
    for s in j.get('skins', []):
        if 'inverseBindMatrices' in s: used.add(s['inverseBindMatrices'])
    order = sorted(used); amap = {o: i for i, o in enumerate(order)}
    new_acc = [j['accessors'][o] for o in order]
    bvs = set(a['bufferView'] for a in new_acc if 'bufferView' in a)
    for a in new_acc:
        sp = a.get('sparse')
        if sp: bvs.add(sp['indices']['bufferView']); bvs.add(sp['values']['bufferView'])
    for im in j.get('images', []):
        if 'bufferView' in im: bvs.add(im['bufferView'])
    border = sorted(bvs); bmap = {o: i for i, o in enumerate(border)}
    nb = bytearray(); new_bv = []
    for o in border:
        bv = dict(j['bufferViews'][o])
        while len(nb) % 4: nb.append(0)
        src = bytes(b[bv.get('byteOffset', 0):bv.get('byteOffset', 0) + bv['byteLength']])
        bv['byteOffset'] = len(nb); bv['buffer'] = 0; nb.extend(src); new_bv.append(bv)
    for a in new_acc:
        if 'bufferView' in a: a['bufferView'] = bmap[a['bufferView']]
        sp = a.get('sparse')
        if sp: sp['indices']['bufferView'] = bmap[sp['indices']['bufferView']]; sp['values']['bufferView'] = bmap[sp['values']['bufferView']]
    for im in j.get('images', []):
        if 'bufferView' in im: im['bufferView'] = bmap[im['bufferView']]
    for m in j.get('meshes', []):
        for p in m['primitives']:
            p['attributes'] = {k: amap[v] for k, v in p['attributes'].items()}
            if 'indices' in p: p['indices'] = amap[p['indices']]
            if 'targets' in p: p['targets'] = [{k: amap[v] for k, v in t.items()} for t in p['targets']]
    for s in j.get('skins', []):
        if 'inverseBindMatrices' in s: s['inverseBindMatrices'] = amap[s['inverseBindMatrices']]
    j['accessors'] = new_acc; j['bufferViews'] = new_bv
    return j, nb


def add_acc(j, b, arr, typ, minmax=False):
    arr = np.asarray(arr, '<f4')
    while len(b) % 4: b.append(0)
    o = len(b); raw = arr.tobytes(); b.extend(raw)
    j['bufferViews'].append({'buffer': 0, 'byteOffset': o, 'byteLength': len(raw)})
    a = {'bufferView': len(j['bufferViews']) - 1, 'componentType': 5126, 'count': int(arr.shape[0]), 'type': typ}
    if minmax: a['min'] = [float(arr.min())]; a['max'] = [float(arr.max())]
    j['accessors'].append(a); return len(j['accessors']) - 1


# ---------------------------------------------------------------- clip data (raw source -> per-bone tracks)
class Clip:
    """bones: name -> {'rotation': (N,4), 'translation': (N,3) (Hips only)}, times (N,), plus the source glb's own rig for FK"""
    def __init__(s, path):
        s.path = path; s.j, s.b = L.load(path); s.rig = L.Rig(s.j, s.b)
        a = s.j['animations'][0]; s.rig_anim = list(s.rig.anims)[0]
        ch = s.rig.anims[s.rig_anim]
        hips = s.rig.bone('Hips'); s.times = ch[(hips, 'rotation')][0]; N = len(s.times)
        s.rot = {}; s.hips_t = None
        for (n, p), (ts, vs) in ch.items():
            nm = s.rig.nodes[n]['name']
            if p == 'rotation':
                if len(ts) != N or not np.allclose(ts, s.times, atol=1e-5):   # resample onto the Hips times
                    vs = np.array([L.sample_q(ts, vs, t) for t in s.times])
                s.rot[nm] = vs.copy()
            elif p == 'translation' and nm == 'mixamorig:Hips':
                s.hips_t = vs.copy() if len(ts) == N else np.array([L.sample_v(ts, vs, t) for t in s.times])
        s.hips_rest_z = abs(s.rig.rt[hips][2])

    def world_rots(s, i):
        """world rotation matrices of every node at frame i with the CURRENT s.rot"""
        W = {}
        for n in s.rig.order:
            nm = s.rig.nodes[n].get('name'); q = s.rot[nm][i] if nm in s.rot else s.rig.rq[n]
            R = L.q2m(q); p = s.rig.parent.get(n)
            W[n] = R if p is None else W[p] @ R
        return W

    def world_rot_op(s, bone, axis, deg):
        """left-multiply the bone's WORLD rotation by an axis-angle (world axis) on every frame; children follow"""
        D = rotm(axis, deg); n = s.rig.idx['mixamorig:' + bone]; p = s.rig.parent[n]; nm = 'mixamorig:' + bone
        for i in range(len(s.times)):
            Pw = s.world_rots(i)[p]
            local = L.q2m(s.rot[nm][i]); new = Pw.T @ D @ Pw @ local
            q = m2q(new)
            if np.dot(q, s.rot[nm][i]) < 0: q = -q
            s.rot[nm][i] = q

    def subrange(s, f0, f1):
        s.rot = {k: v[f0:f1 + 1].copy() for k, v in s.rot.items()}
        if s.hips_t is not None: s.hips_t = s.hips_t[f0:f1 + 1].copy()
        s.times = s.times[f0:f1 + 1] - s.times[f0]


def hold_clip(src, fa, fb, period, fps):
    """new Clip-like object: cosine ease between the poses at frames fa and fb of src and back, `period` seconds, sampled at fps (closing frame == frame 0)"""
    n = int(round(period * fps)) + 1; t = np.arange(n) / fps
    k = 0.5 - 0.5 * np.cos(2 * math.pi * t / period)
    out = {}
    for nm, v in src.rot.items():
        out[nm] = np.array([L.slerp(v[fa], v[fb], kk) for kk in k])
    src.rot = out; src.times = t
    if src.hips_t is not None: src.hips_t = np.array([src.hips_t[fa] * (1 - kk) + src.hips_t[fb] * kk for kk in k])
    return src


# ---------------------------------------------------------------- config
PACK_KEY = 'enemyPack'   # HCKIT6 v100: source-key role name (rows say "enemyPack:<file>"; the legacy "pitbornPack:" prefix is still accepted)


def pack_hook(cfg):   # HCKIT6 v100
    return cfg.get('enemyPackHook') or cfg.get('pitbornPackHook') or {}


def is_pack_src(spec):   # HCKIT6 v100
    return spec.startswith(PACK_KEY + ':') or spec.startswith('pitbornPack:')


def tuning_owner(cfg, roster, name):   # HCKIT6 v100: owner may be a role token (@player / @enemy) or a roster id
    o = cfg.get('tuning', {}).get(name, {}).get('owner')
    return roster.get({'@player': 'player', '@enemy': 'enemy'}[o], o) if o in ('@player', '@enemy') else o


def resolve_src(cfg, spec):
    if ':' not in spec: fail('bad src "%s" (expected key:file)' % spec)
    key, name = spec.split(':', 1)
    if key == 'pitbornPack': key = PACK_KEY   # HCKIT6 v100: legacy prefix
    srcs = dict(cfg['sources']); hk = pack_hook(cfg)   # HCKIT6 v100: role-named hook (enemyPackHook / sources.enemyPack); the old pitborn* keys still read
    if hk.get('enabled'): srcs[PACK_KEY] = hk.get('sources.enemyPack', hk.get('sources.pitbornPack'))   # HCKIT6 v100
    if key not in srcs: fail('unknown source key %s (%s needs enemyPackHook.enabled)' % (key, PACK_KEY))
    p = os.path.join(srcs[key], name if name.lower().endswith('.glb') else name + '.glb')
    return os.path.normpath(p)


def check_config(cfg, roster, ids):
    errs = []
    owners = {k: tuning_owner(cfg, roster, k) for k in cfg.get('tuning', {})}   # HCKIT6 v100
    foreign_tokens = [t.lower() for t in cfg.get('foreignTokens', [])] + [c['id'].lower() for c in roster['characters']]
    for cid in ids:
        spec = cfg['characters'].get(cid, {})
        tun = spec.get('tuning')
        if tun is not None:
            if tun not in owners: errs.append('%s: unknown tuning profile %s' % (cid, tun))
            elif owners[tun] != cid: errs.append('%s: tuning profile "%s" belongs to %s (refused: another character\'s profile)' % (cid, tun, owners[tun]))
        for row in cfg['base'] + spec.get('extraRows', []):
            if is_pack_src(row.get('src', '')) and cid not in pack_hook(cfg).get('allowedFor', []): errs.append('%s/%s: source pack %s is only for %s' % (cid, row['state'], PACK_KEY, pack_hook(cfg).get('allowedFor'))); continue   # HCKIT6 v100
            srcs = [row['src']] if 'src' in row else []
            if 'derive' in row and 'from' in row['derive']: srcs.append(row['derive']['from'])
            for s in srcs:
                p = resolve_src(cfg, s)
                if not os.path.exists(p): errs.append('%s/%s: source missing %s' % (cid, row['state'], p)); continue
                low = p.lower().replace('/', '\\')
                other = [t for t in foreign_tokens if t != cid.lower() and t in low]
                if other and not row.get('foreign'):
                    errs.append('%s/%s: source path names another character (%s): %s (set row.foreign with a reason to allow)' % (cid, row['state'], ','.join(other), p))
            if row.get('foreign') and not str(row['foreign']).strip(): errs.append('%s/%s: empty foreign reason' % (cid, row['state']))
    return errs


# ---------------------------------------------------------------- build
def build_clip(cfg, row, rig_z, corr, tuning, cid, cache):
    def get(spec):
        p = resolve_src(cfg, spec)
        if p not in cache: cache[p] = Clip(p)
        c = cache[p]
        c2 = Clip.__new__(Clip); c2.__dict__.update(c.__dict__); c2.rot = {k: v.copy() for k, v in c.rot.items()}
        c2.hips_t = None if c.hips_t is None else c.hips_t.copy(); c2.times = c.times.copy(); return c2
    notes = []
    if 'derive' in row:
        d = row['derive']; c = get(d['from'])
        if 'frames' in d: c.subrange(d['frames'][0], d['frames'][1]); notes.append('frames %d-%d of %s' % (d['frames'][0], d['frames'][1], d['from']))
        if 'hold' in d:
            h = d['hold']; hold_clip(c, h['frameA'], h['frameB'], h['periodS'], h['fps']); notes.append('hold ease frames %d<->%d' % (h['frameA'], h['frameB']))
        for op in d.get('worldRot', []):
            c.world_rot_op(op['bone'], op['axis'], op['deg']); notes.append('worldRot %s %s %+g' % (op['bone'], op['axis'], op['deg']))
        src_desc = d['from']
    else:
        c = get(row['src']); src_desc = row['src']
    # tuning profile (Joan's = clearance only, zero by default)
    if tuning is not None and row['state'] in tuning.get('clips', []):
        ab = float(tuning.get('clipAbductDeg', {}).get(row['state'], tuning.get('armAbductDeg', 0.0)))   # SOBEKTUNE v1: per-clip override (data), default = armAbductDeg
        if ab != 0.0:
            fw = tuning.get('clipAxis', {}).get(row['state'], tuning['forwardAxis'])   # SOBEKTUNE v1: per-clip axis (data)
            c.world_rot_op('LeftArm', fw, +ab); c.world_rot_op('RightArm', fw, -ab); notes.append('tuning armAbduct %+g deg' % ab)
    # PITNEW v1: hipsRollScale[state] = k scales the pelvis roll (about the model forward axis) toward the rest pose; absent = unchanged
    hrs = (tuning or {}).get('hipsRollScale', {}).get(row['state'])
    if hrs is not None and float(hrs) != 1.0:
        qm = np.array([-math.sqrt(.5), 0, 0, math.sqrt(.5)]); ax_ = np.array((tuning or {}).get('rollAxis', [0, 0, 1]), float)
        w_ = L.qmul(c.rot['mixamorig:Hips'], L.qinv(qm)); ang = 2 * np.arctan2(w_[:, :3] @ ax_, w_[:, 3])     # twist angle about the axis (N,)
        tw = np.concatenate([ax_[None, :] * np.sin(ang / 2)[:, None], np.cos(ang / 2)[:, None]], 1)
        sw = L.qmul(w_, L.qinv(tw)); tw2 = np.concatenate([ax_[None, :] * np.sin(float(hrs) * ang / 2)[:, None], np.cos(float(hrs) * ang / 2)[:, None]], 1)
        c.rot['mixamorig:Hips'] = L.qmul(L.qmul(sw, tw2), qm); notes.append('hipsRollScale %.2f (roll %.1f..%.1f deg)' % (float(hrs), float(np.degrees(ang).min()), float(np.degrees(ang).max())))
    # hips: rig correction + in place + height ratio (same maths as build_joan_combat.py)
    ratio = min(3.0, max(0.33, rig_z / c.hips_rest_z))
    c.rot['mixamorig:Hips'] = L.qmul(corr, c.rot['mixamorig:Hips'])
    v = np.array([L.qrot(corr, x * ratio) for x in c.hips_t]); v[:, 0] = v[0, 0]; v[:, 1] = v[0, 1]
    # SOBEKGETUP v1: per-clip hips lift over time (data tuning.hipsLiftM[state] = [[t_s, metres], ...], linear; 0 where the body stands, non-zero while lying/rising) for a body whose limbs reach below the floor in lying/rising clips; rig up axis = hips z (same axis clip_sets hipsYOffsetM uses)
    lift = (tuning or {}).get('hipsLiftM', {}).get(row['state'])
    if lift:
        lt = np.array([k[0] for k in lift], float); lv = np.array([k[1] for k in lift], float)
        v[:, 2] += np.array([L.sample_v(lt, lv, float(t)) for t in c.times]); notes.append('hipsLift max %.3f m' % float(lv.max()))
    c.hips_t = v
    return c, src_desc, notes, ratio


# ---------------------------------------------------------------- MONSTERPOLISH v1: head pitch limit (data, per clip)
def head_pitch_limit(rig, rot, times, spec, state):
    """spec (data, tuning.<profile>.headPitch): {"axisWorldRest": [x,y,z] snout axis in the model frame at rest, "bones": {"Neck": w, "Head": w} (weights sum 1, parents first),
    "smoothS": s, "clips": {state: {"minDeg": d}}}. For a state listed in spec.clips the head axis pitch (deg above horizontal, y up) is raised to minDeg on every frame it is below it:
    the shortfall is dilated and smoothed in time (no pops), then spread over the listed bones as a world rotation about the horizontal axis normal to the head heading.
    Returns a report dict, or None when the state has no row (clip untouched = old behaviour). The rig is the body rest rig (names, rest rotations)."""
    row = (spec.get('clips') or {}).get(state)
    if not row: return None
    lim = float(row['minDeg']) if 'minDeg' in row else 0.0; bones = spec['bones']; names = ['mixamorig:' + k for k in bones]
    head = 'mixamorig:Head'; ax = np.array(spec['axisWorldRest'], float); ax /= np.linalg.norm(ax)
    chain = []; i = rig.idx[head]
    while i is not None: chain.append(i); i = rig.parent.get(i)
    chain = chain[::-1]; cn = [rig.nodes[k].get('name') for k in chain]
    def wrot(f, upto=None, rotd=None):
        R = np.eye(3); out = {}
        for k, nm in zip(chain, cn):
            q = (rotd or rot)[nm][f] if nm in (rotd or rot) else rig.rq[k]
            R = R @ L.q2m(q); out[nm] = R
            if nm == upto: break
        return out
    Rrest = np.eye(3)
    for k in chain: Rrest = Rrest @ L.q2m(rig.rq[k])
    a_loc = Rrest.T @ ax
    N = len(times); pitch = np.zeros(N); K = []
    last_k = None
    for f in range(N):
        a = wrot(f)[head] @ a_loc; pitch[f] = math.degrees(math.asin(max(-1.0, min(1.0, a[1]))))
        h = np.array([a[0], 0.0, a[2]]); hn = np.linalg.norm(h)
        if hn > 0.25: last_k = np.cross(h / hn, [0.0, 1.0, 0.0])
        K.append(last_k if last_k is not None else np.cross(np.array([ax[0], 0, ax[2]]) / max(1e-6, np.hypot(ax[0], ax[2])), [0.0, 1.0, 0.0]))
    if row.get('minDegKeys'):     # [[t_s, deg], ...] linear: a limit that changes over the clip (data)
        kk = row['minDegKeys']; limv = np.interp(times, [k[0] for k in kk], [k[1] for k in kk]); lim = float(limv.min())
    else: limv = np.full(N, lim)
    need = np.maximum(0.0, limv - pitch)
    dt = float(np.median(np.diff(times))) if N > 1 else 1.0; sm = float(spec.get('smoothS', 0.12)); wn = max(1, int(round(sm / dt)))
    pad = np.pad(need, wn, mode='edge'); dil = np.array([pad[i:i + 2 * wn + 1].max() for i in range(N)])
    g = np.exp(-0.5 * (np.arange(-3 * wn, 3 * wn + 1) / max(1.0, wn)) ** 2); g /= g.sum(); d = np.convolve(np.pad(dil, 3 * wn, mode='edge'), g, mode='valid')[:N]
    d = np.maximum(d, 0.0)
    if d.max() < 0.05: return {'state': state, 'minDeg': lim, 'pitchMinBefore': round(float(pitch.min()), 1), 'pitchMinAfter': round(float(pitch.min()), 1), 'maxCorrDeg': 0.0}
    new = {nm: rot[nm].copy() for nm in names}
    for f in range(N):
        if d[f] < 1e-4: continue
        k = K[f] / max(1e-9, np.linalg.norm(K[f]))
        for nm, w in zip(names, bones.values()):
            ci = cn.index(nm); Pw = np.eye(3)
            for kk, n2 in zip(chain[:ci], cn[:ci]): Pw = Pw @ L.q2m(new[n2][f] if n2 in new else (rot[n2][f] if n2 in rot else rig.rq[kk]))
            D = rotm(k, float(d[f]) * float(w)); loc = L.q2m(new[nm][f]); q = m2q(Pw.T @ D @ Pw @ loc)
            if np.dot(q, new[nm][f]) < 0: q = -q
            new[nm][f] = q
    after = np.zeros(N)
    for f in range(N):
        R = np.eye(3)
        for kk, nm in zip(chain, cn): R = R @ L.q2m(new[nm][f] if nm in new else rot[nm][f] if nm in rot else rig.rq[kk])
        a = R @ a_loc; after[f] = math.degrees(math.asin(max(-1.0, min(1.0, a[1]))))
    for nm in names: rot[nm] = new[nm]
    return {'state': state, 'minDeg': lim, 'pitchMinBefore': round(float(pitch.min()), 1), 'pitchMinAfter': round(float(after.min()), 1), 'maxCorrDeg': round(float(d.max()), 1)}


def pose_blend(clips, spec):
    """MONSTERPOLISH v1: data tuning.<profile>.poseBlend = {state: {"toState": s2, "toTimeS": t, "t0": a, "t1": b, "power": p, "liftM": [[t, m], ...]}}.
    From t0 to t1 (clip seconds) every rotation of `state` is slerped from its own pose toward the pose of clip s2 at toTimeS, weight w = ((t-t0)/(t1-t0))^power, then held at 1: a collapse onto the lying pose
    instead of the library fall. Hips translation is blended the same way. liftM is an extra hips lift (m, rig up axis = z) added after the blend (solved so no vertex is under the floor).
    Duration, frame count and times of `state` are unchanged. Returns {state: report}."""
    by = dict(clips); rep = {}
    for st, row in spec.items():
        if st not in by or row['toState'] not in by: continue
        c = by[st]; tg = by[row['toState']]; tt = float(row.get('toTimeS', 0.0)); t0, t1, p = float(row['t0']), float(row['t1']), float(row.get('power', 1.0))
        u = np.clip((c.times - t0) / max(1e-6, t1 - t0), 0.0, 1.0); w = u ** p
        if row.get('smooth'): w = w * w * (3 - 2 * w)
        for nm in c.rot:
            if nm not in tg.rot: continue
            q1 = np.asarray(L.sample_q(tg.times, tg.rot[nm], tt), float); q1 = q1 / np.linalg.norm(q1)
            for i in range(len(c.times)):
                if w[i] > 0: c.rot[nm][i] = L.slerp(c.rot[nm][i], q1, float(w[i]))
        h1 = np.asarray(L.sample_v(tg.times, tg.hips_t, tt), float)
        for i in range(len(c.times)): c.hips_t[i] = c.hips_t[i] * (1 - w[i]) + h1 * w[i]
        lf = row.get('liftM')
        if lf:
            lt = np.array([k[0] for k in lf], float); lv = np.array([k[1] for k in lf], float)
            c.hips_t[:, 2] += np.array([L.sample_v(lt, lv, float(t)) for t in c.times])
        rep[st] = {'toState': row['toState'], 't0': t0, 't1': t1, 'power': p, 'liftMax': round(float(max([k[1] for k in lf])), 3) if lf else 0.0}
    return rep


def write_clips(j, b, name2idx, clips):
    j['animations'] = []
    for state, c in clips:
        ti = add_acc(j, b, c.times.astype('<f4'), 'SCALAR', True)
        anim = {'name': state, 'samplers': [], 'channels': []}
        for nm in sorted(c.rot):
            if nm not in name2idx: continue
            anim['samplers'].append({'input': ti, 'output': add_acc(j, b, c.rot[nm], 'VEC4'), 'interpolation': 'LINEAR'})
            anim['channels'].append({'sampler': len(anim['samplers']) - 1, 'target': {'node': name2idx[nm], 'path': 'rotation'}})
        anim['samplers'].append({'input': ti, 'output': add_acc(j, b, c.hips_t, 'VEC3'), 'interpolation': 'LINEAR'})
        anim['channels'].append({'sampler': len(anim['samplers']) - 1, 'target': {'node': name2idx['mixamorig:Hips'], 'path': 'translation'}})
        j['animations'].append(anim)


def knee_medial_max(sk, j, b, anim, name2idx):
    """max over frames of the inward knee offset (m, + = knock-kneed), same quantity leg_fix.fix_frame clamps"""
    import clip_gates
    return clip_gates.knee_medial_anim(sk, j, b, anim, name2idx)


def input_hash(cfg, spec, tuning, rows, j, b):
    h = hashlib.md5()
    h.update(TOOL_VERSION.encode()); h.update(json.dumps([rows, tuning, cfg['tools']['legFix']], sort_keys=True).encode())
    h.update(json.dumps(j, sort_keys=True).encode()); h.update(bytes(b))
    for r in rows:
        for key in [r.get('src')] + ([r['derive']['from']] if 'derive' in r else []):
            if key:
                p = resolve_src(cfg, key); st = os.stat(p); h.update(('%s|%d|%d' % (p, st.st_size, int(st.st_mtime))).encode())
    return h.hexdigest()


def build_character(cfg, roster, cid, outdir, proj, args):
    rc = [c for c in roster['characters'] if c['id'] == cid][0]
    spec = cfg['characters'].get(cid, {})
    tuning = cfg.get('tuning', {}).get(spec.get('tuning')) if spec.get('tuning') else None
    if tuning is not None and 'rules' in tuning: fail('%s: tuning profile %s needs the legacy rule bake (not implemented in clip_build.py); use the Dracula bake tools' % (cid, spec.get('tuning')))
    body = os.path.normpath(os.path.join(proj, spec.get('bodyGlb') or rc['modelGlb']))
    if not os.path.exists(body): fail('%s: body glb missing %s' % (cid, body))
    j0, b0 = L.load(body)
    j, b = compact_without_animations(j0, b0)
    name2idx = {n['name']: i for i, n in enumerate(j['nodes']) if 'name' in n}
    for need in cfg['requiredBones']:
        if 'mixamorig:' + need not in name2idx: fail('%s: body lacks bone %s' % (cid, need))
    hips = j['nodes'][name2idx['mixamorig:Hips']]
    q_rig = np.array(hips['rotation']); rig_z = hips['translation'][2]
    q_mix = np.array([-math.sqrt(.5), 0, 0, math.sqrt(.5)]); corr = L.qmul(q_rig, L.qinv(q_mix))
    print('%s: body %s hips z %.5f corr %s ' % (cid, os.path.basename(body), rig_z, np.round(corr, 4)))
    rows = cfg['base'] + spec.get('extraRows', [])
    stamp = input_hash(cfg, spec, tuning, rows, j, b)
    if args.install: out = os.path.normpath(os.path.join(proj, rc['modelGlb'])); animout = os.path.normpath(os.path.join(proj, rc['animGlb'])) if rc.get('animGlb') else None
    else: out = os.path.join(outdir, '%s_combat.glb' % cid); animout = out.replace('_combat.glb', '_anim.glb')
    if os.path.exists(out) and not args.force:
        try:
            jo, _ = L.load(out)
            if jo.get('asset', {}).get('extras', {}).get('clipBuild') == stamp:
                print('%s: up to date (input hash %s) -> %s' % (cid, stamp[:10], out)); return {'tool': TOOL_VERSION, 'character': cid, 'output': out, 'unchanged': True, 'clips': [], 'tuningProfile': spec.get('tuning')}
        except Exception as ex: print('stamp check failed (%s), rebuilding' % ex)
    cache = {}; clips = []; rows_rep = []
    for row in rows:
        c, src_desc, notes, ratio = build_clip(cfg, row, rig_z, corr, tuning, cid, cache)
        clips.append((row['state'], c))
        rows_rep.append({'state': row['state'], 'src': src_desc, 'foreign': row.get('foreign'), 'ops': notes, 'frames': int(len(c.times)), 'durationS': float(c.times[-1]), 'hipsRatio': round(ratio, 4)})
    hp = (tuning or {}).get('headPitch'); pb = (tuning or {}).get('poseBlend')     # MONSTERPOLISH v1 (data; absent = unchanged)
    if hp or pb:
        brig = L.Rig(j, b) if hp else None
        def _hp():
            for r_, (state, c) in zip(rows_rep, clips):
                rp_ = head_pitch_limit(brig, c.rot, c.times, hp, state) if hp else None
                if rp_: r_['headPitch'] = rp_; r_['ops'].append('headPitch min %+g deg (corr max %.1f)' % (rp_['minDeg'], rp_['maxCorrDeg']))
        _hp()
        if pb:
            pr = pose_blend(clips, pb)
            for r_, (state, c) in zip(rows_rep, clips):
                if state in pr: r_['poseBlend'] = pr[state]; r_['ops'].append('poseBlend -> %s t %.2f..%.2f' % (pr[state]['toState'], pr[state]['t0'], pr[state]['t1']))
            if hp: _hp()
    write_clips(j, b, name2idx, clips)
    # leg fix
    lf = cfg['tools']['legFix']; leg_fix.set_cap(lf['maxSwivelDeg'])
    sk, _ = leg_fix.skel_from(j)
    for r, (state, c) in zip(rows_rep, clips):
        an = [a for a in j['animations'] if a['name'] == state][0]
        pre = knee_medial_max(sk, j, b, an, name2idx)
        r['kneeMedialPreFixM'] = round(pre, 4); r['legFix'] = 'none'
        if lf['mode'] == 'always' or (lf['mode'] == 'auto' and pre > lf['kneeMedialTriggerM']):
            n, w = leg_fix.fix_animation(j, b, an, sk, name2idx); r['legFix'] = 'applied'; r['legFixMaxSwivelDeg'] = round(w, 1)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    if os.path.exists(out) and args.install:    # backup next to Tools, never into Assets
        bk = os.path.join(proj, 'Tools', '_backup_clipbuild'); os.makedirs(bk, exist_ok=True); shutil.copy2(out, os.path.join(bk, '%s.%s.bak' % (os.path.basename(out), hashlib.md5(out.encode()).hexdigest()[:6])))
    j.setdefault('asset', {'version': '2.0'}).setdefault('extras', {})['clipBuild'] = stamp
    L.save(j, b, out)
    if animout and animout != out: shutil.copy2(out, animout)
    md5 = hashlib.md5(open(out, 'rb').read()).hexdigest()
    rep = {'tool': TOOL_VERSION, 'character': cid, 'body': body, 'tuningProfile': spec.get('tuning'), 'output': out, 'md5': md5, 'bytes': os.path.getsize(out), 'clips': rows_rep}
    print('%s: wrote %s (%d bytes, %d clips)' % (cid, out, rep['bytes'], len(clips)))
    return rep


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--char'); ap.add_argument('--roster', action='store_true'); ap.add_argument('--out'); ap.add_argument('--check', action='store_true')
    ap.add_argument('--gates', action='store_true'); ap.add_argument('--install', action='store_true', help='write over roster modelGlb/animGlb inside the project (backup in Tools/_backup_clipbuild)'); ap.add_argument('--force', action='store_true'); ap.add_argument('--proj'); ap.add_argument('--config')
    a = ap.parse_args()
    proj = a.proj or find_proj(ROOT) or find_proj(os.getcwd())
    if not proj: fail('project root (Assets/Data/roster.json) not found; pass --proj')
    cfgp = a.config or os.path.join(ROOT, 'Assets', 'Data', 'clip_build.json')
    cfg = read_json(cfgp); roster = read_json(os.path.join(proj, 'Assets', 'Data', 'roster.json'))
    ids = []
    if a.char: ids = [a.char]
    elif a.roster: ids = [roster['player'], roster['enemy']] + list(cfg.get('alwaysBuild', []))
    else: fail('give --char <id> or --roster')
    ids = [i for n, i in enumerate(ids) if i not in ids[:n]]
    known = [c['id'] for c in roster['characters']]
    for i in ids:
        if i not in known: fail('%s is not in roster.json' % i)
    errs = []
    skipped = {}
    if not a.char:       # an explicit --char always builds (review use); the roster sweep honours skipIds
        for i in list(ids):
            if i in cfg.get('skipIds', []): skipped[i] = cfg.get('skipReason', 'skipIds'); ids.remove(i)
    errs = check_config(cfg, roster, ids)
    if errs:
        for e in errs: print('CLIPBUILD ERROR:', e)
        sys.exit(3)
    for i, why in skipped.items(): print('%s: base build SKIPPED by config: %s' % (i, why))
    if a.check: print('check ok for', ids); return
    outdir = os.path.normpath(a.out) if a.out else os.path.normpath(os.path.join(ROOT, cfg['outputDir']))
    if a.install and not a.out: outdir = os.path.join(proj, 'Tools')      # gates json / report land next to the other Tools outputs
    os.makedirs(outdir, exist_ok=True)
    reps = [build_character(cfg, roster, i, outdir, proj, a) for i in ids]
    rp = os.path.join(proj, 'Tools', 'clip_build_report.json') if a.install else os.path.join(outdir, 'clip_build_report.json')
    os.makedirs(os.path.dirname(rp), exist_ok=True)
    prev = {}
    if os.path.exists(rp):
        try: prev = {r['character']: r for r in json.load(open(rp))['built']}
        except Exception: prev = {}
    for k, r in enumerate(reps):
        if r.get('unchanged'):
            if r['character'] in prev and prev[r['character']].get('clips'): reps[k] = dict(prev[r['character']], unchanged=True)
            else: fail('%s up to date but no previous report to gate; rerun with --force' % r['character'])
    if not all(r.get('unchanged') for r in reps):
        json.dump({'tool': TOOL_VERSION, 'built': reps, 'skipped': skipped}, open(rp, 'w'), indent=1)
        print('report', rp)
    if a.gates:
        import clip_gates
        bad = clip_gates.run(cfg, roster, [r for r in reps if r.get('clips')], proj, outdir)
        hard = [x for x in bad if 'G-rig' not in x]
        rigb = [x for x in bad if 'G-rig' in x]
        sys.exit(3 if hard or (rigb and cfg['gates']['rigAudit'].get('blocking', True)) else 0)


if __name__ == '__main__':
    main()

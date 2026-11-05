"""CLIPBUILD: the ONE shared leg fix (replaces Tools/joanlegs/leg_fix.py cap 50 and laneJOANLEGS cap 35; cap 50 chosen: dodge output md5 identical at both caps and attacks need more than 35). Cap is set by clip_build.json (set_cap). Original docstring: knee-swivel correction for Mixamo-library clips on Joan (CPU, numpy only).

Finding (see LEGS.md): the library clips are knock-kneed on THEIR OWN skeleton (knee 0.057 m apart at hip width 0.16 in idle), so copying them (from Dracula or from the Mixamo source) is not the cause.
Fix: per frame, per leg, keep hip and ankle exactly where the clip puts them (feet stay planted, leg lengths unchanged) and swivel the knee about the hip-ankle axis (2-bone IK swivel)
until the knee is no longer medial of the hip-ankle line, measured laterally in the PELVIS frame (soft clamp, so nothing pops). Foot world rotation is kept (soles stay flat).
Rotations only (UpLeg, Leg, Foot of both sides). Works on any 106-joint mixamorig GLB: library(j, b, anim) or CLI on a whole file.
"""
import math, sys, json
import numpy as np

S = 0.012          # soft-clamp width in metres
E_TARGET = -0.012  # knee ends this far OUTSIDE the hip-ankle line when the clip had it inside (knee over foot, slightly out like the chef's own walk)
MAX_SWIVEL = math.radians(50)


def set_cap(deg):
    global MAX_SWIVEL
    MAX_SWIVEL = math.radians(deg)


def q2m(q):
    x, y, z, w = q / np.linalg.norm(q)
    return np.array([[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)], [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)], [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]])


def m2q(m):
    t = np.trace(m)
    if t > 0:
        s = math.sqrt(t + 1) * 2; q = [(m[2, 1]-m[1, 2])/s, (m[0, 2]-m[2, 0])/s, (m[1, 0]-m[0, 1])/s, s/4]
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1+m[0, 0]-m[1, 1]-m[2, 2])*2; q = [s/4, (m[0, 1]+m[1, 0])/s, (m[0, 2]+m[2, 0])/s, (m[2, 1]-m[1, 2])/s]
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1+m[1, 1]-m[0, 0]-m[2, 2])*2; q = [(m[0, 1]+m[1, 0])/s, s/4, (m[1, 2]+m[2, 1])/s, (m[0, 2]-m[2, 0])/s]
    else:
        s = math.sqrt(1+m[2, 2]-m[0, 0]-m[1, 1])*2; q = [(m[0, 2]+m[2, 0])/s, (m[1, 2]+m[2, 1])/s, s/4, (m[1, 0]-m[0, 1])/s]
    q = np.array(q); return q / np.linalg.norm(q)


def slerp(a, b, t):
    d = np.dot(a, b)
    if d < 0: b = -b; d = -d
    if d > 0.9995:
        r = a + t*(b-a); return r/np.linalg.norm(r)
    th = math.acos(d); return (math.sin((1-t)*th)*a + math.sin(t*th)*b)/math.sin(th)


def rot_axis(u, ang):
    u = u/np.linalg.norm(u); K = np.array([[0, -u[2], u[1]], [u[2], 0, -u[0]], [-u[1], u[0], 0]])
    return np.eye(3) + math.sin(ang)*K + (1-math.cos(ang))*(K@K)


def swing(a, b):
    """minimal rotation matrix taking unit-ish vector a to b"""
    a = a/np.linalg.norm(a); b = b/np.linalg.norm(b); v = np.cross(a, b); c = float(np.dot(a, b)); s = np.linalg.norm(v)
    if s < 1e-9: return np.eye(3)
    return rot_axis(v, math.atan2(s, c))


def softmin0(e):  # -> e for e << 0, -> 0 for e >> 0
    x = -e/S
    return -S*(x if x > 30 else math.log1p(math.exp(x)))


class Skel:
    def __init__(s, nodes, idx):
        s.nodes = nodes; s.idx = idx; s.parent = {}
        for i, n in enumerate(nodes):
            for c in n.get('children', []): s.parent[c] = i
        s.rt = [np.array(n.get('translation', [0, 0, 0]), float) for n in nodes]
        s.rq = [np.array(n.get('rotation', [0, 0, 0, 1]), float) for n in nodes]
        s.ids = {k: idx['mixamorig:'+k] for k in ('Hips', 'LeftUpLeg', 'LeftLeg', 'LeftFoot', 'RightUpLeg', 'RightLeg', 'RightFoot')}
        # chain from root to Hips (static part)
        chain = []; i = s.ids['Hips']
        while i in s.parent: i = s.parent[i]; chain.append(i)
        s.hips_parent_chain = chain[::-1]
        R = np.eye(3); T = np.zeros(3)
        for i in s.hips_parent_chain:
            T = T + R @ s.rt[i]; R = R @ q2m(s.rq[i])
        s.R0, s.T0 = R, T


def fix_frame(sk, qh, th, legs):
    """qh, th: Hips local rotation / translation; legs: dict side -> (q_up, q_leg, q_foot) local. Returns new (q_up,q_leg,q_foot) per side + max swivel."""
    Rh = sk.R0 @ q2m(qh); Th = sk.T0 + sk.R0 @ th
    out = {}; worst = 0.0
    for side in ('Left', 'Right'):
        iu, il, ifo = sk.ids[side+'UpLeg'], sk.ids[side+'Leg'], sk.ids[side+'Foot']
        qu, ql, qf = legs[side]
        Ru = Rh @ q2m(qu); Pu = Th + Rh @ sk.rt[iu]
        Rl = Ru @ q2m(ql); Pl = Pu + Ru @ sk.rt[il]
        Rf = Rl @ q2m(qf); Pf = Pl + Rl @ sk.rt[ifo]
        # pelvis frame lateral axis: toward the body midline for this leg
        lat = Rh @ np.array([1.0, 0, 0])
        other = Th + Rh @ sk.rt[sk.ids[('Right' if side == 'Left' else 'Left')+'UpLeg']]
        med = lat if np.dot(other - Pu, lat) > 0 else -lat      # points to midline
        H, K, A = Pu, Pl, Pf
        u = (A-H); L = np.linalg.norm(u)
        if L < 1e-6: out[side] = legs[side]; continue
        u = u/L; p = (K-H) - np.dot(K-H, u)*u; r = np.linalg.norm(p)
        e = float(np.dot(p, med))                                # + = knee medial of the hip-ankle line
        e_new = E_TARGET + softmin0(e - E_TARGET)               # soft clamp: e_new -> e when well outside the limit, -> E_TARGET when inside
        if r < 1e-6 or e_new >= e - 1e-6: out[side] = legs[side]; continue
        # swivel K about axis u by phi so that medial component of p' = e_new
        c = np.cross(u, p); a = math.hypot(np.dot(p, med), np.dot(c, med))
        if a < 1e-9: out[side] = legs[side]; continue
        ph0 = math.atan2(np.dot(c, med), np.dot(p, med))         # comp(phi) = a cos(phi - ph0)
        val = max(-1.0, min(1.0, e_new/a)); d = math.acos(val)
        cands = [ph0 + d, ph0 - d]
        phi = min(cands, key=lambda x: abs(math.atan2(math.sin(x), math.cos(x))))
        phi = math.atan2(math.sin(phi), math.cos(phi)); phi = max(-MAX_SWIVEL, min(MAX_SWIVEL, phi))
        Sw = rot_axis(u, phi); Kn = H + Sw @ (K-H)
        Sf = swing(K-H, Kn-H); St = swing(A-K, A-Kn)
        Ru2 = Sf @ Ru; Rl2 = St @ Rl
        qu2 = m2q(Rh.T @ Ru2); ql2 = m2q(Ru2.T @ Rl2); qf2 = m2q(Rl2.T @ Rf)
        # keep quaternion hemisphere continuous with input
        if np.dot(qu2, qu) < 0: qu2 = -qu2
        if np.dot(ql2, ql) < 0: ql2 = -ql2
        if np.dot(qf2, qf) < 0: qf2 = -qf2
        out[side] = (qu2, ql2, qf2); worst = max(worst, abs(phi))
    return out, worst


def acc_read(j, b, ai):
    a = j['accessors'][ai]; bv = j['bufferViews'][a['bufferView']]; n = {'SCALAR': 1, 'VEC3': 3, 'VEC4': 4}[a['type']]
    off = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    if a['componentType'] == 5122:
        arr = np.frombuffer(bytes(b[off:off+a['count']*n*2]), dtype='<i2').reshape(a['count'], n).astype(float); return np.maximum(arr/32767.0, -1.0)
    return np.frombuffer(bytes(b[off:off+a['count']*n*4]), dtype='<f4').reshape(a['count'], n).astype(float)


def add_acc(j, b, arr, typ, componentType=5126):
    arr = arr.astype('<f4').tobytes()
    while len(b) % 4: b.append(0)
    o = len(b); b.extend(arr)
    j['bufferViews'].append({'buffer': 0, 'byteOffset': o, 'byteLength': len(arr)})
    a = {'bufferView': len(j['bufferViews'])-1, 'componentType': 5126, 'count': len(arr)//4//{'SCALAR': 1, 'VEC3': 3, 'VEC4': 4}[typ], 'type': typ}
    j['accessors'].append(a); return len(j['accessors'])-1


def sample(times, vals, t):
    k = max(0, min(len(times)-2, int(np.searchsorted(times, t))-1)) if len(times) > 1 else 0
    if len(times) == 1 or t <= times[0]: return vals[0]
    if t >= times[-1]: return vals[-1]
    f = (t - times[k])/(times[k+1]-times[k]); return slerp(vals[k], vals[k+1], f)


def fix_animation(j, b, anim, sk, name2idx, rest_hips=None):
    """rewrites the rotation channels of the 6 leg bones of `anim` in place. Returns (frames, worst swivel deg)."""
    ch = {}
    for c in anim['channels']:
        ch[(c['target']['node'], c['target']['path'])] = c
    need = [(sk.ids[s+k], 'rotation') for s in ('Left', 'Right') for k in ('UpLeg', 'Leg', 'Foot')]
    hk = (sk.ids['Hips'], 'rotation'); ht = (sk.ids['Hips'], 'translation')
    if any(k not in ch for k in need) or hk not in ch: return 0, 0.0
    def get(k):
        c = ch[k]; sm = anim['samplers'][c['sampler']]; return acc_read(j, b, sm['input'])[:, 0], acc_read(j, b, sm['output']), sm
    t_ref, _, sm_ref = get(need[0])
    data = {k: get(k) for k in need + [hk]}
    hq_t, hq_v, _ = data[hk]
    N = len(t_ref); newv = {k: np.zeros((N, 4)) for k in need}; worst = 0.0
    for i, t in enumerate(t_ref):
        qh = sample(hq_t, hq_v, t); th = np.zeros(3)             # only relative geometry matters
        legs = {}
        for side in ('Left', 'Right'):
            legs[side] = tuple(sample(data[(sk.ids[side+k], 'rotation')][0], data[(sk.ids[side+k], 'rotation')][1], t) for k in ('UpLeg', 'Leg', 'Foot'))
        res, w = fix_frame(sk, qh, th, legs); worst = max(worst, w)
        for side in ('Left', 'Right'):
            for q, k in zip(res[side], ('UpLeg', 'Leg', 'Foot')): newv[(sk.ids[side+k], 'rotation')][i] = q
    # write: one shared input accessor (re-use first), new float output accessors, one new sampler per channel
    inp = sm_ref['input']
    for k in need:
        oi = add_acc(j, b, newv[k], 'VEC4')
        if data[k][2]['input'] != inp:
            pass
        anim['samplers'].append({'input': inp, 'output': oi, 'interpolation': 'LINEAR'})
        ch[k]['sampler'] = len(anim['samplers'])-1
    return N, math.degrees(worst)


def skel_from(j):
    idx = {n['name']: i for i, n in enumerate(j['nodes']) if 'name' in n}
    return Skel(j['nodes'], idx), idx


if __name__ == '__main__':
    sys.path.insert(0, __import__('os').path.dirname(__file__))
    import struct, os
    def load(p):
        d = open(p, 'rb').read(); n = struct.unpack('<I', d[12:16])[0]; j = json.loads(d[20:20+n]); off = 20+n
        bl = struct.unpack('<I', d[off:off+4])[0]; return j, bytearray(d[off+8:off+8+bl])
    def save(j, b, p):
        while len(b) % 4: b.append(0)
        j['buffers'] = [{'byteLength': len(b)}]; js = json.dumps(j).encode()
        while len(js) % 4: js += b' '
        out = struct.pack('<III', 0x46546C67, 2, 12+8+len(js)+8+len(b)) + struct.pack('<I', len(js)) + b'JSON' + js + struct.pack('<I', len(b)) + b'BIN\x00' + bytes(b)
        open(p, 'wb').write(out)
    src, dst = sys.argv[1], sys.argv[2]
    j, b = load(src); sk, idx = skel_from(j)
    for a in j['animations']:
        n, w = fix_animation(j, b, a, sk, idx); print(a['name'], n, 'frames, max swivel %.1f deg' % w)
    save(j, b, dst); print('saved', dst)


"""Posture fix + metrics for the attack bake (used by Tools/bake_attack_clips.py, and runnable on its own for the metrics table).

Works on the RETARGETED clip (the same quaternion/hips maths as the bake) in forward-kinematics space (numpy only, no Unity, no Blender):
  1. hips: world swing (pitch+roll) capped to hipsMaxTiltDeg, hips height floored at hipsMinY, horizontal travel = frame 0 (in place)
  2. spine: Hips->Neck chord lean capped to torsoMaxLeanDeg (swing correction on mixamorig:Spine, twist kept)
  3. legs: two-bone IK, feet on the ground (ankle height = rest ankle + the clip's own lift, capped), step XZ kept (pulled in only when out of reach), foot levelled when planted
  4. sword arm: wrist re-orientation in the strike window so the blade axis (hand->middle finger, the sword socket axis) leads the hand velocity
  5. strike height: right-arm two-bone IK lifts/lowers the hand at contact towards chest height (data: contactHandY)
All numbers come from the "posture" block of the bake clip row in Assets/Data/attack_clips.json (defaults in "postureDefaults").

  python Tools/attack_posture_fix.py metrics   -> before/after table for the configured clips (writes Tools/attack_posture_report.json)
"""
import json, os, sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
ROOT = os.path.dirname(HERE)

# ------------------------------------------------------------------ small maths
def q2m(q):
    x, y, z, w = q[..., 0], q[..., 1], q[..., 2], q[..., 3]
    m = np.empty(q.shape[:-1] + (3, 3))
    m[..., 0, 0] = 1 - 2 * (y * y + z * z); m[..., 0, 1] = 2 * (x * y - z * w); m[..., 0, 2] = 2 * (x * z + y * w)
    m[..., 1, 0] = 2 * (x * y + z * w); m[..., 1, 1] = 1 - 2 * (x * x + z * z); m[..., 1, 2] = 2 * (y * z - x * w)
    m[..., 2, 0] = 2 * (x * z - y * w); m[..., 2, 1] = 2 * (y * z + x * w); m[..., 2, 2] = 1 - 2 * (x * x + y * y)
    return m


def m2q(R):
    t = R[0, 0] + R[1, 1] + R[2, 2]
    if t > 0:
        s = np.sqrt(t + 1) * 2; q = [(R[2, 1] - R[1, 2]) / s, (R[0, 2] - R[2, 0]) / s, (R[1, 0] - R[0, 1]) / s, s / 4]
    elif R[0, 0] > R[1, 1] and R[0, 0] > R[2, 2]:
        s = np.sqrt(1 + R[0, 0] - R[1, 1] - R[2, 2]) * 2; q = [s / 4, (R[0, 1] + R[1, 0]) / s, (R[0, 2] + R[2, 0]) / s, (R[2, 1] - R[1, 2]) / s]
    elif R[1, 1] > R[2, 2]:
        s = np.sqrt(1 + R[1, 1] - R[0, 0] - R[2, 2]) * 2; q = [(R[0, 1] + R[1, 0]) / s, s / 4, (R[1, 2] + R[2, 1]) / s, (R[0, 2] - R[2, 0]) / s]
    else:
        s = np.sqrt(1 + R[2, 2] - R[0, 0] - R[1, 1]) * 2; q = [(R[0, 2] + R[2, 0]) / s, (R[1, 2] + R[2, 1]) / s, s / 4, (R[1, 0] - R[0, 1]) / s]
    q = np.array(q); return q / np.linalg.norm(q)


def unit(v):
    n = np.linalg.norm(v, axis=-1, keepdims=True)
    return v / np.maximum(n, 1e-9)


def axang(axis, ang):
    a = unit(np.asarray(axis, float)); c, s = np.cos(ang), np.sin(ang); x, y, z = a
    return np.array([[c + x * x * (1 - c), x * y * (1 - c) - z * s, x * z * (1 - c) + y * s],
                     [y * x * (1 - c) + z * s, c + y * y * (1 - c), y * z * (1 - c) - x * s],
                     [z * x * (1 - c) - y * s, z * y * (1 - c) + x * s, c + z * z * (1 - c)]])


def from_to(a, b):
    """minimal rotation matrix taking direction a to direction b"""
    a = unit(np.asarray(a, float)); b = unit(np.asarray(b, float))
    c = float(np.clip(a @ b, -1, 1)); ax = np.cross(a, b); n = np.linalg.norm(ax)
    if n < 1e-9:
        if c > 0: return np.eye(3)
        p = np.cross(a, [1, 0, 0]);
        if np.linalg.norm(p) < 1e-6: p = np.cross(a, [0, 1, 0])
        return axang(p, np.pi)
    return axang(ax, np.arctan2(n, c))


def angle(a, b):
    a = unit(np.asarray(a, float)); b = unit(np.asarray(b, float))
    return float(np.degrees(np.arccos(np.clip(np.sum(a * b, -1), -1, 1))))


def clamp_swing(R, cap_rad):
    """R = Rswing * Rtwist(about Y). Returns R with the swing (tilt of Y) capped."""
    u = R @ np.array([0, 1.0, 0]); Rs = from_to([0, 1.0, 0], u); Rt = Rs.T @ R
    ang = np.arccos(np.clip(u[1], -1, 1))
    if ang <= cap_rad: return R, float(np.degrees(ang))
    ax = np.cross([0, 1.0, 0], u)
    return axang(ax, cap_rad) @ Rt, float(np.degrees(ang))


def smooth(x):
    x = np.clip(x, 0, 1); return x * x * (3 - 2 * x)


# ------------------------------------------------------------------ rig FK
class Rig:
    def __init__(s, j):
        s.N = j['nodes']; s.n2i = {n.get('name'): i for i, n in enumerate(s.N)}
        s.par = {}
        for i, n in enumerate(s.N):
            for c in n.get('children', []): s.par[c] = i
        s.t0 = np.array([n.get('translation', [0, 0, 0]) for n in s.N], float)
        s.q0 = np.array([n.get('rotation', [0, 0, 0, 1]) for n in s.N], float)
        s.sc = np.array([n.get('scale', [1, 1, 1]) for n in s.N], float)
        order = []; seen = set()

        def vis(i):
            if i in seen: return
            if i in s.par: vis(s.par[i])
            seen.add(i); order.append(i)
        for i in range(len(s.N)): vis(i)
        s.order = order
        s.hips = s.n2i['mixamorig:Hips']

    def idx(s, n): return s.n2i['mixamorig:' + n]

    def fk(s, rot, hipt):
        T = hipt.shape[0]; W = np.zeros((len(s.N), T, 4, 4))
        for i in s.order:
            L = np.zeros((T, 4, 4)); L[:, 3, 3] = 1
            q = rot[i] if i in rot else np.tile(s.q0[i], (T, 1))
            L[:, :3, :3] = q2m(q) * s.sc[i][None, None, :]
            L[:, :3, 3] = hipt if i == s.hips else s.t0[i]
            W[i] = L if i not in s.par else W[s.par[i]] @ L
        return W

    def rest_world(s):
        rot = {}; return s.fk(rot, np.tile(s.t0[s.hips], (1, 1)))[:, 0]


def rotpart(M):  # (...,4,4) -> orthonormal 3x3
    R = M[..., :3, :3].copy()
    for k in range(3):
        R[..., :, k] = unit(R[..., :, k])
    return R


def set_world_rot(rig, rot, hipt, node, Rw, only=None):
    """set local rotation of node so its world rotation is Rw (T,3,3) (parent chain as currently in rot)"""
    W = rig.fk(rot, hipt)
    p = rig.par.get(node)
    Pw = rotpart(W[p]) if p is not None else np.tile(np.eye(3), (hipt.shape[0], 1, 1))
    loc = np.transpose(Pw, (0, 2, 1)) @ Rw
    q = np.array([m2q(loc[t]) for t in range(loc.shape[0])])
    # keep the quaternion hemisphere continuous with the previous values
    old = rot.get(node)
    if old is not None:
        d = np.sum(q * old, -1) < 0; q[d] *= -1
    rot[node] = q


def retarget(rig, rig_json_hips, clip_path, B):
    """same maths as the bake (Tools/bake_attack_clips.py): returns times, rot{node: (T,4)}, hips parent-space translation (T,3), ratio"""
    hips = rig.N[rig.hips]
    q_rig = np.array(hips['rotation']); rig_z = hips['translation'][2]
    q_mix = np.array([-np.sqrt(.5), 0, 0, np.sqrt(.5)]); qi = q_mix * np.array([-1, -1, -1, 1])
    corr = B.qmul(q_rig, qi[None, :])[0]
    cj, cb = B.load(clip_path)
    ch = [n for n in cj['nodes'] if n.get('name') == 'mixamorig:Hips'][0]
    ratio = min(3.0, max(0.33, rig_z / abs(ch['translation'][2])))
    an = cj['animations'][0]; rot = {}; hipt = None; times = None
    for c in an['channels']:
        nm = cj['nodes'][c['target']['node']].get('name'); p = c['target']['path']
        if nm not in rig.n2i or p not in ('rotation', 'translation'): continue
        if p == 'translation' and nm != 'mixamorig:Hips': continue
        s = an['samplers'][c['sampler']]
        t = B.acc_data(cj, cb, s['input'])[:, 0]; v = B.acc_data(cj, cb, s['output'])
        if times is None: times = t
        elif len(t) != len(times) or abs(t - times).max() > 1e-6: raise Exception('timeline mismatch ' + nm)
        if nm == 'mixamorig:Hips' and p == 'rotation': v = B.qmul(corr, v)
        elif nm == 'mixamorig:Hips' and p == 'translation': v = B.qrot(corr, v * ratio)
        if p == 'rotation': rot[rig.n2i[nm]] = v
        else: hipt = v
    return times, rot, hipt, ratio


def inplace(hipt):
    h = hipt.copy(); h[:, 0] = h[0, 0]; h[:, 1] = h[0, 1]; return h


# ------------------------------------------------------------------ metrics
DEF_BLADE = {'socketReachFrac': 0.55, 'tipLength': 0.6147}   # BuildSlice.AttachWeapon(sword_start ... tipY 0.6147): socket at 0.55 * |hand->middle1| along the hand->middle1 axis, blade +Y along it


def metrics(rig, times, rot, hipt, contact, rest, blade=None, window=0.06):
    blade = blade or DEF_BLADE
    W = rig.fk(rot, hipt); T = len(times)
    P = lambda n: W[rig.idx(n), :, :3, 3]
    hips, neck = P('Hips'), P('Neck')
    Rh = rotpart(W[rig.hips]); up = Rh @ np.array([0, 1.0, 0])
    tilt = np.degrees(np.arccos(np.clip(up[:, 1], -1, 1)))
    pitch = np.degrees(np.arctan2(up[:, 2], up[:, 1]))          # + = leaning to +Z (glTF forward)
    rest_hips_y = rest[rig.hips][1, 3]
    out = {'hipsTiltMaxDeg': float(tilt.max()), 'hipsPitchFwdMaxDeg': float(pitch.max()),
           'hipsDropM': float(rest_hips_y - hips[:, 1].min()), 'hipsMinY': float(hips[:, 1].min())}
    # feet
    lifts = {}
    for side in ('Left', 'Right'):
        a = P(side + 'Foot')[:, 1] - rest[rig.idx(side + 'Foot')][1, 3]
        tb = P(side + 'ToeBase')[:, 1] - rest[rig.idx(side + 'ToeBase')][1, 3]
        lifts[side] = np.minimum(a, tb)
    both = np.minimum(lifts['Left'], lifts['Right'])
    out['airborneFrames'] = int(np.sum(both > 0.06))
    out['frames'] = T
    out['maxFootLiftM'] = float(max(lifts['Left'].max(), lifts['Right'].max()))
    out['footBelowFloorM'] = float(-min(lifts['Left'].min(), lifts['Right'].min(), 0))
    chord = neck - hips; lean = np.array([angle(chord[t], [0, 1, 0]) for t in range(T)])
    out['torsoLeanMaxDeg'] = float(lean.max())
    # contact
    ci = int(np.argmin(abs(times - contact)))
    out['torsoLeanAtContactDeg'] = float(lean[ci])
    hand, mid = P('RightHand'), P('RightHandMiddle1')
    axis = unit(mid - hand)
    tip = hand + axis * (blade['socketReachFrac'] * np.linalg.norm(mid - hand, axis=1, keepdims=True) + blade['tipLength'])
    dt = np.gradient(times)
    vh = np.gradient(hand, axis=0) / dt[:, None]; vt = np.gradient(tip, axis=0) / dt[:, None]
    # smooth the velocity over +-window
    def sv(v):
        o = np.zeros_like(v)
        for t in range(T):
            m = abs(times - times[t]) <= window + 1e-6; o[t] = v[m].mean(0)
        return o
    vh, vt = sv(vh), sv(vt)
    out['contactIdx'] = ci
    out['bladeLeadAngleDeg'] = angle(axis[ci], vh[ci])           # blade axis vs hand velocity (<90 = blade leads the hand)
    out['tipVsHandSpeed'] = float(np.linalg.norm(vt[ci]) / max(1e-6, np.linalg.norm(vh[ci])))
    out['tipAheadOfHandM'] = float(np.dot(tip[ci] - hand[ci], unit(vh[ci])))   # >0 = tip is ahead of the hand along its motion
    out['bladeAxisY'] = float(axis[ci][1]); out['bladeAxisFwdZ'] = float(axis[ci][2])
    out['handSpeed'] = float(np.linalg.norm(vh[ci]))
    sh = P('RightArm')[ci]
    out['handY'] = float(hand[ci][1]); out['handMinusShoulderY'] = float(hand[ci][1] - sh[1])
    out['tipY'] = float(tip[ci][1]); out['tipFwdOfHipsM'] = float(tip[ci][2] - hipt[0, 2] * 0 - hips[0][2])
    out['shoulderY'] = float(sh[1])
    # blade lead over the strike window (+-0.1 s)
    m = abs(times - contact) <= 0.1
    out['bladeLeadWindowMeanDeg'] = float(np.mean([angle(axis[t], vh[t]) for t in np.where(m)[0]]))
    return out, dict(hand=hand, tip=tip, axis=axis, vh=vh, W=W)


# ------------------------------------------------------------------ IK helpers
def two_bone(rig, rot, hipt, a, b, c, target, pole, W=None):
    """rotate bones a (e.g. UpLeg) and b (Leg) so joint c (Foot) reaches target; per frame. a,b,c node indexes. pole (T,3) = desired knee direction"""
    T = hipt.shape[0]
    W = W if W is not None else rig.fk(rot, hipt)
    pa, pb, pc = W[a][:, :3, 3], W[b][:, :3, 3], W[c][:, :3, 3]
    L1 = np.linalg.norm(pb - pa, axis=1); L2 = np.linalg.norm(pc - pb, axis=1)
    Ra = np.zeros((T, 3, 3)); knees = np.zeros((T, 3))
    for t in range(T):
        d = target[t] - pa[t]; dist = np.linalg.norm(d); dist = min(dist, (L1[t] + L2[t]) * 0.9995); dist = max(dist, abs(L1[t] - L2[t]) + 1e-4)
        dn = unit(d)
        x = (dist * dist + L1[t] ** 2 - L2[t] ** 2) / (2 * dist)
        h = np.sqrt(max(L1[t] ** 2 - x * x, 0))
        pv = pole[t] - dn * np.dot(pole[t], dn)
        if np.linalg.norm(pv) < 1e-6: pv = np.cross(dn, [1, 0, 0])
        pv = unit(pv)
        knees[t] = pa[t] + dn * x + pv * h
    # a: swing so that a->b points at the knee
    Rwa = np.array([from_to(pb[t] - pa[t], knees[t] - pa[t]) @ rotpart(W[a][t:t + 1])[0] for t in range(T)])
    set_world_rot(rig, rot, hipt, a, Rwa)
    W2 = rig.fk(rot, hipt)
    pb2, pc2 = W2[b][:, :3, 3], W2[c][:, :3, 3]
    Rwb = np.array([from_to(pc2[t] - pb2[t], np.where(True, target[t], 0) - pb2[t] if np.linalg.norm(target[t] - pa[t]) <= (L1[t] + L2[t]) * 0.9995 else unit(target[t] - pa[t]) * L2[t]) @ rotpart(W2[b][t:t + 1])[0] for t in range(T)])
    set_world_rot(rig, rot, hipt, b, Rwb)
    return knees


# ------------------------------------------------------------------ the fix
DEFAULTS = {
    'hipsMaxTiltDeg': 12.0, 'hipsMinY': 0.80, 'torsoMaxLeanDeg': 15.0,
    'ankleLiftCapM': 0.14, 'stanceGateM': 0.08, 'plantedLiftM': 0.04, 'footFlatBlendM': 0.10, 'maxStrideM': 0.55,
    'bladeMaxLeadDeg': 70.0, 'bladeWristMaxDeg': 70.0, 'bladeWindowS': 0.12, 'bladeBlendS': 0.10,
    'contactHandY': 0.0, 'contactHandBlendS': 0.14, 'contactHandMaxShiftM': 0.25,
    'yawAbsolute': False, 'footYawMaxDeg': 0.0, 'hipsYawMaxDeg': 0.0, 'chestYawMaxDeg': 0.0, 'bladeForearmMaxDeg': 0.0, 'bladeAim': [], 'handClamp': [],
    'enableLegs': True, 'enableBlade': True, 'enableHeight': True,
}


def fix(rig, times, rot0, hipt0, contact, params, rest, log=None):
    p = dict(DEFAULTS); p.update(params or {})
    rot = {k: v.copy() for k, v in rot0.items()}
    T = len(times)
    hipt = inplace(hipt0)
    W0 = rig.fk(rot, hipt)
    P0 = lambda n: W0[rig.idx(n), :, :3, 3]
    hips_rest_y = rest[rig.hips][1, 3]
    # --- 1 hips: orientation swing cap + height floor (parent space z = world up, y = -world z)
    Rh = rotpart(W0[rig.hips]); Rn = np.zeros_like(Rh); tilt0 = []
    cap = np.radians(p['hipsMaxTiltDeg'])
    for t in range(T):
        Rn[t], a = clamp_swing(Rh[t], cap); tilt0.append(a)
    if p['hipsYawMaxDeg'] > 0:   # cap the hips twist (about the vertical) relative to frame 0
        def _tw(R):
            u = R @ np.array([0, 1.0, 0]); Rs = from_to([0, 1.0, 0], u); Rt = Rs.T @ R
            return Rs, float(np.arctan2(Rt[0, 2], Rt[2, 2]))
        _, y0_ = _tw(Rn[0]); y0_ = 0.0 if p['yawAbsolute'] else y0_; capy = np.radians(p['hipsYawMaxDeg'])
        for t in range(T):
            Rs, y_ = _tw(Rn[t]); d = (y_ - y0_ + np.pi) % (2 * np.pi) - np.pi
            Rn[t] = Rs @ axang([0, 1, 0], y0_ + float(np.clip(d, -capy, capy)))
    _zf = np.array([0, 0, 1.0])
    _hd = lambda Rm: np.array([np.arctan2((Rm[t] @ _zf)[0], (Rm[t] @ _zf)[2]) for t in range(T)])
    hd_new = _hd(Rn)
    yaw_delta = (_hd(Rn) - _hd(Rh) + np.pi) % (2 * np.pi) - np.pi   # how far the hips were turned: the stance (foot targets) turns with them
    set_world_rot(rig, rot, hipt, rig.hips, Rn)
    h = hipt.copy()
    y0 = h[:, 2].copy()
    h[:, 2] = np.maximum(y0, p['hipsMinY']) if p['hipsMinY'] > 0 else y0
    hipt = h
    # --- 2 spine lean cap (swing about mixamorig:Spine)
    spine = rig.idx('Spine'); capL = p['torsoMaxLeanDeg']
    for it in range(4):
        Wc = rig.fk(rot, hipt)
        chord = Wc[rig.idx('Neck')][:, :3, 3] - Wc[rig.hips][:, :3, 3]
        lean = np.array([angle(chord[t], [0, 1, 0]) for t in range(T)])
        if lean.max() <= capL + 0.3: break
        Rsp = rotpart(Wc[spine]).copy()
        for t in range(T):
            if lean[t] > capL + 0.2:
                c = unit(chord[t]); target = unit(np.array([c[0], 0, c[2]]) * np.sin(np.radians(capL)) / max(1e-9, np.hypot(c[0], c[2])) + np.array([0, np.cos(np.radians(capL)), 0]))
                Rsp[t] = from_to(c, target) @ Rsp[t]
        set_world_rot(rig, rot, hipt, spine, Rsp)
    if p['chestYawMaxDeg'] > 0:   # chest heading (Spine2) vs frame 0, corrected on Spine1
        s1, s2 = rig.idx('Spine1'), rig.idx('Spine2'); capc = np.radians(p['chestYawMaxDeg'])
        for it in range(2):
            Wc = rig.fk(rot, hipt); R2 = rotpart(Wc[s2]); R1 = rotpart(Wc[s1]).copy()
            hd = np.array([np.arctan2((R2[t] @ [0, 0, 1.0])[0], (R2[t] @ [0, 0, 1.0])[2]) for t in range(T)])
            for t in range(T):
                d = (hd[t] - (0.0 if p['yawAbsolute'] else hd[0]) + np.pi) % (2 * np.pi) - np.pi
                ex = abs(d) - capc
                if ex > 0: R1[t] = axang([0, 1, 0], -np.sign(d) * ex) @ R1[t]
            set_world_rot(rig, rot, hipt, s1, R1)
    # --- 3 legs: IK to the ground
    if p['enableLegs']:
        Wd = rig.fk(rot, hipt)   # pose after hips/spine fix (legs still FK from the clip)
        raw = {}
        for side in ('Left', 'Right'):
            ft_, tb_ = rig.idx(side + 'Foot'), rig.idx(side + 'ToeBase')
            raw[side] = np.clip(np.minimum(W0[ft_][:, 1, 3] - rest[ft_][1, 3], W0[tb_][:, 1, 3] - rest[tb_][1, 3]), 0, None)
        # one swing foot at a time: the lower foot is the stance foot (smooth gate), swing lift capped
        gate = p['stanceGateM']
        lifts = {}
        for side, other in (('Left', 'Right'), ('Right', 'Left')):
            g = smooth((raw[side] - raw[other]) / gate + 0.5)
            lifts[side] = np.minimum(raw[side], p['ankleLiftCapM']) * g
        for side in ('Left', 'Right'):
            up, kn, ft, tb = rig.idx(side + 'UpLeg'), rig.idx(side + 'Leg'), rig.idx(side + 'Foot'), rig.idx(side + 'ToeBase')
            ank_rest = rest[ft][1, 3]; toe_rest = rest[tb][1, 3]
            a0 = W0[ft][:, :3, 3]; tb0 = W0[tb][:, :3, 3]
            lift = lifts[side]
            lift = np.where(lift < p['plantedLiftM'], 0.0, lift)
            tgt = a0.copy(); tgt[:, 1] = ank_rest + lift
            hip_j = Wd[up][:, :3, 3]
            # keep the stride: pull XZ in only when out of reach, and cap the distance from the hips
            L1 = np.linalg.norm(Wd[kn][:, :3, 3] - Wd[up][:, :3, 3], axis=1); L2 = np.linalg.norm(Wd[ft][:, :3, 3] - Wd[kn][:, :3, 3], axis=1)
            for t in range(T):
                if abs(yaw_delta[t]) > 1e-6:
                    dd = tgt[t] - hip_j[t]; dd = axang([0, 1, 0], yaw_delta[t]) @ dd; tgt[t] = hip_j[t] + dd
                d = tgt[t] - hip_j[t]; dxz = np.array([d[0], d[2]]); n = np.linalg.norm(dxz)
                dmax = np.sqrt(max((0.985 * (L1[t] + L2[t])) ** 2 - d[1] ** 2, 0.0))
                dmax = min(dmax, p['maxStrideM'] + 0.0)
                # distance measured from the hips joint column; cap it
                if n > dmax and n > 1e-6:
                    k = dmax / n; tgt[t, 0] = hip_j[t, 0] + d[0] * k; tgt[t, 2] = hip_j[t, 2] + d[2] * k
            # pole = clip's own knee direction, fallback forward
            kn0 = W0[kn][:, :3, 3]; mid = 0.5 * (hip_j + tgt)
            pole = kn0 - mid
            pole[:, 1] *= 0.0
            fwd = np.tile([0, 0, 1.0], (T, 1))
            pole = np.where(np.linalg.norm(pole, axis=1, keepdims=True) > 0.02, pole, fwd)
            # blend towards "forward" a bit so a hip-yawed clip cannot fold the knee sideways
            pole = unit(unit(pole) * 0.5 + fwd * 0.5)
            R_ft0 = rotpart(W0[ft]).copy()
            for t in range(T):
                if abs(yaw_delta[t]) > 1e-6: R_ft0[t] = axang([0, 1, 0], yaw_delta[t]) @ R_ft0[t]
            two_bone(rig, rot, hipt, up, kn, ft, tgt, pole, W=Wd)
            # foot orientation: the clip's own world rotation (yaw + pitch), levelled to the rest pitch when planted
            Rrest = rotpart(rest[ft][None])[0]
            fw = lambda R: unit((R @ np.array([0, 1.0, 0]))) if False else None
            Rf = np.zeros((T, 3, 3))
            for t in range(T):
                # yaw of the foot = heading of its toe direction (rest foot toe vector in foot-local space)
                toe_loc = rig.t0[tb]; toe_loc = unit(toe_loc)
                v0 = R_ft0[t] @ toe_loc; vr = Rrest @ toe_loc
                yaw0 = np.arctan2(v0[0], v0[2]); yawr = np.arctan2(vr[0], vr[2])
                if p['footYawMaxDeg'] > 0:   # toe direction stays within +-footYawMaxDeg of the (capped) hips heading
                    dd_ = (yaw0 - hd_new[t] + np.pi) % (2 * np.pi) - np.pi; yaw0 = hd_new[t] + float(np.clip(dd_, -np.radians(p['footYawMaxDeg']), np.radians(p['footYawMaxDeg'])))
                Rflat = axang([0, 1, 0], yaw0 - yawr) @ Rrest
                w = smooth(lift[t] / max(1e-6, p['footFlatBlendM']))
                # slerp-free blend: nlerp on the quaternions
                qf, qo = m2q(Rflat), m2q(R_ft0[t])
                if np.dot(qf, qo) < 0: qo = -qo
                qq = qf * (1 - w) + qo * w; qq /= np.linalg.norm(qq)
                Rf[t] = q2m(qq[None])[0]
            set_world_rot(rig, rot, hipt, ft, Rf)
    # --- 4 blade lead + 5 hand height: arm
    ra = {'arm': rig.idx('RightArm'), 'fore': rig.idx('RightForeArm'), 'hand': rig.idx('RightHand'), 'mid': rig.idx('RightHandMiddle1')}
    def _win(t0, t1, bl):
        return smooth((times - (t0 - bl)) / max(bl, 1e-6)) * smooth(((t1 + bl) - times) / max(bl, 1e-6))
    if p['enableHeight'] and (p['contactHandY'] > 0 or p['handClamp']):
        Wd = rig.fk(rot, hipt)
        hand = Wd[ra['hand']][:, :3, 3].copy()
        ci = int(np.argmin(abs(times - contact)))
        tgt = hand.copy()
        if p['contactHandY'] > 0:
            # shift = how far the hand at contact is from the wanted height, applied with a smooth window around the contact time
            dy = p['contactHandY'] - hand[ci, 1]
            dy = float(np.clip(dy, -p['contactHandMaxShiftM'], p['contactHandMaxShiftM']))
            wgt = smooth(1 - abs(times - contact) / p['contactHandBlendS'])
            tgt[:, 1] += dy * wgt
        for hc in p['handClamp']:   # keep the hand inside [yMin, yMax] during [t0, t1] (clip seconds)
            w = _win(hc['t0'], hc['t1'], hc.get('blend', 0.08))
            if hc.get('yMax') is not None: tgt[:, 1] = np.where(tgt[:, 1] > hc['yMax'], tgt[:, 1] + (hc['yMax'] - tgt[:, 1]) * w, tgt[:, 1])
            if hc.get('yMin') is not None: tgt[:, 1] = np.where(tgt[:, 1] < hc['yMin'], tgt[:, 1] + (hc['yMin'] - tgt[:, 1]) * w, tgt[:, 1])
        # pole: elbow stays where it is (down/out), plus a little outward
        elb = Wd[ra['fore']][:, :3, 3]; sh = Wd[ra['arm']][:, :3, 3]
        pole = elb - 0.5 * (sh + hand)
        pole = unit(pole)
        Wd2 = Wd
        two_bone(rig, rot, hipt, ra['arm'], ra['fore'], ra['hand'], tgt, pole, W=Wd2)
        # keep the hand orientation (world) as before the arm IK
        set_world_rot(rig, rot, hipt, ra['hand'], rotpart(Wd[ra['hand']]))
    if p['enableBlade']:
        for it in range(2):
            Wd = rig.fk(rot, hipt)
            hand = Wd[ra['hand']][:, :3, 3]; mid = Wd[ra['mid']][:, :3, 3]
            axis = unit(mid - hand); vh = np.gradient(hand, axis=0) / np.gradient(times)[:, None]
            # smoothed velocity
            vs = np.zeros_like(vh)
            for t in range(T):
                m = abs(times - times[t]) <= 0.06 + 1e-6; vs[t] = vh[m].mean(0)
            Rh_ = rotpart(Wd[ra['hand']]).copy(); changed = False
            for t in range(T):
                w = smooth(1 - max(0.0, abs(times[t] - contact) - p['bladeWindowS']) / p['bladeBlendS'])
                if w <= 0 or np.linalg.norm(vs[t]) < 0.4: continue
                ang = angle(axis[t], vs[t])
                if ang <= p['bladeMaxLeadDeg']: continue
                # rotate the blade axis towards the hand velocity by (ang - max) and by at most bladeWristMaxDeg
                need = min(np.radians(ang - p['bladeMaxLeadDeg']), np.radians(p['bladeWristMaxDeg'])) * w
                ax = np.cross(axis[t], unit(vs[t]))
                if np.linalg.norm(ax) < 1e-8: continue
                Rh_[t] = axang(ax, need) @ Rh_[t]; changed = True
            if changed: set_world_rot(rig, rot, hipt, ra['hand'], Rh_)
        if p['bladeForearmMaxDeg'] > 0:   # blade follows the forearm through the cut
            Wd = rig.fk(rot, hipt)
            hand = Wd[ra['hand']][:, :3, 3]; mid = Wd[ra['mid']][:, :3, 3]; elb = Wd[ra['fore']][:, :3, 3]
            axis = unit(mid - hand); fa = unit(hand - elb); Rh_ = rotpart(Wd[ra['hand']]).copy(); changed = False
            for t in range(T):
                w = smooth(1 - max(0.0, abs(times[t] - contact) - p['bladeWindowS']) / p['bladeBlendS'])
                ang = angle(axis[t], fa[t])
                if w <= 0 or ang <= p['bladeForearmMaxDeg']: continue
                ax = np.cross(axis[t], fa[t])
                if np.linalg.norm(ax) < 1e-8: continue
                Rh_[t] = axang(ax, np.radians(ang - p['bladeForearmMaxDeg']) * w) @ Rh_[t]; changed = True
            if changed: set_world_rot(rig, rot, hipt, ra['hand'], Rh_)
        for aim in p['bladeAim']:   # explicit blade direction (character frame: +Z forward, +Y up) in [t0, t1]
            Wd = rig.fk(rot, hipt)
            hand = Wd[ra['hand']][:, :3, 3]; mid = Wd[ra['mid']][:, :3, 3]
            axis = unit(mid - hand); Rh_ = rotpart(Wd[ra['hand']]).copy(); want = unit(np.array(aim['axis'], float))
            w = _win(aim['t0'], aim['t1'], aim.get('blend', 0.08)) * float(aim.get('weight', 1.0)); mx = np.radians(aim.get('maxDeg', 110.0))
            for t in range(T):
                if w[t] <= 1e-4: continue
                ang = np.radians(angle(axis[t], want)); ax = np.cross(axis[t], want)
                if np.linalg.norm(ax) < 1e-8: continue
                Rh_[t] = axang(ax, min(ang, mx) * w[t]) @ Rh_[t]
            set_world_rot(rig, rot, hipt, ra['hand'], Rh_)
    if log is not None:
        log['hipsTilt0Max'] = float(max(tilt0))
    return rot, hipt


def hips_to_bake(hipt):
    return hipt


# ------------------------------------------------------------------ CLI (metrics table)
def main():
    import bake_attack_clips as B
    mode = sys.argv[1] if len(sys.argv) > 1 else 'metrics'
    cfgp = os.path.join(ROOT, 'Assets', 'Data', 'attack_clips.json')
    cfg = json.load(open(cfgp, encoding='utf-8-sig'))
    ent = cfg['bake'][0]
    j, b = B.load(os.path.join(ROOT, ent['baseGlb']))
    rig = Rig(j); rest = rig.rest_world()
    rows = {}
    for r in cfg['rows']:
        if r.get('character') == 'Dracula': rows.setdefault(r['clip'], r)
    res = {}
    for c in ent['clips']:
        times, rot, hipt, ratio = retarget(rig, None, c['source'], B)
        contact = rows[c['state']]['contactTime']
        hp = inplace(hipt)
        m0, _ = metrics(rig, times, rot, hp, contact, rest)
        params = dict(cfg.get('postureDefaults', {})); params.update(c.get('posture', {}))
        rot2, hip2 = fix(rig, times, rot, hipt, contact, params, rest)
        m1, _ = metrics(rig, times, rot2, hip2, contact, rest)
        res[c['state']] = {'before': m0, 'after': m1, 'contact': contact}
    keys = ['hipsPitchFwdMaxDeg', 'hipsDropM', 'airborneFrames', 'torsoLeanMaxDeg', 'torsoLeanAtContactDeg', 'bladeLeadAngleDeg', 'tipAheadOfHandM', 'handY', 'handMinusShoulderY', 'tipY', 'tipFwdOfHipsM']
    print('%-12s %-6s ' % ('clip', 'when') + ' '.join('%10s' % k[:10] for k in keys))
    for s, v in res.items():
        for w in ('before', 'after'):
            print('%-12s %-6s ' % (s, w) + ' '.join('%10.2f' % v[w][k] for k in keys))
    json.dump(res, open(os.path.join(HERE, 'attack_posture_report.json'), 'w'), indent=1)


if __name__ == '__main__':
    main()

"""BOWART bow_pose.py: pose solver for the bow clips (numpy only, CPU only, no Blender, no Unity).
Works on any Mixamo-106 glb (character id and glb come from the data, nothing is named here except Mixamo bone names).
A pose = local quaternions + local translations over the glb node tree. FK/IK in the glb's own world frame (y up, character faces +Z, its left is +X).
Everything numeric comes from Assets/Data/bow_clips.json (stance, anchors, finger curls, key times)."""
import math, sys, os
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
for _p in (HERE, os.path.join(HERE, '..', '..', '..', '..', 'Tools'), os.path.join(HERE, '..', '..', '..', 'Tools')):
    if os.path.exists(os.path.join(_p, 'clip_lib.py')):
        sys.path.insert(0, os.path.abspath(_p)); break
import clip_lib as L

MX = 'mixamorig:'


def unit(v):
    n = np.linalg.norm(v)
    return v / n if n > 1e-12 else v


def rotY(deg):
    a = math.radians(deg); c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


def rotAxis(axis, deg):
    return L.q2m(L.qaxis(axis, deg))


def m2q(m):
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0:
        s = math.sqrt(t + 1.0) * 2; q = [(m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, 0.25 * s]
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2; q = [0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s, (m[2, 1] - m[1, 2]) / s]
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2; q = [(m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s, (m[0, 2] - m[2, 0]) / s]
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2; q = [(m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s, (m[1, 0] - m[0, 1]) / s]
    q = np.array(q, float); return q / np.linalg.norm(q)


def arc(a, b):
    """minimal rotation matrix taking direction a to direction b"""
    a = unit(a); b = unit(b); c = float(np.dot(a, b))
    if c > 1 - 1e-9: return np.eye(3)
    if c < -1 + 1e-9:
        ax = unit(np.cross(a, [1, 0, 0]) if abs(a[0]) < 0.9 else np.cross(a, [0, 1, 0])); return rotAxis(ax, 180.0)
    ax = np.cross(a, b); s = np.linalg.norm(ax); ang = math.degrees(math.atan2(s, c))
    return rotAxis(ax / s, ang)


def twist_deg(R, axis):
    """swing-twist: twist angle (deg, -180..180) of rotation matrix R about unit axis"""
    q = m2q(R); d = float(np.dot(q[:3], axis)); a = math.degrees(2 * math.atan2(d, q[3]))
    return (a + 180.0) % 360.0 - 180.0


def roll_minswing(Rr_local, Rl):
    """roll (deg) of a bone about its OWN axis against its parent, measured from the minimal-swing orientation: the bone axis is first swung straight from its rest direction to where it points now
    (shortest arc), what is left is the roll about the new axis. Rr_local / Rl = rest and current local rotation matrices. Used for the shoulder (Arm vs Shoulder), where the bone swings a long way
    (a plain swing-twist about the rest axis has a branch cut there and jumps by 180 deg for a small motion)."""
    d0 = Rr_local[:, 1]; d1 = Rl[:, 1]
    M = Rl @ Rr_local.T @ arc(d0, d1).T
    return twist_deg(M, d1)


def frame(a, n):
    """right-handed frame, columns a, n (made perpendicular to a), a x n"""
    a = unit(np.asarray(a, float)); n = np.asarray(n, float); n = unit(n - a * float(np.dot(n, a)))
    return np.stack([a, n, np.cross(a, n)], 1)


def signed_angle(a, b, axis):
    """signed angle (deg) about axis from a to b (both projected perpendicular to the axis)"""
    axis = unit(axis); a = a - axis * float(np.dot(a, axis)); b = b - axis * float(np.dot(b, axis))
    if np.linalg.norm(a) < 1e-9 or np.linalg.norm(b) < 1e-9: return 0.0
    return math.degrees(math.atan2(float(np.dot(axis, np.cross(a, b))), float(np.dot(a, b))))


def smooth(x):
    x = min(1.0, max(0.0, x)); return x * x * (3 - 2 * x)


class Body:
    """one character glb: rest data + a mutable pose"""
    def __init__(self, glb):
        self.j, self.b = L.load(glb); self.R = L.Rig(self.j, self.b)
        self.n = len(self.R.nodes); self.idx = self.R.idx
        self.rest_world = self.R.world(None, 0.0)
        self.T = None; self.Q = None; self._W = None; self.roll_prev = {}

    def bone(self, s): return self.idx[MX + s]

    def clips(self): return list(self.R.anims.keys())

    def load_base(self, state, t):
        T, Q = self.R.pose(state, t); self.T = [np.array(x, float) for x in T]; self.Q = [np.array(x, float) for x in Q]; self.Q0 = [q.copy() for q in self.Q]; self._W = None
        self.base_world = self.fk()

    def fk(self):
        if self._W is not None: return self._W
        W = {}
        for i in self.R.order:
            Rm = L.q2m(self.Q[i]); p = self.R.parent.get(i)
            if p is None: W[i] = (Rm, np.array(self.T[i], float))
            else: Rp, tp = W[p]; W[i] = (Rp @ Rm, tp + Rp @ self.T[i])
        self._W = W; return W

    def pos(self, name): return self.fk()[self.bone(name)][1]

    def rot(self, name): return self.fk()[self.bone(name)][0]

    def set_world_rot(self, name, Rw):
        i = self.bone(name); p = self.R.parent.get(i); W = self.fk()
        Rp = W[p][0] if p is not None else np.eye(3)
        self.Q[i] = m2q(Rp.T @ Rw); self._W = None

    def set_world_pos(self, name, pw):
        i = self.bone(name); p = self.R.parent.get(i); W = self.fk()
        if p is None: self.T[i] = np.array(pw, float)
        else: Rp, tp = W[p]; self.T[i] = Rp.T @ (np.array(pw, float) - tp)
        self._W = None

    def rot_world(self, name, R_add):
        """premultiply a world rotation matrix onto a bone (children follow)"""
        self.set_world_rot(name, R_add @ self.rot(name))

    # ---------------------------------------------------------------- twist-free two bone chains (BOWART v2)
    def hinge_rest(self, bone, flex_dir):
        """rest-pose bone axis y_r and hinge axis h_r (world, T-pose rest). +rotation about h_r flexes the bone towards flex_dir (arms: forward, knees: backward)."""
        Rr = self.rest_world[self.bone(bone)][0]; y = Rr[:, 1].copy()
        h = unit(np.cross(y, [0.0, 0.0, 1.0]))
        if float(np.dot(np.cross(h, y), flex_dir)) < 0: h = -h
        return y, h

    def local_roll(self, name):
        """shoulder roll: see roll_minswing"""
        i = self.bone(name); return roll_minswing(L.q2m(self.R.rq[i]), L.q2m(self.Q[i]))

    def local_swing(self, name):
        """how far (deg) the bone axis is swung away from its rest direction in its parent frame (the shoulder roll is only meaningful below ~135 deg: past that the arm points back across the body)"""
        i = self.bone(name); d0 = L.q2m(self.R.rq[i])[:, 1]; d1 = L.q2m(self.Q[i])[:, 1]
        return math.degrees(math.acos(max(-1.0, min(1.0, float(np.dot(d0, d1))))))

    def local_twist(self, name):
        """twist (deg, about the bone's own local Y axis) of the bone's local rotation relative to its REST local rotation: the roll this bone has against its parent
        (candy-wrapper measure: 0 = the skin of the two bones lines up like in the T-pose)"""
        i = self.bone(name); Rl = L.q2m(self.Q[i]); Rr = L.q2m(self.R.rq[i])
        return twist_deg(Rr.T @ Rl, np.array([0.0, 1.0, 0.0]))

    def chain_geom(self, root, mid, tip, target, pole, psi=0.0):
        """two bone geometry. returns elbow E, tip T (clamped to reach), unit bone directions u1,u2 and the hinge axis m (rotation m by +bend takes u1 to u2)."""
        S = self.pos(root); E0 = self.pos(mid); W0 = self.pos(tip)
        l1 = float(np.linalg.norm(E0 - S)); l2 = float(np.linalg.norm(W0 - E0))
        d_t = np.asarray(target, float) - S; dist = float(np.linalg.norm(d_t)); u = d_t / max(dist, 1e-9)
        d = min(max(dist, abs(l1 - l2) + 1e-4), l1 + l2 - 1e-4)
        a = (l1 * l1 + d * d - l2 * l2) / (2 * d); h = math.sqrt(max(l1 * l1 - a * a, 0.0))
        pole = np.asarray(pole, float); pp = pole - u * float(np.dot(pole, u))
        pp = unit(pp) if np.linalg.norm(pp) > 1e-6 else unit(np.cross(u, [0, 1.0, 0]))
        if psi: pp = rotAxis(u, psi) @ pp
        E = S + u * a + pp * h; T = S + u * d
        return {'S': S, 'E': E, 'T': T, 'u1': unit(E - S), 'u2': unit(T - E), 'm': unit(np.cross(pp, u)), 'l1': l1, 'l2': l2, 'pp': pp, 'err': float(dist - d)}

    def chain_rots(self, root, mid, g, flex_dir):
        """world rotations of the two bones for the geometry g: the bone axes follow u1/u2 and BOTH share the hinge axis m, so the mid bone is a pure hinge (no roll between the two bones except the rest offset)"""
        out = []
        for nm, u in ((root, g['u1']), (mid, g['u2'])):
            yr, hr = self.hinge_rest(nm, flex_dir)
            out.append(frame(u, g['m']) @ frame(yr, hr).T @ self.rest_world[self.bone(nm)][0])
        return out

    def place_chain(self, root, mid, g, flex_dir, roll_mid_deg=0.0):
        R1, R2 = self.chain_rots(root, mid, g, flex_dir)
        if roll_mid_deg: R2 = rotAxis(g['u2'], roll_mid_deg) @ R2
        self.set_world_rot(root, R1); self.set_world_rot(mid, R2)

    # ---------------------------------------------------------------- fingers
    def reset_fingers(self, side):
        pre = MX + side + 'Hand'
        for i, nd in enumerate(self.R.nodes):
            nm = nd.get('name', '')
            if nm.startswith(pre) and nm != pre: self.Q[i] = self.Q0[i].copy()
        self._W = None

    def curl(self, chain, angles_deg, axis_w):
        """rotate the joints of a finger chain about a fixed physical axis (rest-pose frame) by the given angles"""
        for name, ang in zip(chain, angles_deg):
            i = self.bone(name); a_loc = self.rest_world[i][0].T @ axis_w
            self.Q[i] = L.qmul(self.Q[i], L.qaxis(a_loc, ang)); self._W = None


def euler_zxy_deg(Rm):
    """Unity Quaternion.Euler(x,y,z) = Ry * Rx * Rz (matrix form); returns x,y,z degrees"""
    sx = -Rm[1, 2]; x = math.asin(max(-1, min(1, sx)))
    if abs(sx) < 0.99999:
        y = math.atan2(Rm[0, 2], Rm[2, 2]); z = math.atan2(Rm[1, 0], Rm[1, 1])
    else:
        y = math.atan2(-Rm[2, 0], Rm[0, 0]); z = 0.0
    return [math.degrees(x), math.degrees(y), math.degrees(z)]


def euler_to_m(x, y, z):
    return rotAxis([0, 1, 0], y) @ rotAxis([1, 0, 0], x) @ rotAxis([0, 0, 1], z)


# ====================================================================== BOWART v2: arm solver (twist-free, data driven)
FLEX_ARM = np.array([0.0, 0.0, 1.0])      # elbow flexes the forearm towards the front of the T-pose character
FLEX_LEG = np.array([0.0, 0.0, -1.0])     # knee flexes the shin backwards


def two_bone(S, l1, l2, target, pole, psi=0.0):
    """two bone geometry on its own (no body): elbow E, tip T (clamped to reach), unit bone directions u1,u2, hinge axis m (rotation m by +bend takes u1 to u2)"""
    d_t = np.asarray(target, float) - S; dist = float(np.linalg.norm(d_t)); u = d_t / max(dist, 1e-9)
    d = min(max(dist, abs(l1 - l2) + 1e-4), l1 + l2 - 1e-4)
    a = (l1 * l1 + d * d - l2 * l2) / (2 * d); h = math.sqrt(max(l1 * l1 - a * a, 0.0))
    pole = np.asarray(pole, float); pp = pole - u * float(np.dot(pole, u))
    pp = unit(pp) if np.linalg.norm(pp) > 1e-6 else unit(np.cross(u, [0, 1.0, 0]))
    if psi: pp = rotAxis(u, psi) @ pp
    E = S + u * a + pp * h; T = S + u * d
    return {'S': S, 'E': E, 'T': T, 'u1': unit(E - S), 'u2': unit(T - E), 'm': unit(np.cross(pp, u)), 'l1': l1, 'l2': l2, 'pp': pp, 'err': float(dist - d)}


class ArmCtx:
    """everything one arm solve needs, captured from the body at that moment so the elbow swivel psi can be chosen later (dynamic programming over a whole clip)"""
    def __init__(s, body, side, target, pole, elbow_target, f_t, n_t, ac, neutral=0.0):
        s.side = side; s.arm, s.fore, s.hand = side + 'Arm', side + 'ForeArm', side + 'Hand'
        ia, ifo, iha = body.bone(s.arm), body.bone(s.fore), body.bone(s.hand)
        s.Rp = body.fk()[body.R.parent[ia]][0].copy()
        s.Rr_arm_l = L.q2m(body.R.rq[ia]); s.Rr_fore_l = L.q2m(body.R.rq[ifo]); s.Rr_hand_l = L.q2m(body.R.rq[iha]); Rr_hand = body.rest_world[iha][0]
        s.palm_loc = Rr_hand.T @ np.array([0, -1.0, 0])
        s.S = body.pos(s.arm).copy(); E0 = body.pos(s.fore); W0 = body.pos(s.hand)
        s.l1 = float(np.linalg.norm(E0 - s.S)); s.l2 = float(np.linalg.norm(W0 - E0))
        s.target = np.asarray(target, float); s.pole = np.asarray(pole, float); s.et = None if elbow_target is None else np.asarray(elbow_target, float)
        s.f_t = np.asarray(f_t, float); s.n_t = np.asarray(n_t, float); s.ac = ac; s.neutral = neutral
        s.flex = FLEX_ARM; s.rest = {nm: body.hinge_rest(nm, FLEX_ARM) for nm in (s.arm, s.fore)}
        s.Rr = {nm: body.rest_world[body.bone(nm)][0] for nm in (s.arm, s.fore)}

    def rots(s, g):
        R1 = frame(g['u1'], g['m']) @ frame(*s.rest[s.arm]).T @ s.Rr[s.arm]
        R20 = R1 @ s.Rr_fore_l                                   # forearm = arm * its rest local rotation, then swung (axis perpendicular to its own axis = zero twist) onto the forearm direction
        return R1, R20, arc(R20[:, 1], g['u2']) @ R20

    def hand_swing(s, R2, g):
        R_h0 = R2 @ s.Rr_hand_l
        f_e = unit(s.f_t * (1.0 - s.neutral) + g['u2'] * s.neutral) if s.neutral > 0 else s.f_t   # neutral = share of the wrist kept straight
        R_h1 = arc(R_h0[:, 1], f_e) @ R_h0
        return f_e, R_h1

    def eval(s, psi, prev_roll=None):
        ac = s.ac; capF, capH = ac['forearmRollMaxDeg'], ac['handRollMaxDeg']; share = ac.get('forearmRollShare', 0.5)
        g = two_bone(s.S, s.l1, s.l2, s.target, s.pole, psi)
        R1, R20, R2 = s.rots(g)
        twA = roll_minswing(s.Rr_arm_l, s.Rp.T @ R1)
        f_e, R_h1 = s.hand_swing(R2, g)
        phi = signed_angle(R_h1 @ s.palm_loc, s.n_t, f_e)
        if abs(phi) > 150 and prev_roll and phi * prev_roll < 0: phi -= 360.0 * math.copysign(1.0, phi)      # near 180 deg either roll direction is as bad: keep the branch of the previous frame
        p = max(-capF, min(capF, phi * share)); q = max(-capH, min(capH, phi - p))
        resid = abs(phi - p - q)
        c = ac['wPalm'] * resid * resid + ac['wArmTwist'] * max(0.0, abs(twA) - ac['armTwistSoftDeg']) ** 2 + ac['wPsi'] * psi * psi
        if s.et is not None: c += ac['wElbow'] * (float(np.linalg.norm(g['E'] - s.et)) * 100.0) ** 2
        return c, g, twA, phi, p, q, resid, f_e

    def cost(s, psi): return s.eval(psi)[0]

    def apply(s, body, psi, prev_roll=None):
        c, g, twA, phi, p, q, resid, f_e = s.eval(psi, prev_roll)
        R1, R20, R2 = s.rots(g); R2 = rotAxis(g['u2'], p) @ R2
        R_h0 = R2 @ s.Rr_hand_l; R_h1 = arc(R_h0[:, 1], f_e) @ R_h0; R_h = rotAxis(f_e, q) @ R_h1
        body.set_world_rot(s.arm, R1); body.set_world_rot(s.fore, R2); body.set_world_rot(s.hand, R_h)
        return {'psi': float(psi), 'cost': c, 'elbow': g['E'], 'wrist': g['T'], 'reachErr': g['err'], 'palmResidDeg': resid, 'palmPhiDeg': phi, 'foreRollDeg': p, 'handRollDeg': q, 'S': s.S, 'ctx': s}


PSI_GRID = np.arange(-180.0, 180.0, 5.0)


def solve_arm(body, side, target, pole, elbow_target, f_t, n_t, ac, psi=None, prev_psi=None, prev_roll=None, neutral=0.0):
    """Place Arm / ForeArm / Hand of one side. Everything is a pure hinge chain (zero roll between Arm and ForeArm, ForeArm and Hand except what is added on purpose), so the only roll the arm needs
    comes from the swivel psi of the elbow around the shoulder-wrist axis. psi = given (dynamic programming result) or searched near prev_psi / globally with the weights ac['w*']:
    keep the elbow near elbow_target, turn the palm to n_t using at most ac['forearmRollMaxDeg'] + ac['handRollMaxDeg'] of roll, keep the shoulder roll under ac['armTwistSoftDeg'].
    f_t = wanted finger direction (world), n_t = wanted palm normal (world), neutral = share of the wrist kept straight."""
    ctx = ArmCtx(body, side, target, pole, elbow_target, f_t, n_t, ac, neutral)
    if psi is None:
        if prev_psi is None: cands = PSI_GRID
        else: cands = np.arange(prev_psi - ac.get('psiStepWindowDeg', 14.0), prev_psi + ac.get('psiStepWindowDeg', 14.0) + 0.1, 3.0)
        best = min(cands, key=ctx.cost)
        psi = min(np.arange(best - 3.0, best + 3.01, 0.5), key=ctx.cost)
    return ctx.apply(body, psi, prev_roll)


def _ang_matrix(A, B):
    """geodesic angle (deg) between every rotation of A (G,3,3) and every rotation of B (G,3,3) -> (G,G)"""
    t = A.reshape(len(A), 9) @ B.reshape(len(B), 9).T
    return np.degrees(np.arccos(np.clip((t - 1.0) / 2.0, -1.0, 1.0)))


def dp_psi(ctxs, w_trans):
    """pick the swivel psi of every frame of a clip with dynamic programming: sum of the per-frame costs + w_trans * (how far the Arm, ForeArm and Hand bones actually ROTATE between two
    frames, geodesic angle squared). So the arm takes the short way round: no full turns about the shoulder-wrist axis, no roll flips through 180 deg. returns the psi list."""
    n = len(ctxs); G = len(PSI_GRID)
    local = np.zeros((n, G)); rots = []
    for k, c in enumerate(ctxs):
        R1s = np.zeros((G, 3, 3)); R2s = np.zeros((G, 3, 3)); Rhs = np.zeros((G, 3, 3))
        for i, psi in enumerate(PSI_GRID):
            cst, g, twA, phi, p, q, resid, f_e = c.eval(psi)
            R1, R20, R2 = c.rots(g); R2 = rotAxis(g['u2'], p) @ R2
            R_h0 = R2 @ c.Rr_hand_l; R_h = rotAxis(f_e, q) @ (arc(R_h0[:, 1], f_e) @ R_h0)
            local[k, i] = cst; R1s[i] = R1; R2s[i] = R2; Rhs[i] = R_h
        rots.append((R1s, R2s, Rhs))
    acc = local[0].copy(); back = np.zeros((n, G), int)
    for k in range(1, n):
        a = rots[k - 1]; b = rots[k]
        T = w_trans * (_ang_matrix(a[0], b[0]) ** 2 + _ang_matrix(a[1], b[1]) ** 2 + _ang_matrix(a[2], b[2]) ** 2)      # [from, to]
        tot = acc[:, None] + T
        back[k] = np.argmin(tot, axis=0); acc = local[k] + tot[back[k], np.arange(G)]
    idx = [int(np.argmin(acc))]
    for k in range(n - 1, 0, -1): idx.append(int(back[k][idx[-1]]))
    idx = idx[::-1]
    out = []
    for k, i in enumerate(idx):                              # refine each frame by 0.5 deg around its grid value
        out.append(float(min(np.arange(PSI_GRID[i] - 2.5, PSI_GRID[i] + 2.51, 0.5), key=ctxs[k].cost)))
    return out


def twists(body, side):
    """roll of each bone of the arm chain against its parent, deg about the bone axis: shoulder (Shoulder->Arm), elbow (Arm->ForeArm), wrist (ForeArm->Hand)"""
    return {'shoulderSwing': body.local_swing(side + 'Arm'), 'shoulder': body.local_roll(side + 'Arm'), 'elbow': body.local_twist(side + 'ForeArm'), 'wrist': body.local_twist(side + 'Hand')}


def aim_frame(sgn_bow, aim_yaw_deg, aim_pitch_deg=0.0):
    """aim frame in the glb world: A = horizontal direction to the target, U = up, F = the way the archer's chest faces (bow hand side towards the target).
    data vectors [along A, up, front F] -> world with w(v)."""
    A = rotY(aim_yaw_deg) @ np.array([0, 0, 1.0]); U = np.array([0, 1.0, 0]); F = sgn_bow * np.cross(A, U)
    Ap = A * math.cos(math.radians(aim_pitch_deg)) + U * math.sin(math.radians(aim_pitch_deg))
    def w(v): return A * v[0] + U * v[1] + F * v[2]
    return {'A': A, 'U': U, 'F': F, 'Ap': Ap, 'w': w}


def yaw_deg(v): return math.degrees(math.atan2(v[0], v[2]))


def wrap_deg(a): return (a + 180.0) % 360.0 - 180.0


def knee_medial(body):
    """largest inward deviation (m) of a knee from the hip-ankle line, towards the other leg, in the pelvis frame (the same quantity as Tools/clip_gates.knee_e_frame: knock-knees > 0.02 m fail)"""
    lat = body.rot('Hips') @ np.array([1.0, 0, 0]); worst = -9.0
    for side, other in (('Left', 'Right'), ('Right', 'Left')):
        Pu, Pl, Pf, Po = body.pos(side + 'UpLeg'), body.pos(side + 'Leg'), body.pos(side + 'Foot'), body.pos(other + 'UpLeg')
        med = lat if float(np.dot(Po - Pu, lat)) > 0 else -lat
        u = unit(Pf - Pu); p = (Pl - Pu) - float(np.dot(Pl - Pu, u)) * u
        worst = max(worst, float(np.dot(p, med)))
    return worst

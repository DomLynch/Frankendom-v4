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

    # ---------------------------------------------------------------- two bone IK
    def ik2(self, root, mid, tip, target, pole, tip_R=None, twist_split=0.5, bone_axis=(0, 1, 0)):
        """place tip joint at target (clamped to reach), mid joint toward pole. tip_R = world rotation of the tip bone afterwards (None keeps its world rotation).
        The roll of the two bones is resolved by coordinate descent so the twist of mid vs root and tip vs mid stays small (twist_split = share the mid bone takes)."""
        r, m, t = self.bone(root), self.bone(mid), self.bone(tip)
        W = self.fk(); S = W[r][1].copy(); E = W[m][1].copy(); Wp = W[t][1].copy()
        l1 = float(np.linalg.norm(E - S)); l2 = float(np.linalg.norm(Wp - E))
        keepR = self.rot(tip) if tip_R is None else tip_R
        d_t = target - S; dist = float(np.linalg.norm(d_t)); u = d_t / max(dist, 1e-9)
        d = min(max(dist, abs(l1 - l2) + 1e-4), l1 + l2 - 1e-4)
        a = (l1 * l1 + d * d - l2 * l2) / (2 * d); h = math.sqrt(max(l1 * l1 - a * a, 0.0))
        pp = pole - u * float(np.dot(pole, u)); pp = unit(pp) if np.linalg.norm(pp) > 1e-6 else unit(np.cross(u, [0, 1, 0]))
        E2 = S + u * a + pp * h; T2 = S + u * d
        # analytic bone frames: both bones keep, relative to the elbow hinge plane, the same orientation they have in the base (idle) pose, so the
        # joint bends about its natural hinge axis and the solution is continuous (no roll search, no flips). hinge axis n = plane normal (pole side).
        u1 = unit(E2 - S); u2 = unit(T2 - E2); n = unit(np.cross(pp, u))
        bw = self.base_world; Si, Ei, Wi = bw[r][1], bw[m][1], bw[t][1]
        u1i = unit(Ei - Si); u2i = unit(Wi - Ei); ni = np.cross(u1i, u2i)
        ni = unit(ni) if np.linalg.norm(ni) > 1e-4 else n
        def frame(a_, n_): return np.stack([a_, n_, np.cross(a_, n_)], 1)
        self.set_world_rot(root, frame(u1, n) @ frame(u1i, ni).T @ bw[r][0])
        Rm2 = frame(u2, n) @ frame(u2i, ni).T @ bw[m][0]
        self.set_world_rot(mid, Rm2)
        # forearm / shin roll about its own axis (keeps tip position): takes the share of the pronation the tip needs relative to the base pose
        reft = L.q2m(self.Q0[t])
        def geo(Ra): return math.acos(max(-1.0, min(1.0, (np.trace(Ra) - 1.0) / 2.0)))
        def cost(phi): return geo(reft.T @ ((rotAxis(u2, phi) @ Rm2).T @ keepR))
        pv = self.roll_prev.get(root)
        phis = np.arange(-180, 180, 4.0) if pv is None else np.arange(pv - 60, pv + 60.1, 4.0)   # first frame global, later frames stay in the previous well (no 180 degree roll flips)
        phi = float(phis[int(np.argmin([cost(x) for x in phis]))])
        phi = float(min(np.arange(phi - 4, phi + 4.01, 0.5), key=cost))
        self.roll_prev[root] = phi
        self.set_world_rot(mid, rotAxis(u2, phi * twist_split) @ Rm2)
        self.set_world_rot(tip, keepR)
        W = self.fk()
        return float(np.linalg.norm(W[t][1] - target)), (l1, l2)

    # ---------------------------------------------------------------- fingers
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

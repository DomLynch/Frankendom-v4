"""SOBEKTUNE: bake the tail follower (hang + swing + floor) into the clips of a combat/anim glb. PRESENTATION ONLY: it only adds rotation channels for the
tail chain bones (names from tail_spring.json), nothing the sim reads. Reuses laneKAIJUTAIL's TailSpring data (bones, tipLengthM, damping) and its idea (a point
that springs toward the animated tip, gravity, bone length kept), extended with a floor and a state-dependent stiffness because the runtime spring is limited to 18 deg
from the animated direction and so can never let a tail that the death pose throws up fall down again.
usage: python tail_bake.py <in.glb> <out.glb> <tail_spring.json> [--character Sobek] [--chain tail] [--report out.json]
All numbers are in tail_spring.json characters[].chains[].follow (data). Deterministic: no RNG, fixed substeps."""
import sys, os, json, math, argparse
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE); sys.path.insert(1, os.path.join(HERE, 'scratch', 'Tools'))   # live Tools\ (clip_lib, leg_fix, clip_build) or this lane's scratch copy
import numpy as np
import clip_lib as L, leg_fix, clip_build

def unit(v):
    n = np.linalg.norm(v); return v / n if n > 1e-9 else v

def from_to(a, b):
    a = unit(a); b = unit(b); c = np.cross(a, b); d = float(np.dot(a, b))
    if d > 0.999999: return np.eye(3)
    if d < -0.999999:
        ax = unit(np.cross(a, np.array([1.0, 0, 0]) if abs(a[0]) < 0.9 else np.array([0, 1.0, 0]))); return 2 * np.outer(ax, ax) - np.eye(3)
    K = np.array([[0, -c[2], c[1]], [c[2], 0, -c[0]], [-c[1], c[0], 0]]); return np.eye(3) + K + K @ K / (1 + d)

def clamp_dir(anim, d, max_deg):
    ang = math.acos(max(-1, min(1, float(np.dot(anim, d)))))
    if ang <= math.radians(max_deg): return d
    ax = np.cross(anim, d); n = np.linalg.norm(ax)
    if n < 1e-9: return anim
    ax /= n; t = math.radians(max_deg); return anim * math.cos(t) + np.cross(ax, anim) * math.sin(t)

def main():
    ap = argparse.ArgumentParser(); ap.add_argument('glb'); ap.add_argument('out'); ap.add_argument('cfg'); ap.add_argument('--character', required=True); ap.add_argument('--chain', default='tail'); ap.add_argument('--report')
    a = ap.parse_args()
    cfg = json.load(open(a.cfg, encoding='utf-8-sig')); row = [r for r in cfg['characters'] if r['name'] == a.character][0]
    ch = [c for c in row['chains'] if c['name'] == a.chain][0]; F = ch['follow']; W0 = cfg['world']
    g = np.array([W0['gravityDir']['x'], W0['gravityDir']['y'], W0['gravityDir']['z']], float); g = unit(g) * W0['gravityMps2'] * F['gravityMul']
    j, b = L.load(a.glb); rig = L.Rig(j, b)
    ids = [rig.idx[n] for n in ch['bones']]; nb = len(ids); par0 = rig.parent[ids[0]]
    hips = rig.bone('Hips'); name2idx = rig.idx
    if os.environ.get('SOBEKTUNE_DEBUG'): print('chain', ch['bones'], 'parent', rig.nodes[par0].get('name'))
    rep = {'character': a.character, 'bones': ch['bones'], 'clips': {}}
    for an in j['animations']:
        st = an['name']; chn = rig.anims[st]; hk = (hips, 'rotation'); ts = chn[hk][0]; N = len(ts)
        # time accessor index of the Hips rotation sampler (reuse)
        ti = [an['samplers'][c['sampler']]['input'] for c in an['channels'] if c['target']['node'] == hips and c['target']['path'] == 'rotation'][0]
        # animated chain points per frame
        A = np.zeros((N, nb + 1, 3)); Rw = np.zeros((N, nb, 3, 3)); Rpar = np.zeros((N, 3, 3))
        for k, t in enumerate(ts):
            W = rig.world(st, t)
            for i, n in enumerate(ids): A[k, i] = W[n][1]; Rw[k, i] = W[n][0]
            Rpar[k] = W[par0][0]
            d = unit(A[k, nb - 1] - A[k, nb - 2]); A[k, nb] = A[k, nb - 1] + d * ch['tipLengthM']
        Ls = np.linalg.norm(A[0, 1:] - A[0, :-1], axis=1)
        mul = F.get('stateStiffnessMul', {}).get(st, 1.0); loop = st in F.get('loopStates', []); passes = F['prerollPasses'] + 1 if loop else 1
        kroot, ktip = F['stiffnessRoot'] * mul, F['stiffnessTip'] * mul; damp = F['damping']; floor = F['floorM']
        xs = None
        for ps in range(passes):
            X = np.zeros((N, nb + 1, 3)); x = (A[0] if xs is None else xs).copy(); v = np.zeros_like(x); x[0] = A[0, 0]
            for k in range(N):
                if k > 0:
                    dt = ts[k] - ts[k - 1]; n = max(1, int(math.ceil(dt / F['substepS']))); h = dt / n
                    for _ in range(n):
                        xp = x.copy()
                        for i in range(1, nb + 1):
                            u = (i - 1) / max(1, nb - 1); kk = kroot + (ktip - kroot) * u
                            v[i] += ((A[k, i] - x[i]) * kk + g) * h; v[i] *= math.exp(-damp * h); x[i] += v[i] * h
                        for _it in range(F['constraintIters']):
                            x[0] = A[k, 0]
                            for i in range(1, nb + 1):
                                if x[i, 1] < floor: x[i, 1] = floor
                                animd = unit(A[k, i] - A[k, i - 1]); dd = unit(x[i] - x[i - 1])
                                if i == 1: d = clamp_dir(animd, dd, F['maxRootBendDeg'])
                                else:   # bend of this joint relative to the previous segment is limited around the ANIMATED relative bend (keeps the skin smooth: no pinch, no stretch)
                                    pa = unit(A[k, i - 1] - A[k, i - 2]); pn = unit(x[i - 1] - x[i - 2]); d = clamp_dir(from_to(pa, pn) @ animd, dd, F['maxJointBendDeg'])
                                x[i] = x[i - 1] + d * Ls[i - 1]
                        v = (x - xp) / h; sp_ = np.linalg.norm(v, axis=1, keepdims=True); v = v * np.minimum(1.0, F['maxSpeedMps'] / np.maximum(sp_, 1e-9))
                else:
                    x[0] = A[0, 0]
                X[k] = x
            xs = X[-1].copy(); Xf = X
        # chain points -> local rotations
        q = np.zeros((nb, N, 4)); maxdev = 0.0
        for k in range(N):
            Rp = Rpar[k]
            for i in range(nb):
                ad = unit(A[k, i + 1] - A[k, i]); nd = unit(Xf[k, i + 1] - Xf[k, i]); maxdev = max(maxdev, math.degrees(math.acos(max(-1, min(1, float(np.dot(ad, nd)))))))
                Rn = from_to(ad, nd) @ Rw[k, i]; loc = Rp.T @ Rn; qq = np.array(leg_fix.m2q(loc), float)
                if k > 0 and np.dot(qq, q[i, k - 1]) < 0: qq = -qq
                q[i, k] = qq; Rp = Rn
        rep['clips'][st] = {'frames': N, 'maxDeviationDeg': round(maxdev, 1), 'stiffnessMul': mul, 'loop': loop, 'tipMinY': round(float(Xf[:, nb, 1].min()), 3), 'tipMaxY': round(float(Xf[:, nb, 1].max()), 3)}
        for i, n in enumerate(ids):   # replace any existing channel on that bone, else add
            an['channels'] = [c for c in an['channels'] if not (c['target']['node'] == n and c['target']['path'] == 'rotation')]
            ai = clip_build.add_acc(j, b, q[i], 'VEC4')
            an['samplers'].append({'input': ti, 'output': ai, 'interpolation': 'LINEAR'})
            an['channels'].append({'sampler': len(an['samplers']) - 1, 'target': {'node': n, 'path': 'rotation'}})
        print('%-18s frames %3d mul %.2f loop %d maxDev %5.1f deg  tip y %.2f..%.2f' % (st, N, mul, loop, maxdev, Xf[:, nb, 1].min(), Xf[:, nb, 1].max()), flush=True)
    L.save(j, b, a.out)
    if a.report: json.dump(rep, open(a.report, 'w'), indent=1)
    print('wrote', a.out)

if __name__ == '__main__': main()

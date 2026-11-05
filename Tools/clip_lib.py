"""CLIPBUILD shared helpers: GLB io, quaternion maths (xyzw), skeleton FK. numpy only, CPU only. No character names here."""
import json, struct, math
import numpy as np


def load(p):
    d = open(p, 'rb').read()
    n = struct.unpack('<I', d[12:16])[0]
    j = json.loads(d[20:20 + n]); off = 20 + n
    bl = struct.unpack('<I', d[off:off + 4])[0]
    return j, bytearray(d[off + 8:off + 8 + bl])


def save(j, b, p):
    while len(b) % 4: b.append(0)
    j['buffers'] = [{'byteLength': len(b)}]
    js = json.dumps(j).encode()
    while len(js) % 4: js += b' '
    out = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(b))
    out += struct.pack('<I', len(js)) + b'JSON' + js + struct.pack('<I', len(b)) + b'BIN\x00' + bytes(b)
    open(p, 'wb').write(out)


def qmul(a, b):
    """quaternion product a*b, xyzw; a and b broadcast ((4,) or (N,4))"""
    a = np.asarray(a, float); b = np.asarray(b, float)
    ax, ay, az, aw = a[..., 0], a[..., 1], a[..., 2], a[..., 3]
    bx, by, bz, bw = b[..., 0], b[..., 1], b[..., 2], b[..., 3]
    return np.stack([aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx, aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz], -1)


def qinv(q):
    q = np.asarray(q, float); return q * np.array([-1, -1, -1, 1.0])


def qrot(q, v):
    qv = np.asarray(q[:3], float); w = q[3]
    t = 2 * np.cross(qv, v)
    return v + w * t + np.cross(qv, t)


def qaxis(axis, deg):
    a = np.asarray(axis, float); a = a / np.linalg.norm(a); h = math.radians(deg) / 2
    return np.array([*(a * math.sin(h)), math.cos(h)])


def qangle_deg(a, b):
    d = abs(float(np.dot(a, b))) / (np.linalg.norm(a) * np.linalg.norm(b))
    return math.degrees(2 * math.acos(min(1.0, d)))


def q2m(q):
    x, y, z, w = np.asarray(q, float) / np.linalg.norm(q)
    return np.array([[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)], [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)], [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]])


def slerp(a, b, t):
    a = np.asarray(a, float); b = np.asarray(b, float)
    d = float(np.dot(a, b))
    if d < 0: b = -b; d = -d
    if d > 0.9995:
        r = a + t * (b - a); return r / np.linalg.norm(r)
    th = math.acos(d); return (math.sin((1 - t) * th) * a + math.sin(t * th) * b) / math.sin(th)


def acc_data(j, b, ai):
    a = j['accessors'][ai]; bv = j['bufferViews'][a['bufferView']]
    n = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}[a['type']]
    off = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    if a['componentType'] == 5122:
        arr = np.frombuffer(bytes(b[off:off + a['count'] * n * 2]), dtype='<i2').reshape(a['count'], n).astype(np.float64)
        return np.maximum(arr / 32767.0, -1.0)
    if a['componentType'] == 5120:
        arr = np.frombuffer(bytes(b[off:off + a['count'] * n]), dtype='i1').reshape(a['count'], n).astype(np.float64)
        return np.maximum(arr / 127.0, -1.0)
    return np.frombuffer(bytes(b[off:off + a['count'] * n * 4]), dtype='<f4').reshape(a['count'], n).astype(np.float64)


def sample_q(times, vals, t):
    if len(times) == 1 or t <= times[0]: return vals[0]
    if t >= times[-1]: return vals[-1]
    k = int(np.searchsorted(times, t)) - 1; k = max(0, min(len(times) - 2, k))
    f = (t - times[k]) / (times[k + 1] - times[k]); return slerp(vals[k], vals[k + 1], f)


def sample_v(times, vals, t):
    if len(times) == 1 or t <= times[0]: return vals[0]
    if t >= times[-1]: return vals[-1]
    k = int(np.searchsorted(times, t)) - 1; k = max(0, min(len(times) - 2, k))
    f = (t - times[k]) / (times[k + 1] - times[k]); return vals[k] * (1 - f) + vals[k + 1] * f


class Rig:
    """FK over a glb's node tree. anims[name] = {(node,path): (times, values)}"""
    def __init__(s, j, b):
        s.j, s.b = j, b; s.nodes = j['nodes']; s.idx = {n.get('name'): i for i, n in enumerate(s.nodes) if 'name' in n}
        s.parent = {}
        for i, n in enumerate(s.nodes):
            for c in n.get('children', []): s.parent[c] = i
        s.rt = [np.array(n.get('translation', [0, 0, 0]), float) for n in s.nodes]
        s.rq = [np.array(n.get('rotation', [0, 0, 0, 1]), float) for n in s.nodes]
        s.anims = {}
        for a in j.get('animations', []):
            ch = {}
            for c in a['channels']:
                sm = a['samplers'][c['sampler']]
                ch[(c['target']['node'], c['target']['path'])] = (acc_data(j, b, sm['input'])[:, 0], acc_data(j, b, sm['output']))
            s.anims[a.get('name')] = ch
        s.order = s._topo()

    def _topo(s):
        seen = []; done = set()
        def go(i):
            if i in done: return
            p = s.parent.get(i)
            if p is not None: go(p)
            done.add(i); seen.append(i)
        for i in range(len(s.nodes)): go(i)
        return seen

    def duration(s, name):
        return max(v[0][-1] for v in s.anims[name].values())

    def pose(s, name, t):
        T = list(s.rt); Q = list(s.rq)
        if name is not None:
            for (n, p), (ts, vs) in s.anims[name].items():
                if p == 'rotation': Q[n] = sample_q(ts, vs, t)
                elif p == 'translation': T[n] = sample_v(ts, vs, t)
        return T, Q

    def world(s, name, t):
        """returns dict node index -> (R 3x3, position)"""
        T, Q = s.pose(name, t); W = {}
        for i in s.order:
            R = q2m(Q[i]); p = s.parent.get(i)
            if p is None: W[i] = (R, np.array(T[i], float))
            else: Rp, tp = W[p]; W[i] = (Rp @ R, tp + Rp @ T[i])
        return W

    def bone(s, short):
        return s.idx['mixamorig:' + short]

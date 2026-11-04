import json, struct, glob, sys
import numpy as np

def load(p):
    d = open(p, 'rb').read()
    n = struct.unpack('<I', d[12:16])[0]
    j = json.loads(d[20:20+n])
    off = 20 + n
    bl = struct.unpack('<I', d[off:off+4])[0]
    return j, bytearray(d[off+8:off+8+bl])

def save(j, b, p):
    while len(b) % 4: b.append(0)
    j['buffers'] = [{'byteLength': len(b)}]
    js = json.dumps(j).encode()
    while len(js) % 4: js += b' '
    out = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(b))
    out += struct.pack('<I', len(js)) + b'JSON' + js + struct.pack('<I', len(b)) + b'BIN\x00' + bytes(b)
    open(p, 'wb').write(out)

def qmul(a, b):  # xyzw, broadcast over rows of b
    ax, ay, az, aw = a
    bx, by, bz, bw = b[..., 0], b[..., 1], b[..., 2], b[..., 3]
    return np.stack([aw*bx + ax*bw + ay*bz - az*by,
                     aw*by - ax*bz + ay*bw + az*bx,
                     aw*bz + ax*by - ay*bx + az*bw,
                     aw*bw - ax*bx - ay*by - az*bz], -1)

def qrot(q, v):  # rotate vectors v (N,3) by quaternion q
    qv = np.array(q[:3]); w = q[3]
    t = 2 * np.cross(qv, v)
    return v + w * t + np.cross(qv, t)

def acc_data(j, b, ai):
    a = j['accessors'][ai]; bv = j['bufferViews'][a['bufferView']]
    n = {'SCALAR': 1, 'VEC3': 3, 'VEC4': 4}[a['type']]
    off = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    if a['componentType'] == 5122:
        arr = np.frombuffer(bytes(b[off:off + a['count'] * n * 2]), dtype='<i2').reshape(a['count'], n).astype(np.float64)
        return np.maximum(arr / 32767.0, -1.0)
    return np.frombuffer(bytes(b[off:off + a['count'] * n * 4]), dtype='<f4').reshape(a['count'], n).astype(np.float64)

base_path, out_path = sys.argv[1], sys.argv[2]
K = sys.argv[3] if len(sys.argv) > 3 else r'C:\Users\dom12\Documents\kits_out\3d\pitborn\clips6'
j, b = load(base_path)
name2idx = {n['name']: i for i, n in enumerate(j['nodes']) if 'name' in n}
hips = j['nodes'][name2idx['mixamorig:Hips']]
q_rig = np.array(hips['rotation']); rig_z = hips['translation'][2]
q_mix = np.array([-np.sqrt(.5), 0, 0, np.sqrt(.5)])
q_mix_inv = q_mix * np.array([-1, -1, -1, 1])
corr = qmul(q_rig, q_mix_inv[None, :])[0]
print('rig hips z', rig_z, 'corr', corr)
for n in j['nodes']:
    if n.get('name', '').startswith('Icosphere'):
        n.pop('mesh', None); n.pop('skin', None)
j['animations'] = []
import os
CUSTOM = len(sys.argv) > 3
KEYS = [os.path.splitext(os.path.basename(f))[0] for f in sorted(glob.glob(K + r'\*.glb'))] if CUSTOM else ['idle', 'walk', 'run', 'kicking', 'head-hit', 'dying']
for key in KEYS:
    cj, cb = load(K + '\\' + key + '.glb' if CUSTOM else glob.glob(K + r'\*-' + key + '.glb')[0])
    chips = [n for n in cj['nodes'] if n.get('name') == 'mixamorig:Hips'][0]
    clip_z = abs(chips['translation'][2])
    ratio = min(3.0, max(0.33, rig_z / clip_z))
    bvo = len(j['bufferViews']); acco = len(j['accessors'])
    while len(b) % 4: b.append(0)
    boff = len(b); b.extend(cb)
    for bv in cj['bufferViews']:
        bv = dict(bv); bv['buffer'] = 0; bv['byteOffset'] = bv.get('byteOffset', 0) + boff; j['bufferViews'].append(bv)
    for a in cj['accessors']:
        a = dict(a)
        if 'bufferView' in a: a['bufferView'] += bvo
        j['accessors'].append(a)
    anim = {'name': key, 'samplers': [], 'channels': []}
    for s in cj['animations'][0]['samplers']:
        anim['samplers'].append({'input': s['input'] + acco, 'output': s['output'] + acco, 'interpolation': s.get('interpolation', 'LINEAR')})
    for c in cj['animations'][0]['channels']:
        nm = cj['nodes'][c['target']['node']]['name']
        if nm not in name2idx: continue
        path = c['target']['path']
        if path == 'translation' and nm != 'mixamorig:Hips': continue
        samp = anim['samplers'][c['sampler']]
        if nm == 'mixamorig:Hips' and path in ('rotation', 'translation'):
            data = acc_data(j, b, samp['output'])
            if path == 'rotation':
                new = qmul(corr, data)
            else:
                v = qrot(corr, data * ratio)
                v[:, 0] = v[0, 0]; v[:, 1] = v[0, 1]  # hold non-vertical axes at first frame
                new = v
            new = new.astype('<f4').tobytes()
            while len(b) % 4: b.append(0)
            o = len(b); b.extend(new)
            j['bufferViews'].append({'buffer': 0, 'byteOffset': o, 'byteLength': len(new)})
            a = dict(j['accessors'][samp['output']]); a['bufferView'] = len(j['bufferViews']) - 1; a['byteOffset'] = 0
            a.pop('min', None); a.pop('max', None); a.pop('normalized', None); a['componentType'] = 5126
            j['accessors'].append(a)
            samp['output'] = len(j['accessors']) - 1
        anim['channels'].append({'sampler': c['sampler'], 'target': {'node': name2idx[nm], 'path': path}})
    print(key, len(anim['channels']), 'channels ratio', round(ratio, 3))
    j['animations'].append(anim)
save(j, b, out_path)


"""Bake the attack clips listed in Assets/Data/attack_clips.json ("bake") onto a character's rig as an animation-only GLB.

  python Tools/bake_attack_clips.py [Dracula]        (character name, default: every bake entry)

For every bake entry it writes  <outGlb>  = the base GLB's node tree (names, hierarchy, rest pose; NO meshes, skins, materials) + one animation per clip,
named by the Animator state (atk_light_R ...). Retarget = the same maths as Tools/merge_clips.py: Mixamo clip rotations are copied onto the matching
mixamorig:* nodes, the Hips get the rig correction quaternion (corr = q_rig * inverse(q_mix)) and the Hips translation is rotated by it and scaled by
rig_hips_z / clip_hips_z. Root motion: rootMotion "inplace" (default) holds the Hips horizontal translation at frame 0 (the body travels by DuelFighter stepIn);
"limited" keeps the clip's Hips horizontal travel scaled down to at most maxStep metres. Times, speeds and keys are untouched (no resampling).
Nothing is overwritten: an existing outGlb is moved to Tools/_backup_clips_bake/ first. Unity is never started; Assets/Editor/AttackClipSetup.cs
(menu Tools > Combat > Add Attack Clips) then builds the .anim assets and the Animator states from the imported GLB.
"""
import json, struct, sys, os, shutil, uuid, time
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from clip_sets_lib import expand_bake   # HCKIT5 v95
from paths import resolve as resolve_path   # HCKIT5 v95

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # unity_test_hdrp


def load(p):
    d = open(p, 'rb').read()
    n = struct.unpack('<I', d[12:16])[0]
    j = json.loads(d[20:20 + n])
    off = 20 + n
    bl = struct.unpack('<I', d[off:off + 4])[0]
    return j, bytearray(d[off + 8:off + 8 + bl])


def save(j, b, p):
    while len(b) % 4:
        b.append(0)
    j['buffers'] = [{'byteLength': len(b)}]
    js = json.dumps(j).encode()
    while len(js) % 4:
        js += b' '
    out = struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(b))
    out += struct.pack('<I', len(js)) + b'JSON' + js + struct.pack('<I', len(b)) + b'BIN\x00' + bytes(b)
    open(p, 'wb').write(out)


def qmul(a, b):  # xyzw, a = single quaternion, b = rows
    ax, ay, az, aw = a
    bx, by, bz, bw = b[..., 0], b[..., 1], b[..., 2], b[..., 3]
    return np.stack([aw * bx + ax * bw + ay * bz - az * by,
                     aw * by - ax * bz + ay * bw + az * bx,
                     aw * bz + ax * by - ay * bx + az * bw,
                     aw * bw - ax * bx - ay * by - az * bz], -1)


def qrot(q, v):
    qv = np.array(q[:3]); w = q[3]
    t = 2 * np.cross(qv, v)
    return v + w * t + np.cross(qv, t)


def acc_data(j, b, ai):
    a = j['accessors'][ai]; bv = j['bufferViews'][a['bufferView']]
    n = {'SCALAR': 1, 'VEC3': 3, 'VEC4': 4}[a['type']]
    off = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    ct = a['componentType']
    if ct == 5122:
        arr = np.frombuffer(bytes(b[off:off + a['count'] * n * 2]), dtype='<i2').reshape(a['count'], n).astype(np.float64)
        return np.maximum(arr / 32767.0, -1.0)
    if ct == 5126:
        return np.frombuffer(bytes(b[off:off + a['count'] * n * 4]), dtype='<f4').reshape(a['count'], n).astype(np.float64)
    raise ValueError('unsupported componentType %d' % ct)


def bake(entry, report):
    base = os.path.join(ROOT, entry['baseGlb'])
    out = os.path.join(ROOT, entry['outGlb'])
    j, b = load(base)
    name2idx = {n['name']: i for i, n in enumerate(j['nodes']) if 'name' in n}
    hips = j['nodes'][name2idx['mixamorig:Hips']]
    q_rig = np.array(hips['rotation']); rig_z = hips['translation'][2]
    q_mix = np.array([-np.sqrt(.5), 0, 0, np.sqrt(.5)])
    q_mix_inv = q_mix * np.array([-1, -1, -1, 1])
    corr = qmul(q_rig, q_mix_inv[None, :])[0]
    print('base', base, 'rig hips z', rig_z, 'corr', corr)

    keep = ('name', 'children', 'translation', 'rotation', 'scale', 'matrix')
    oj = {'asset': {'version': '2.0', 'generator': 'bake_attack_clips.py'},
          'scene': j.get('scene', 0), 'scenes': j['scenes'],
          'nodes': [{k: v for k, v in n.items() if k in keep} for n in j['nodes']],
          'animations': [], 'accessors': [], 'bufferViews': []}
    ob = bytearray()

    def add_acc(arr, typ, minmax=False):
        arr = np.ascontiguousarray(arr.astype('<f4'))
        while len(ob) % 4:
            ob.append(0)
        off = len(ob); ob.extend(arr.tobytes())
        oj['bufferViews'].append({'buffer': 0, 'byteOffset': off, 'byteLength': arr.nbytes})
        a = {'bufferView': len(oj['bufferViews']) - 1, 'componentType': 5126, 'count': int(arr.shape[0]), 'type': typ}
        if minmax:
            a['min'] = [float(arr.min())]; a['max'] = [float(arr.max())]
        oj['accessors'].append(a)
        return len(oj['accessors']) - 1

    posture = bool(entry.get('posture'))
    if posture:   # v2: posture fix (hips/spine caps, foot IK, blade lead, chest-height strike) in Tools/attack_posture_fix.py, parameters in attack_clips.json
        import attack_posture_fix as PF
        rig = PF.Rig(j); rest = rig.rest_world()
        cfg_all = json.load(open(os.path.join(ROOT, 'Assets', 'Data', 'attack_clips.json'), encoding='utf-8-sig'))
        contacts = {}
        for r in cfg_all['rows']:
            if r.get('character') == entry['character']: contacts.setdefault(r['clip'], r['contactTime'])

    for c in entry['clips']:
        src = resolve_path(c['source'])   # HCKIT5 v95 ${kitsOut} tokens
        mode = c.get('rootMotion', 'inplace'); max_step = float(c.get('maxStep', 0.0))
        if posture:
            times, rot, hipt, ratio = PF.retarget(rig, None, src, sys.modules[__name__])
            params = dict(cfg_all.get('postureDefaults', {})); params.update(c.get('posture', {}))
            rot2, hip2 = PF.fix(rig, times, rot, hipt, contacts.get(c['state'], 0.4), params, rest)
            anim = {'name': c['state'], 'samplers': [], 'channels': []}
            for node, q in sorted(rot2.items()):
                ti = add_acc(times.reshape(-1, 1), 'SCALAR', True); vi = add_acc(q, 'VEC4')
                anim['samplers'].append({'input': ti, 'output': vi, 'interpolation': 'LINEAR'})
                anim['channels'].append({'sampler': len(anim['samplers']) - 1, 'target': {'node': node, 'path': 'rotation'}})
            ti = add_acc(times.reshape(-1, 1), 'SCALAR', True); vi = add_acc(hip2, 'VEC3')
            anim['samplers'].append({'input': ti, 'output': vi, 'interpolation': 'LINEAR'})
            anim['channels'].append({'sampler': len(anim['samplers']) - 1, 'target': {'node': rig.hips, 'path': 'translation'}})
            oj['animations'].append(anim)
            info = {'state': c['state'], 'source': os.path.basename(src), 'ratio': round(float(ratio), 3), 'rootMotion': 'inplace', 'posture': params,
                    'duration': round(float(times[-1]), 3), 'channels': len(anim['channels'])}
            report.append(info); print(info)
            continue
        cj, cb = load(src)
        chips = [n for n in cj['nodes'] if n.get('name') == 'mixamorig:Hips'][0]
        clip_z = abs(chips['translation'][2])
        ratio = min(3.0, max(0.33, rig_z / clip_z))
        an = cj['animations'][0]
        anim = {'name': c['state'], 'samplers': [], 'channels': []}
        info = {'state': c['state'], 'source': os.path.basename(src), 'ratio': round(float(ratio), 3), 'rootMotion': mode}
        dur = 0.0
        for ch in an['channels']:
            nm = cj['nodes'][ch['target']['node']].get('name')
            path = ch['target']['path']
            if nm not in name2idx or path not in ('rotation', 'translation'):
                continue
            if path == 'translation' and nm != 'mixamorig:Hips':
                continue
            s = an['samplers'][ch['sampler']]
            t = acc_data(cj, cb, s['input'])[:, 0]
            v = acc_data(cj, cb, s['output'])
            dur = max(dur, float(t[-1]))
            if nm == 'mixamorig:Hips' and path == 'rotation':
                v = qmul(corr, v)
            elif nm == 'mixamorig:Hips' and path == 'translation':
                v = qrot(corr, v * ratio)
                h0 = v[0, :2].copy()
                travel = float(np.max(np.linalg.norm(v[:, :2] - h0, axis=1)))
                info['hipsTravelBefore'] = round(travel, 3)
                if mode == 'limited' and max_step > 0 and travel > 1e-6:
                    k = min(1.0, max_step / travel)
                    v[:, :2] = h0 + (v[:, :2] - h0) * k
                else:
                    v[:, 0] = h0[0]; v[:, 1] = h0[1]   # in place (same rule as merge_clips.py)
                info['hipsTravelAfter'] = round(float(np.max(np.linalg.norm(v[:, :2] - h0, axis=1))), 3)
            ti = add_acc(t.reshape(-1, 1), 'SCALAR', True)
            vi = add_acc(v, 'VEC3' if path == 'translation' else 'VEC4')
            anim['samplers'].append({'input': ti, 'output': vi, 'interpolation': 'LINEAR'})
            anim['channels'].append({'sampler': len(anim['samplers']) - 1, 'target': {'node': name2idx[nm], 'path': path}})
        info['duration'] = round(dur, 3); info['channels'] = len(anim['channels'])
        oj['animations'].append(anim)
        report.append(info)
        print(info)

    if os.path.exists(out):
        bdir = os.path.join(ROOT, 'Tools', '_backup_clips_bake'); os.makedirs(bdir, exist_ok=True)
        shutil.copyfile(out, os.path.join(bdir, os.path.basename(out) + '.' + time.strftime('%H%M%S')))
    save(oj, ob, out)
    print('wrote', out, os.path.getsize(out), 'bytes')

    meta = out + '.meta'
    if not os.path.exists(meta):   # same glTFast importer settings as the character GLB, new GUID
        bm = open(base + '.meta', 'r', encoding='utf-8').read()
        lines = []
        skip = False
        for ln in bm.splitlines():
            if ln.startswith('guid:'):
                ln = 'guid: ' + uuid.uuid4().hex
            if ln.strip() == 'reportItems:':
                skip = True; lines.append('  reportItems: []'); continue
            if skip:
                if ln.startswith('  ') and not ln.startswith('  - ') and not ln.startswith('    '):
                    skip = False
                else:
                    continue
            lines.append(ln)
        open(meta, 'w', encoding='utf-8', newline='\n').write('\n'.join(lines) + '\n')
        print('wrote', meta)


def main():
    cfg = json.load(open(os.path.join(ROOT, 'Assets', 'Data', 'attack_clips.json'), encoding='utf-8-sig'))
    only = sys.argv[1] if len(sys.argv) > 1 else None
    report = []
    for e in expand_bake(cfg, 'attack'):   # HCKIT5 v95 explicit bake rows + roster clipSet rows
        if only and e['character'] != only:
            continue
        if e.get('prebuilt'):   # BOWART v2: glb built by Tools/bow_clips_build.py, nothing to retarget
            print('skip prebuilt entry', e['outGlb']); continue
        bake(e, report)
    json.dump(report, open(os.path.join(ROOT, 'Tools', 'attack_bake_report.json'), 'w'), indent=1)


if __name__ == '__main__':
    main()

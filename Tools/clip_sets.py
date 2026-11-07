"""CLIPSETS clip_sets.py (lane CLIPBUILD2): attacks / dodges / finishers baked from the ORIGINAL sources onto the character's OWN 106-joint skeleton.
Extends laneCLIPBUILD's tool (clip_build.py / clip_lib.py / clip_gates.py / leg_fix.py are lane copies, unchanged except where noted in MANIFEST).

  python clip_sets.py --set attacks|dodges|finishers|all [--char Joan] [--gates] [--render]    (config: ../Assets/Data/clip_sets.json)

Per clip (same maths as clip_build.build_clip / bake_attack_clips.py, no Dracula data anywhere):
  1. raw source glb (52-bone library rig, Hips translation) -> per-bone rotations on the body's node tree (106 joints; unkeyed joints stay at rest)
  2. Hips: rotation = corr * q, translation = corr * (p * ratio); corr = rig hips rotation * inverse(Mixamo hips rotation) (read from the body), ratio = body hips z / clip hips z (clamped 0.33..3)
  3. root motion per row: inplace (horizontal hips travel held at frame 0) | keep (victims that fall / are flung) | limited (<= maxStepM)
  4. attacks only: the posture fix of attack_posture_fix.py with the character's OWN posture rows of attack_clips.json (hips cap, feet IK, blade aim, contact height)
  5. leg fix (clip_build.json tools.legFix, mode auto: only clips whose knee points inward more than the trigger are touched)
Writes out/<Id>_attacks_v2.glb, out/<Id>_dodges.glb, out/<Id>_finishers.glb (animation-only, body node tree, names = Animator states) and out/clip_sets_report.json.
Times, key counts and frame rate are the source's (no resampling), so paired killer/victim timing and hips travel are as today.
numpy only, CPU only. Never writes outside --out.
"""
import argparse, copy, glob, hashlib, json, math, os, shutil, sys, types
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__)); ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE); sys.dont_write_bytecode = True
import clip_lib as L
import clip_build as CB
import clip_gates as G
import leg_fix

TOOL_VERSION = 'clipsets-1'


def fail(m):
    print('CLIPSETS ERROR:', m); sys.exit(3)


# ------------------------------------------------------------------ body template (animation-only, full node tree)
def body_template(proj, rc):
    body = os.path.normpath(os.path.join(proj, rc['modelGlb']))
    j0, b0 = L.load(body)
    keep = ('name', 'children', 'translation', 'rotation', 'scale', 'matrix')
    j = {'asset': {'version': '2.0', 'generator': TOOL_VERSION}, 'scene': j0.get('scene', 0), 'scenes': copy.deepcopy(j0['scenes']),
         'nodes': [{k: v for k, v in n.items() if k in keep} for n in j0['nodes']], 'animations': [], 'accessors': [], 'bufferViews': []}
    return j, bytearray(), body, j0, b0


def corr_of(j):
    n2i = {n['name']: i for i, n in enumerate(j['nodes']) if 'name' in n}
    hips = j['nodes'][n2i['mixamorig:Hips']]
    q_rig = np.array(hips['rotation']); rig_z = hips['translation'][2]
    q_mix = np.array([-math.sqrt(.5), 0, 0, math.sqrt(.5)])
    return L.qmul(q_rig, L.qinv(q_mix)), rig_z, n2i


# ------------------------------------------------------------------ retarget one clip (non-posture)
def root_motion(c, mode, max_step):
    v = c.hips_t; h0 = v[0, :2].copy(); travel = float(np.max(np.linalg.norm(v[:, :2] - h0, axis=1)))
    if mode == 'inplace': v[:, 0] = h0[0]; v[:, 1] = h0[1]
    elif mode == 'limited' and max_step > 0 and travel > 1e-6:
        k = min(1.0, max_step / travel); v[:, :2] = h0 + (v[:, :2] - h0) * k
    elif mode not in ('keep', 'limited'): fail('unknown rootMotion %s' % mode)
    c.hips_t = v
    return travel, float(np.max(np.linalg.norm(v[:, :2] - h0, axis=1)))


def roll_scale_hips(q, k, axis):
    """PITNEW v1: scale the roll (rotation about `axis` in the source world frame) of a Hips rotation track toward the rest pose (same maths as clip_build PITNEW v1)"""
    qm = np.array([-math.sqrt(.5), 0, 0, math.sqrt(.5)]); ax_ = np.array(axis, float)
    w_ = L.qmul(q, L.qinv(qm)); ang = 2 * np.arctan2(w_[:, :3] @ ax_, w_[:, 3])
    tw = np.concatenate([ax_[None, :] * np.sin(ang / 2)[:, None], np.cos(ang / 2)[:, None]], 1)
    sw = L.qmul(w_, L.qinv(tw)); tw2 = np.concatenate([ax_[None, :] * np.sin(k * ang / 2)[:, None], np.cos(k * ang / 2)[:, None]], 1)
    return L.qmul(L.qmul(sw, tw2), qm)


def retarget_plain(path, corr, rig_z, mode, max_step, hips_y=0.0, ratio_from=None, corr_override=None, roll=None):
    c = CB.Clip(path)
    if roll is not None: c.rot['mixamorig:Hips'] = roll_scale_hips(c.rot['mixamorig:Hips'], float(roll[0]), roll[1])    # PITNEW v1
    z = ratio_from if ratio_from else c.hips_rest_z
    ratio = min(3.0, max(0.33, rig_z / z))
    cc = corr_override if corr_override is not None else corr
    c.rot['mixamorig:Hips'] = L.qmul(cc, c.rot['mixamorig:Hips'])
    c.hips_t = np.array([L.qrot(cc, x * ratio) for x in c.hips_t])
    tb, ta = root_motion(c, mode, max_step)
    c.hips_t[:, 2] += hips_y
    return c, ratio, tb, ta


# ------------------------------------------------------------------ attacks (posture fix, as bake_attack_clips.py)
class PFShim:
    """the few helpers attack_posture_fix.retarget wants from bake_attack_clips (kept here so the project Tools folder is never imported)"""
    load = staticmethod(L.load); qmul = staticmethod(L.qmul); qrot = staticmethod(L.qrot); acc_data = staticmethod(L.acc_data)


def retarget_posture(j, row, contact, defaults, pf_cache):
    import attack_posture_fix as PF
    rig = pf_cache.get('rig') or PF.Rig(j); pf_cache['rig'] = rig
    rest = pf_cache.get('rest')
    if rest is None: rest = rig.rest_world(); pf_cache['rest'] = rest
    times, rot, hipt, ratio = PF.retarget(rig, None, row['src'], PFShim)
    params = dict(defaults); params.update(row.get('posture', {}))
    rot2, hip2 = PF.fix(rig, times, rot, hipt, contact, params, rest)
    c = types.SimpleNamespace(times=np.asarray(times, float), rot={rig.N[n].get('name'): q for n, q in rot2.items()}, hips_t=np.asarray(hip2, float))
    return c, ratio


# ------------------------------------------------------------------ build a set
def knee_pre(sk, j, b, anim, n2i):
    return G.knee_medial_anim(sk, j, b, anim, n2i)


def build_set(cfg, ccfg, roster, proj, cid, setname, outdir, live_emul=False):
    rc = [c for c in roster['characters'] if c['id'] == cid][0]
    S = cfg['sets'][setname]
    j, b, body, j0, b0 = body_template(proj, rc)
    corr, rig_z, n2i = corr_of(j)
    rows = S['rows'][cid]
    clips = []; rep = []; pf_cache = {}
    for r in rows:
        mode = r.get('rootMotion', 'inplace'); ms = float(r.get('maxStep', 0.0))
        src = r['src']
        if not os.path.exists(src): fail('%s/%s: source missing %s' % (setname, r['state'], src))
        low = src.lower().replace('/', '\\')
        other = [t for t in cfg['foreignTokens'] + [c['id'].lower() for c in roster['characters'] if c['id'] != cid] if t.lower() in low]
        if other and not r.get('foreign'): fail('%s/%s: source path names another character (%s): %s' % (setname, r['state'], other, src))
        info = {'state': r['state'], 'src': os.path.basename(src), 'srcDir': os.path.dirname(src).replace('\\', '/').split('/')[-1], 'set': setname}
        if setname.startswith('attacks') and r.get('posture') is not None:
            c, ratio = retarget_posture(j, r, r['contactTime'], cfg['sets']['attacks']['postureDefaults'], pf_cache)
            info.update({'ratio': round(float(ratio), 4), 'rootMotion': 'inplace', 'posture': True})
        else:
            corr_o = None
            if live_emul: corr_o = np.array(r['liveCorr']) if r.get('liveCorr') else None
            c, ratio, tb, ta = retarget_plain(src, corr, rig_z, mode, ms, float(r.get('hipsYOffsetM', 0.0)), r.get('hipsRestZ') if live_emul else None, corr_o, (float(r['hipsRollScale']), r.get('rollAxis', [0, 1, 0])) if r.get('hipsRollScale') is not None else None)
            info.update({'ratio': round(float(ratio), 4), 'rootMotion': mode, 'hipsTravelBeforeM': round(tb, 3), 'hipsTravelAfterM': round(ta, 3), 'posture': False})
        info['frames'] = int(len(c.times)); info['durationS'] = round(float(c.times[-1]), 4); info['channels'] = len(c.rot) + 1
        clips.append((r['state'], c)); rep.append(info)
    CB.write_clips(j, b, n2i, clips)
    # leg fix (shared tool, same mode / cap as the base set)
    lf = dict(ccfg['tools']['legFix']); lf['maxSwivelDeg'] = S.get('legFixMaxSwivelDeg', lf['maxSwivelDeg']); leg_fix.set_cap(lf['maxSwivelDeg']); sk, _ = leg_fix.skel_from(j)
    for info, (state, c) in zip(rep, clips):
        an = [a for a in j['animations'] if a['name'] == state][0]
        pre = knee_pre(sk, j, b, an, n2i); info['kneeMedialPreFixM'] = round(pre, 4); info['legFix'] = 'none'
        if not live_emul and (lf['mode'] == 'always' or (lf['mode'] == 'auto' and pre > lf['kneeMedialTriggerM'])):
            n, w = leg_fix.fix_animation(j, b, an, sk, n2i); info['legFix'] = 'applied'; info['legFixMaxSwivelDeg'] = round(w, 1)
    os.makedirs(outdir, exist_ok=True)
    name = S['outFile'].format(id=cid) if not live_emul else 'live_emul_%s' % S['outFile'].format(id=cid)
    out = os.path.join(outdir, name)
    L.save(j, b, out)
    return {'file': out, 'md5': hashlib.md5(open(out, 'rb').read()).hexdigest(), 'bytes': os.path.getsize(out), 'clips': rep, 'body': body}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--set', default='all'); ap.add_argument('--char', default='Joan'); ap.add_argument('--out'); ap.add_argument('--gates', action='store_true')
    ap.add_argument('--proj'); ap.add_argument('--config'); ap.add_argument('--check', action='store_true')
    a = ap.parse_args()
    proj = a.proj or CB.find_proj(ROOT) or CB.find_proj(os.getcwd())
    if not proj: fail('project root not found')
    cfg = CB.read_json(a.config or os.path.join(ROOT, 'Assets', 'Data', 'clip_sets.json'))
    ccfg = CB.read_json(os.path.join(ROOT, 'Assets', 'Data', 'clip_build.json')); roster = CB.read_json(os.path.join(proj, 'Assets', 'Data', 'roster.json'))
    outdir = os.path.normpath(a.out) if a.out else os.path.join(ROOT, 'out')
    sets = [s for s in cfg['sets'] if s != 'finishers_live'] if a.set == 'all' else [a.set]
    if a.char not in [c['id'] for c in roster['characters']]: fail('%s not in roster' % a.char)
    for s in sets:
        if s not in cfg['sets']: fail('unknown set %s' % s)
        if a.char not in cfg['sets'][s]['rows']: fail('%s: no rows for %s' % (s, a.char))
    if a.check:
        for s in sets:
            for r in cfg['sets'][s]['rows'][a.char]:
                if not os.path.exists(r['src']): fail('%s/%s source missing %s' % (s, r['state'], r['src']))
        print('check ok', sets); return
    reps = {}
    for s in sets:
        reps[s] = build_set(cfg, ccfg, roster, proj, a.char, s, outdir)
        print('%s: wrote %s (%d bytes, %d clips)' % (s, reps[s]['file'], reps[s]['bytes'], len(reps[s]['clips'])))
        if s == 'finishers':       # what the project does today (C# retarget of Tools/finisher_src_legfix on the same tree), for the live column
            reps['finishers_live'] = build_set(cfg, ccfg, roster, proj, a.char, 'finishers_live', outdir, live_emul=True)
    json.dump({'tool': TOOL_VERSION, 'character': a.char, 'sets': reps}, open(os.path.join(outdir, 'clip_sets_report.json'), 'w'), indent=1)
    if a.gates:
        import clip_sets_gates as SG
        bad = SG.run(cfg, ccfg, roster, proj, a.char, reps, outdir)
        sys.exit(3 if bad else 0)


if __name__ == '__main__':
    main()

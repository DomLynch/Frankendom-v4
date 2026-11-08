"""BOWART bow_clips_build.py: build the bow draw/hold/loose/special clips for a Mixamo-106 character from Assets/Data/bow_clips.json (numpy only, CPU only).

  python bow_clips_build.py [--proj DIR] [--out DIR] [--char Joan] [--preview]

Reads   <proj>/Assets/Data/bow_clips.json  (+ roster.json for the model glb)
Writes  <out>/Assets/Characters/<folder>/<id>_bow_v1.glb   animation-only glb (bake format: node tree of the combat glb, one animation per clip id)
        <out>/out/bow_clips_report.json                    CPU gates + the bow hold (Unity Euler / position in the bow-hand bone frame)
        --preview: <out>/out/<id>_bow_preview.glb          combat glb + the new clips (for the Blender contact sheet only, never shipped)
Never writes into <proj>. Idempotent."""
import argparse, json, math, os, sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import bow_pose as P
import clip_lib as L


def jload(p): return json.load(open(p, encoding='utf-8-sig'))


def key_params(cfg, clip):
    """list of (t, dict) with every param resolved: key value, else clip default, else 0"""
    names = ['raise', 'draw', 'hook', 'relBack', 'relDrop', 'lean', 'aimYaw', 'aimPitch', 'tremor', 'kick']
    base = {n: 0.0 for n in names}
    like = None
    if clip.get('like'):
        like = [c for c in cfg['clips'] if c['id'] == clip['like']][0]
    src = like if like else clip
    for n in names:
        if n in clip: base[n] = float(clip[n])
        elif like and n in like: base[n] = float(like[n])
    out = []
    for k in src['keys']:
        d = dict(base)
        for n in names:
            if n in k: d[n] = float(k[n])
        if clip.get('like') and 'aimYaw' in clip: d['aimYaw'] = d['aimYaw'] if 'aimYaw' in k else float(clip['aimYaw'])
        out.append((float(k['t']), d))
    return out


def eval_params(keys, t, ease):
    if t <= keys[0][0]: return dict(keys[0][1])
    if t >= keys[-1][0]: return dict(keys[-1][1])
    for (t0, a), (t1, b) in zip(keys[:-1], keys[1:]):
        if t0 <= t <= t1:
            f = (t - t0) / max(t1 - t0, 1e-9); f = P.smooth(f) if ease == 'smooth' else f
            return {n: a[n] + (b[n] - a[n]) * f for n in a}
    return dict(keys[-1][1])


def side_sign(body, side):
    return 1.0 if body.rest_world[body.bone(side + 'Hand')][1][0] > 0 else -1.0


def solve(body, cfg, prm, phase, cache):
    """one frame. phase = t / duration (tremor period). Returns metrics dict."""
    st = cfg['stance']; ar = cfg['arms']; fg = cfg['fingers']
    base = cfg['base']; bs = cfg['bowHand']; ds = cfg['drawHand']
    body.load_base(base['state'], base['time'])
    bw0 = body.base_world
    hips0 = body.pos('Hips').copy()
    foot_R0 = {s: bw0[body.bone(s + 'Foot')][0].copy() for s in (bs, ds)}
    foot_y0 = {s: bw0[body.bone(s + 'Foot')][1][1] for s in (bs, ds)}
    head_R0 = bw0[body.bone('Head')][0].copy(); neck_R0 = bw0[body.bone('Neck')][0].copy()
    # ---- torso / stance
    body.set_world_pos('Hips', [hips0[0], st['hipsY'], hips0[2]])
    body.rot_world('Hips', P.rotY(st['hipsYawDeg']))
    for nm, yaw in zip(('Spine', 'Spine1', 'Spine2'), st['spineYawDeg']): body.rot_world(nm, P.rotY(yaw))
    total_yaw = st['hipsYawDeg'] + sum(st['spineYawDeg'])
    chest_fwd = P.rotY(total_yaw) @ np.array([0, 0, 1.0])
    if abs(prm['lean']) > 1e-6:
        ax = P.unit(np.cross([0, 1.0, 0], -chest_fwd))     # rotating about ax tilts the top towards -chest_fwd (back)
        h_before = body.pos('Head').copy()
        body.rot_world('Spine1', P.rotAxis(ax, prm['lean']))
        if float(np.dot(body.pos('Head') - h_before, -chest_fwd)) * np.sign(prm['lean']) < 0:   # sign guard
            body.rot_world('Spine1', P.rotAxis(ax, -2 * prm['lean']))
    # ---- feet (planted: constant world targets from the base hips)
    sh = {}
    for k, s in enumerate((bs, ds)):
        tgt = np.array([hips0[0] + st['feetOffsetX'][k], foot_y0[s], hips0[2] + st['feetOffsetZ'][k]])
        pole = np.array(st['kneePole'], float)
        err, _ = body.ik2(s + 'UpLeg', s + 'Leg', s + 'Foot', tgt, pole, tip_R=P.rotY(st['footYawDeg']) @ foot_R0[s],
                          twist_split=0.5, bone_axis=P.unit(np.array(body.R.rt[body.bone(s + 'Foot')], float)))
        sh[s + 'Foot'] = (err, tgt)
    # ---- head / neck
    yawH = prm['aimYaw'] * st['headYawShare']
    Rh = P.rotY(yawH) @ head_R0; Rn = P.rotY(yawH * st['neckYawShare']) @ neck_R0
    if st.get('headPitchDeg'): Rh = P.rotAxis(np.cross([0, 1, 0], P.rotY(yawH) @ [0, 0, 1]), st['headPitchDeg']) @ Rh
    body.set_world_rot('Neck', Rn); body.set_world_rot('Head', Rh)
    head_pos = body.pos('Head').copy()
    head_yaw_R = P.rotY(yawH)
    # ---- arms
    sgnB = side_sign(body, bs); sgnD = side_sign(body, ds)
    ease = P.smooth(prm['raise'])
    aim_dir = P.unit(P.rotY(prm['aimYaw']) @ np.array([0, math.sin(math.radians(prm['aimPitch'])), math.cos(math.radians(prm['aimPitch']))]))
    shB = body.pos(bs + 'Arm').copy()
    lenB = float(np.linalg.norm(body.pos(bs + 'ForeArm') - shB) + np.linalg.norm(body.pos(bs + 'Hand') - body.pos(bs + 'ForeArm')))
    aimT = shB + aim_dir * (lenB * ar['bowArmReach']) + np.array([0, ar['aimHeightAboveShoulder'], 0])
    cy = ar['carry']; carryT = shB + np.array([sgnB * cy['side'], -cy['down'], cy['forward']])
    bowT = carryT * (1 - ease) + aimT * ease
    ph = 2 * math.pi * phase
    bowT = bowT + prm['tremor'] * np.array([math.sin(ph), math.sin(2 * ph + 0.7), 0.0]) - aim_dir * prm['kick']
    poleB = np.array(ar['bowArmPole'], float) * np.array([sgnB, 1, 1])
    shD = body.pos(ds + 'Arm').copy()

    def hand_R(side, f, n_hint, roll_deg):
        i = body.bone(side + 'Hand'); mid = body.bone(side + 'HandMiddle1')
        f0 = P.unit(body.rest_world[mid][1] - body.rest_world[i][1])
        n0 = np.array([0, -1.0, 0]); n0 = P.unit(n0 - f0 * float(np.dot(n0, f0)))
        B0 = np.stack([f0, n0, np.cross(f0, n0)], 1)
        f = P.unit(f); n = np.array(n_hint, float); n = P.unit(n - f * float(np.dot(n, f)))
        n = P.rotAxis(f, roll_deg) @ n
        Bt = np.stack([f, n, np.cross(f, n)], 1)
        return Bt @ B0.T @ body.rest_world[i][0], f0, n0

    # pass 1: position only (twist cost with the hand left alone), pass 2: hand orientation
    for _pass in range(2):
        tipB = None
        if _pass == 1:
            E = body.pos(bs + 'ForeArm'); W_ = body.pos(bs + 'Hand')
            fB = P.unit(W_ - E)
            RB, _f0, _n0 = hand_R(bs, fB, np.array(ar['bowHandPalm'], float) * np.array([sgnB, 1, 1]), ar['bowHandRollDeg'] * sgnB)
            tipB = RB
        axb = P.unit(np.array(body.R.rt[body.bone(bs + 'Hand')], float))
        errB, _ = body.ik2(bs + 'Arm', bs + 'ForeArm', bs + 'Hand', bowT, poleB, tip_R=tipB, twist_split=0.5, bone_axis=axb)
    handB = body.fk()[body.bone(bs + 'Hand')]
    grip_dir = P.unit(handB[1] - body.pos(bs + 'ForeArm'))
    P_grip = handB[1] + grip_dir * cfg['hold']['palmAlong']
    back = -aim_dir
    nock = P_grip + back * ar['brace']
    A = ar['anchor']
    anchor = head_pos + head_yaw_R @ np.array([A['x'] * -sgnD, A['y'], A['z']])
    d = prm['draw']; Pstr = nock * (1 - d) + anchor * d
    Pstr = Pstr + back * prm['relBack']
    dr = ar['dropTarget']; dropP = shD + np.array([sgnD * dr['side'], -dr['down'], dr['forward']])
    f_D = P.unit(aim_dir * (1 - prm['relDrop']) + P.unit(np.array([0, -0.6, 0.8])) * prm['relDrop'])
    wristD = Pstr - f_D * ar['stringHandBack'] - np.array([0, ar['stringHandDown'], 0])
    wristD = wristD * (1 - prm['relDrop']) + dropP * prm['relDrop']
    nl = lambda a_, b_, f_: P.unit(P.unit(np.array(a_, float)) * (1 - f_) + P.unit(np.array(b_, float)) * f_)   # blend unit poles (a small blended vector would swing the elbow)
    poleD = nl(nl(ar['drawPoleNock'], ar['drawPoleFull'], d), ar['dropPole'], prm['relDrop'])
    poleD = poleD * np.array([-sgnD, 1, 1])   # poles are written for a right draw hand; the elbow of a hanging arm points to the back of the torso (never anti-parallel to the arm)
    for _pass in range(2):
        tipD = None
        if _pass == 1:
            E = body.pos(ds + 'ForeArm'); W_ = body.pos(ds + 'Hand')
            RD, _f0, _n0 = hand_R(ds, f_D, np.array(ar['drawHandPalm'], float) * np.array([-sgnD, 1, 1]), ar['drawHandRollDeg'] * sgnD)
            tipD = RD
        axd = P.unit(np.array(body.R.rt[body.bone(ds + 'Hand')], float))
        errD, _ = body.ik2(ds + 'Arm', ds + 'ForeArm', ds + 'Hand', wristD, poleD, tip_R=tipD, twist_split=0.5, bone_axis=axd)
    # ---- fingers
    def finger_axes(side):
        i = body.bone(side + 'Hand'); mid = body.bone(side + 'HandMiddle1')
        f0 = P.unit(body.rest_world[mid][1] - body.rest_world[i][1])
        n0 = np.array([0, -1.0, 0]); n0 = P.unit(n0 - f0 * float(np.dot(n0, f0)))
        a_f = P.unit(np.cross(f0, n0))
        t1 = body.rest_world[body.bone(side + 'HandThumb2')][1] - body.rest_world[body.bone(side + 'HandThumb1')][1]
        a_t = P.unit(np.cross(P.unit(t1), n0))
        return a_f, a_t
    aB, tB = finger_axes(bs); aD, tD = finger_axes(ds)
    for f in ('Index', 'Middle', 'Ring', 'Pinky'):
        ch = [bs + 'Hand' + f + str(k) for k in (1, 2, 3)]
        body.curl(ch, fg['bow']['pinky' if f == 'Pinky' else 'finger'], aB)
    body.curl([bs + 'HandThumb' + str(k) for k in (1, 2, 3)], fg['bow']['thumb'], tB)
    h = prm['hook']
    for f in ('Index', 'Middle', 'Ring', 'Pinky'):
        a_h = np.array(fg['hook'][f.lower()], float); a_o = np.array(fg['open']['finger'], float)
        body.curl([ds + 'Hand' + f + str(k) for k in (1, 2, 3)], list(a_o * (1 - h) + a_h * h), aD)
    body.curl([ds + 'HandThumb' + str(k) for k in (1, 2, 3)], list(np.array(fg['open']['thumb'], float) * (1 - h) + np.array(fg['hook']['thumb'], float) * h), tD)
    W = body.fk()
    pc = lambda pole_, sh_, w_: abs(float(np.dot(P.unit(pole_), P.unit(w_ - sh_))))
    return {'poleCos': max(pc(poleB, shB, bowT), pc(poleD, shD, wristD)), 'errBow': errB, 'errDraw': errD, 'feet': sh, 'anchor': anchor, 'Pstr': Pstr, 'aim_dir': aim_dir, 'bowT': bowT, 'wristD': wristD, 'prm': prm}


def bow_world(body, cfg, aim_dir):
    """bow glb (glTF-space) world frame fitted to the bow hand in the CURRENT pose: long axis = fist curl axis (vertical), string side towards the archer.
    returns R (3x3, columns = bow X,Y,Z in world), p (grip centre)"""
    bs = cfg['bowHand']; ho = cfg['hold']
    Hh = body.fk()[body.bone(bs + 'Hand')]
    fdir = P.unit(Hh[1] - body.pos(bs + 'ForeArm'))
    # palm normal in the world = the rest hand-frame axis that pointed down (palm) at rest, now carried by the hand rotation
    i = body.bone(bs + 'Hand')
    rest_n = body.rest_world[i][0].T @ np.array([0, -1.0, 0])      # palm normal in the hand frame (rest: palm down)
    n = Hh[0] @ rest_n
    p = Hh[1] + fdir * ho['palmAlong'] + n * ho['palmDepth']
    up = np.array([0, 1.0, 0])
    Zb = -aim_dir.copy(); Zb[1] = 0; Zb = P.unit(Zb) if np.linalg.norm(Zb) > 1e-6 else np.array([0, 0, -1.0])
    Zb = P.rotAxis(np.cross(up, Zb), ho.get('bowTiltPitchDeg', 0.0)) @ Zb if ho.get('bowTiltPitchDeg', 0.0) else Zb
    Yb = P.unit(up - Zb * float(np.dot(up, Zb)))
    Xb = np.cross(Yb, Zb)
    p = p + Zb * ho.get('gripShiftBack', 0.0)
    return np.stack([Xb, Yb, Zb], 1), p, n


def to_unity_hold(body, cfg, Rb, pb):
    bs = cfg['bowHand']; Hh = body.fk()[body.bone(bs + 'Hand')]
    Rg = Hh[0].T @ Rb; pg = Hh[0].T @ (pb - Hh[1])
    M = np.diag([-1.0, 1, 1]); Ru = M @ Rg @ M; pu = M @ pg
    e = P.euler_zxy_deg(Ru)
    chk = P.euler_to_m(*e); err = float(np.abs(chk - Ru).max())
    return e, pu, err


def angle_between(a, b):
    return math.degrees(math.acos(max(-1.0, min(1.0, float(np.dot(P.unit(a), P.unit(b)))))))


def build(proj, out, char, want_preview, cfg_path=None):
    cfg = jload(cfg_path or os.path.join(proj, 'Assets', 'Data', 'bow_clips.json'))
    if char not in cfg['characters']: raise SystemExit('BOWART: %s is not in bow_clips.json characters' % char)
    ros = jload(os.path.join(proj, 'Assets', 'Data', 'roster.json'))
    row = [r for r in ros['characters'] if r['id'] == char][0]
    glb = os.path.join(proj, row['modelGlb']); folder = os.path.dirname(row['modelGlb']).replace('\\', '/')
    body = P.Body(glb)
    fps = float(cfg['fps']); gates = cfg['gates']
    anims = {}; report = {'character': char, 'tool': 'bowart-1', 'clips': {}, 'gatesFailed': []}
    base_rotch = [n for (n, p) in body.R.anims[cfg['base']['state']].keys() if p == 'rotation']
    hips = body.bone('Hips')
    cache = {}
    hold_info = None
    for clip in cfg['clips']:
        keys = key_params(cfg, clip); dur = float(clip['duration']); n = int(round(dur * fps)) + 1
        times = np.array([k / fps for k in range(n)]); times[-1] = dur
        rot = {nd: np.zeros((n, 4)) for nd in base_rotch}; hip_t = np.zeros((n, 3))
        met = {'reachErrM': 0.0, 'footSlideM': 0.0, 'footMinY': 9.0, 'maxStepDeg': 0.0, 'elbowMin': 999.0, 'elbowMax': 0.0, 'anchorErrM': None, 'bowGripErrM': 0.0}
        feet_ref = {}; prevq = None; first = None; body.roll_prev.clear()
        for k in range(n):
            t = float(times[k]); ph = (t / dur) if clip.get('loop') else 0.0
            if clip.get('loop') and k == n - 1: ph = 0.0
            prm = eval_params(keys, t, clip.get('ease', 'smooth'))
            if clip.get('loop'):   # tremor phase uses the clip period; last frame == first frame
                ph = (k % (n - 1)) / (n - 1)
            res = solve(body, cfg, prm, ph, cache)
            W = body.fk()
            for nd in base_rotch: rot[nd][k] = body.Q[nd]
            hip_t[k] = body.T[hips]
            met['poleCosMax'] = max(met.get('poleCosMax', 0.0), res['poleCos'])
            met['reachErrM'] = max(met['reachErrM'], res['errBow'], res['errDraw'] if prm['draw'] >= gates.get('reachDrawFrom', 0.5) else 0.0)   # the string hand cannot reach the nock while the bow arm is already out: gated once it is on the string
            for fn, (e, tgt) in res['feet'].items():
                p_now = W[body.bone(fn)][1]
                feet_ref.setdefault(fn, p_now.copy())
                met['footSlideM'] = max(met['footSlideM'], float(np.linalg.norm((p_now - feet_ref[fn])[[0, 2]])), e)
                tb = W[body.bone(fn.replace('Foot', 'ToeBase'))][1]
                met['footMinY'] = min(met['footMinY'], float(min(p_now[1], tb[1])))
            for side, nm in ((cfg['bowHand'], 'bow'), (cfg['drawHand'], 'draw')):
                S, E, Wr = body.pos(side + 'Arm'), body.pos(side + 'ForeArm'), body.pos(side + 'Hand')
                ang = angle_between(S - E, Wr - E); met['elbowMin'] = min(met['elbowMin'], ang); met['elbowMax'] = max(met['elbowMax'], ang)
            if prm['draw'] > 0.999 and prm['hook'] > 0.99 and prm['raise'] > 0.99 and prm['relBack'] < 1e-6:
                Wd = body.pos(cfg['drawHand'] + 'Hand'); fd = res['aim_dir']
                tip = Wd + fd * cfg['arms']['stringHandBack'] + np.array([0, cfg['arms']['stringHandDown'], 0])
                met['anchorErrM'] = max(met['anchorErrM'] or 0.0, float(np.linalg.norm(tip - res['anchor'])))
            if prevq is not None:
                stp = [(L.qangle_deg(prevq[nd], body.Q[nd]), nd) for nd in base_rotch if nd in prevq and 'Hand' not in body.R.nodes[nd]['name'].replace('mixamorig:LeftHand', '', 1).replace('mixamorig:RightHand', '', 1) [:0] + 'x' and not any(f in body.R.nodes[nd]['name'] for f in ('HandIndex', 'HandMiddle', 'HandRing', 'HandPinky', 'HandThumb'))]
                step, snd = max(stp)
                if step > met['maxStepDeg']: met['maxStepDeg'] = step; met['worstStepBone'] = body.R.nodes[snd]['name']; met['worstStepT'] = round(t, 3)
            prevq = {nd: body.Q[nd].copy() for nd in base_rotch}
            if k == 0: first = prevq
            if clip['id'] == cfg['hold']['refClip'] and k == 0:
                Rb, pb, nrm = bow_world(body, cfg, res['aim_dir'])
                e, pu, err = to_unity_hold(body, cfg, Rb, pb)
                hold_info = {'holdRxDeg': round(e[0], 2), 'holdRyDeg': round(e[1], 2), 'holdRzDeg': round(e[2], 2),
                             'holdPx': round(float(pu[0]), 4), 'holdPy': round(float(pu[1]), 4), 'holdPz': round(float(pu[2]), 4), 'eulerRoundTripErr': round(err, 6)}
        if clip.get('loop'):
            met['loopCloseDeg'] = max(L.qangle_deg(rot[nd][0], rot[nd][-1]) for nd in base_rotch)
        met['frames'] = n; met['durationS'] = dur; met['contactTime'] = clip.get('contactTime')
        report['clips'][clip['id']] = {k: (round(v, 5) if isinstance(v, float) else v) for k, v in met.items()}
        anims[clip['id']] = (times, rot, hip_t)
        # gates
        fail = lambda msg: report['gatesFailed'].append('%s: %s' % (clip['id'], msg))
        if met['reachErrM'] > gates['reachErrMaxM']: fail('reach error %.3f m' % met['reachErrM'])
        if met['footSlideM'] > gates['footSlideMaxM']: fail('foot slide %.4f m' % met['footSlideM'])
        if met['footMinY'] < gates['footFloorMinM']: fail('foot below floor %.3f m' % met['footMinY'])
        if met.get('loopCloseDeg', 0) > gates['loopCloseMaxDeg']: fail('loop not closed %.2f deg' % met['loopCloseDeg'])
        if met['poleCosMax'] > gates.get('poleCosMax', 0.97): fail('elbow pole nearly parallel to the arm (cos %.2f): unstable elbow roll' % met['poleCosMax'])
        if met['maxStepDeg'] > gates['maxJointStepDeg']: fail('joint step %.1f deg/frame' % met['maxStepDeg'])
        if met['anchorErrM'] is not None and met['anchorErrM'] > gates['anchorErrMaxM']: fail('anchor error %.3f m' % met['anchorErrM'])
        if met['elbowMin'] < gates['elbowMinDeg']: fail('elbow too tight %.0f deg' % met['elbowMin'])
        if met['elbowMax'] > gates['elbowMaxDeg']: fail('elbow locked %.0f deg' % met['elbowMax'])
        print('%-12s frames %3d  reach %.4f  slide %.4f  step %.1f  elbow %.0f..%.0f  anchor %s' % (clip['id'], n, met['reachErrM'], met['footSlideM'], met['maxStepDeg'], met['elbowMin'], met['elbowMax'], met['anchorErrM']))
    report['hold'] = hold_info
    sheet = []
    if hold_info is not None:
        Rh_ = P.euler_to_m(hold_info['holdRxDeg'], hold_info['holdRyDeg'], hold_info['holdRzDeg']); M = np.diag([-1.0, 1, 1])
        Rg_hold = M @ Rh_ @ M; pg_hold = M @ np.array([hold_info['holdPx'], hold_info['holdPy'], hold_info['holdPz']])
        bs_, ds_ = cfg['bowHand'], cfg['drawHand']
        for pose in cfg.get('sheet', []):
            clip = [c for c in cfg['clips'] if c['id'] == pose['clip']][0]
            keys = key_params(cfg, clip); prm = eval_params(keys, pose['t'], clip.get('ease', 'smooth'))
            res = solve(body, cfg, prm, (pose['t'] / clip['duration']) % 1.0, cache)
            Hh = body.fk()[body.bone(bs_ + 'Hand')]
            Rb = Hh[0] @ Rg_hold; pb = Hh[1] + Hh[0] @ pg_hold
            fing = (body.pos(ds_ + 'HandIndex2') + body.pos(ds_ + 'HandMiddle2')) / 2
            sheet.append({'clip': pose['clip'], 't': pose['t'], 'fps': fps, 'label': pose.get('label', pose['clip']), 'bowR': Rb.tolist(), 'bowP': pb.tolist(), 'stringHand': fing.tolist(),
                          'gripWrist': Hh[1].tolist(), 'drawWrist': body.pos(ds_ + 'Hand').tolist(), 'head': body.pos('Head').tolist()})
    json.dump(sheet, open(os.path.join(out, 'out', 'sheet_poses.json'), 'w'), indent=1)
    # ---- write anim-only glb (bake format)
    j, b = body.j, body.b
    keep = ('name', 'children', 'translation', 'rotation', 'scale', 'matrix')
    oj = {'asset': {'version': '2.0', 'generator': 'bow_clips_build.py'}, 'scene': j.get('scene', 0), 'scenes': j['scenes'],
          'nodes': [{k: v for k, v in nd.items() if k in keep} for nd in j['nodes']], 'animations': [], 'accessors': [], 'bufferViews': []}
    ob = bytearray()

    def add_acc(arr, typ, minmax=False):
        arr = np.ascontiguousarray(arr.astype('<f4'))
        while len(ob) % 4: ob.append(0)
        off = len(ob); ob.extend(arr.tobytes())
        oj['bufferViews'].append({'buffer': 0, 'byteOffset': off, 'byteLength': arr.nbytes})
        a = {'bufferView': len(oj['bufferViews']) - 1, 'componentType': 5126, 'count': int(arr.shape[0]), 'type': typ}
        if minmax: a['min'] = [float(arr.min())]; a['max'] = [float(arr.max())]
        oj['accessors'].append(a); return len(oj['accessors']) - 1
    for cid, (times, rot, hip_t) in anims.items():
        an = {'name': cid, 'samplers': [], 'channels': []}
        for nd, q in sorted(rot.items()):
            ti = add_acc(times.reshape(-1, 1), 'SCALAR', True); vi = add_acc(q, 'VEC4')
            an['samplers'].append({'input': ti, 'output': vi, 'interpolation': 'LINEAR'}); an['channels'].append({'sampler': len(an['samplers']) - 1, 'target': {'node': nd, 'path': 'rotation'}})
        ti = add_acc(times.reshape(-1, 1), 'SCALAR', True); vi = add_acc(hip_t, 'VEC3')
        an['samplers'].append({'input': ti, 'output': vi, 'interpolation': 'LINEAR'}); an['channels'].append({'sampler': len(an['samplers']) - 1, 'target': {'node': hips, 'path': 'translation'}})
        oj['animations'].append(an)
    dst = os.path.join(out, 'Assets', 'Characters', folder.split('Assets/Characters/')[-1], '%s_bow_v1.glb' % char)
    os.makedirs(os.path.dirname(dst), exist_ok=True); L.save(oj, ob, dst)
    report['outGlb'] = os.path.relpath(dst, out).replace('\\', '/'); report['outBytes'] = os.path.getsize(dst)
    os.makedirs(os.path.join(out, 'out'), exist_ok=True)
    if want_preview:
        pj = json.loads(json.dumps(j)); pb_ = bytearray(b)
        pj['animations'] = []
        for an in oj['animations']:
            na = {'name': an['name'], 'samplers': [], 'channels': []}
            for s in an['samplers']:
                ids = []
                for ai in (s['input'], s['output']):
                    a = oj['accessors'][ai]; bv = oj['bufferViews'][a['bufferView']]
                    raw = bytes(ob[bv['byteOffset']:bv['byteOffset'] + bv['byteLength']])
                    while len(pb_) % 4: pb_.append(0)
                    off = len(pb_); pb_.extend(raw)
                    pj['bufferViews'].append({'buffer': 0, 'byteOffset': off, 'byteLength': len(raw)})
                    na_ = dict(a); na_['bufferView'] = len(pj['bufferViews']) - 1; pj['accessors'].append(na_); ids.append(len(pj['accessors']) - 1)
                na['samplers'].append({'input': ids[0], 'output': ids[1], 'interpolation': 'LINEAR'})
            na['channels'] = an['channels']; pj['animations'].append(na)
        pdst = os.path.join(out, 'out', '%s_bow_preview.glb' % char); L.save(pj, pb_, pdst); report['preview'] = os.path.relpath(pdst, out).replace('\\', '/')
    # bow fit per clip for the sheet (world matrices computed again in the contact-sheet script from the same maths)
    json.dump(report, open(os.path.join(out, 'out', 'bow_clips_report.json'), 'w'), indent=1)
    print('wrote', dst, os.path.getsize(dst), 'bytes;', 'gates failed:', report['gatesFailed'] or 'none')
    print('hold', hold_info)
    return report


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--proj', default=os.path.abspath(os.path.join(HERE, '..', '..', '..', '..')))
    ap.add_argument('--out', default=None); ap.add_argument('--char', default='Joan'); ap.add_argument('--preview', action='store_true'); ap.add_argument('--cfg', default=None)
    a = ap.parse_args()
    proj = a.proj
    out = a.out or os.path.abspath(os.path.join(HERE, '..', '..', 'stage_on'))
    # the Data file may be the staged copy
    cfgp = a.cfg or os.path.join(HERE, '..', 'Assets', 'Data', 'bow_clips.json')
    if not os.path.exists(cfgp): cfgp = os.path.join(proj, 'Assets', 'Data', 'bow_clips.json')
    build(proj, out, a.char, a.preview, cfgp)

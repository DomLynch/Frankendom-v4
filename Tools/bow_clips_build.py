"""BOWART bow_clips_build.py (v2): build the bow draw/hold/loose/special clips for a Mixamo-106 character from Assets/Data/bow_pose.json (pose targets) + bow_clips.json (clips, keys, gates)
(numpy only, CPU only).

  python bow_clips_build.py [--proj DIR] [--out DIR] [--char Joan] [--preview] [--cfg bow_clips.json] [--pose bow_pose.json]

Reads   <proj>/Assets/Data/bow_clips.json, bow_pose.json  (+ roster.json for the model glb)
Writes  <out>/Assets/Characters/<folder>/<id>_bow_v1.glb   animation-only glb (bake format: node tree of the combat glb, one animation per clip id)
        <out>/out/bow_clips_report.json                    CPU gates (incl. the TWIST gate) + the bow hold (Unity Euler / position in the bow-hand bone frame)
        <out>/out/sheet_poses.json                          poses for the Blender contact sheet
        --preview: <out>/out/<id>_bow_preview.glb          combat glb + the new clips (for the Blender contact sheet only, never shipped)
Never writes into <proj>. Idempotent.
v2: twist-free hinge arm chains (bow_pose.solve_arm), aim-frame data, stance from data, gates for roll between Arm/ForeArm/Hand."""
import argparse, json, math, os, sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import bow_pose as P
import clip_lib as L

PARAMS = ['raise', 'draw', 'drawExtra', 'hook', 'relBack', 'relOut', 'relDrop', 'lean', 'aimYaw', 'aimPitch', 'tremor', 'kick']
FINGER_BONES = ('HandIndex', 'HandMiddle', 'HandRing', 'HandPinky', 'HandThumb')


def jload(p): return json.load(open(p, encoding='utf-8-sig'))


def key_params(cfg, clip):
    """list of (t, dict) with every param resolved: key value, else clip default, else 0"""
    base = {n: 0.0 for n in PARAMS}
    like = None
    if clip.get('like'): like = [c for c in cfg['clips'] if c['id'] == clip['like']][0]
    src = like if like else clip
    for n in PARAMS:
        if n in clip: base[n] = float(clip[n])
        elif like and n in like: base[n] = float(like[n])
    out = []
    for k in src['keys']:
        d = dict(base)
        for n in PARAMS:
            if n in k: d[n] = float(k[n])
        if clip.get('like') and 'aimYaw' in clip: d['aimYaw'] = d['aimYaw'] if 'aimYaw' in k else float(clip['aimYaw'])
        out.append((float(k['t']), d))
    return out


def clip_stance(cfg, clip):
    like = [c for c in cfg['clips'] if c['id'] == clip['like']][0] if clip.get('like') else {}
    return clip.get('stance', like.get('stance', {}))


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


def nl(a, b, f):
    """blend two direction vectors and normalise (a small blended vector would swing a pole)"""
    return P.unit(P.unit(np.array(a, float)) * (1 - f) + P.unit(np.array(b, float)) * f)


def clavicle(body, side, toward_a_deg, up_deg, A, U):
    """rotate the Shoulder (clavicle) bone: + toward_a = the shoulder point moves towards the target, + up = it rises. the sign of the rotation is found by testing, so it does not depend on a bone convention"""
    sh, arm = side + 'Shoulder', side + 'Arm'
    for ang, tgt in ((toward_a_deg, A), (up_deg, U)):
        if abs(ang) < 1e-6: continue
        p0 = body.pos(arm).copy()
        if tgt is A: ax = U
        else:
            c = P.unit(body.pos(arm) - body.pos(sh)); ax = P.unit(np.cross(c, U))
        for sgn in (1.0, -1.0):
            body.rot_world(sh, P.rotAxis(ax, sgn * ang))
            if float(np.dot(body.pos(arm) - p0, tgt)) > 0: break
            body.rot_world(sh, P.rotAxis(ax, -sgn * ang))


def solve(body, cfg, pz, prm, phase, st_over, state, fix=None):
    """one frame. returns a metrics dict (the pose is left in body.Q / body.T)."""
    st = dict(pz['stance']); st.update(st_over or {})
    ar = pz['arms']; fg = pz['fingers']; hold = pz['hold']
    base = pz['base']; bs = pz['bowHand']; ds = pz['drawHand']
    body.load_base(base['state'], base['time'])
    bw0 = body.base_world
    hips0 = body.pos('Hips').copy()
    foot_R0 = {s: bw0[body.bone(s + 'Foot')][0].copy() for s in (bs, ds)}
    foot_y0 = {s: bw0[body.bone(s + 'Foot')][1][1] for s in (bs, ds)}
    head_R0 = bw0[body.bone('Head')][0].copy(); neck_R0 = bw0[body.bone('Neck')][0].copy()
    sgnB = side_sign(body, bs); sgnD = side_sign(body, ds)
    sw = float(np.linalg.norm(bw0[body.bone(bs + 'Arm')][1] - bw0[body.bone(ds + 'Arm')][1]))      # shoulder width (measured)
    fr0 = P.aim_frame(sgnB, 0.0)                         # stance frame: the feet never move with the aim
    yawF = P.yaw_deg(fr0['F']); toward = 1.0 if P.wrap_deg(0.0 - yawF) > 0 else -1.0
    yawAim = prm['aimYaw']
    chestYaw = yawF + toward * st['chestOpenDeg'] + st['chestAimShare'] * yawAim
    hipsYaw = yawF + toward * st['hipsOpenDeg'] + st['hipsAimShare'] * yawAim
    # ---- hips / spine
    body.set_world_pos('Hips', [hips0[0] + fr0['A'][0] * st['weightShiftM'], st['hipsY'], hips0[2] + fr0['A'][2] * st['weightShiftM']])
    body.rot_world('Hips', P.rotY(hipsYaw))
    for nm, shr in zip(('Spine', 'Spine1', 'Spine2'), st['spineShare']): body.rot_world(nm, P.rotY((chestYaw - hipsYaw) * shr))
    chest_fwd = P.rotY(chestYaw) @ np.array([0, 0, 1.0])
    if abs(prm['lean']) > 1e-6:
        ax = P.unit(np.cross([0, 1.0, 0], -chest_fwd)); h_before = body.pos('Head').copy()
        body.rot_world('Spine1', P.rotAxis(ax, prm['lean']))
        if float(np.dot(body.pos('Head') - h_before, -chest_fwd)) * np.sign(prm['lean']) < 0: body.rot_world('Spine1', P.rotAxis(ax, -2 * prm['lean']))
    # ---- feet / legs (planted, weight even, knees over the toes)
    width = st['feetWidthShoulders'] * sw
    cen = np.array([hips0[0], 0.0, hips0[2]]) + fr0['A'] * st['weightShiftM']
    feet = {}; legerr = 0.0
    for k, s in enumerate((bs, ds)):
        tgt = cen + fr0['A'] * (1.0 if k == 0 else -1.0) * width / 2; tgt[1] = foot_y0[s]
        fyaw = yawF + toward * st['footOpenDeg'][k]
        pole = P.rotY(fyaw) @ np.array([0, 0, 1.0]) + np.array([0, st['kneePoleUp'], 0])
        g = body.chain_geom(s + 'UpLeg', s + 'Leg', s + 'Foot', tgt, pole)
        body.place_chain(s + 'UpLeg', s + 'Leg', g, P.FLEX_LEG)
        body.set_world_rot(s + 'Foot', P.rotY(fyaw) @ foot_R0[s])
        feet[s + 'Foot'] = (g['err'], tgt); legerr = max(legerr, g['err'])
    # ---- head / neck: the face looks down the arrow
    yawHead = yawAim + st['headYawOffDeg']
    neckYaw = chestYaw + P.wrap_deg(yawHead - chestYaw) * st['neckYawShare']
    Rn = P.rotY(neckYaw) @ neck_R0; Rh = P.rotY(yawHead) @ head_R0
    pitch = st['headPitchDeg'] + st['headPitchFollow'] * prm['aimPitch']
    if abs(pitch) > 1e-6: Rh = P.rotAxis(np.cross([0, 1.0, 0], P.rotY(yawHead) @ [0, 0, 1.0]), -pitch) @ Rh
    if st['headTiltDeg']: Rh = P.rotAxis(P.rotY(yawHead) @ np.array([0, 0, 1.0]), st['headTiltDeg'] * sgnD) @ Rh
    body.set_world_rot('Neck', Rn); body.set_world_rot('Head', Rh)
    head_pos = body.pos('Head').copy()
    frH = P.aim_frame(sgnB, yawHead, 0.0)
    # ---- shoulders (clavicles)
    fr = P.aim_frame(sgnB, yawAim, prm['aimPitch']); A, U, F, Ap, w = fr['A'], fr['U'], fr['F'], fr['Ap'], fr['w']
    ab, ad = ar['bow'], ar['draw']
    clavicle(body, bs, ab['shoulderProtractDeg'], ab['shoulderElevateDeg'], A, U)
    clavicle(body, ds, -ad['shoulderRetractDeg'], ad['shoulderElevateDeg'], A, U)
    # ---- bow arm
    ease = P.smooth(prm['raise'])
    shB = body.pos(bs + 'Arm').copy()
    lenB = float(np.linalg.norm(body.pos(bs + 'ForeArm') - shB) + np.linalg.norm(body.pos(bs + 'Hand') - body.pos(bs + 'ForeArm')))
    aimT = shB + P.unit(Ap * lenB + U * ab['heightAboveShoulder']) * (lenB * ab['reach'])      # constant arm extension whatever the pitch (a few mm shorter would unlock the elbow)
    cy = ab['carry']; carryT = shB + w(cy['pos'])
    bowT = carryT * (1 - ease) + aimT * ease
    ph = 2 * math.pi * phase
    perp = P.unit(np.cross(Ap, U))
    bowT = bowT + prm['tremor'] * (perp * math.sin(ph) + U * math.sin(2 * ph + 0.7)) - Ap * prm['kick']
    fB = nl(w(cy['fingerDir']), w(ab['fingerDir']), ease)
    nB = nl(w(cy['palm']), w(ab['palm']), ease)
    if ab['palmRollDeg']: nB = P.rotAxis(fB, ab['palmRollDeg'] * sgnB) @ nB
    resB = P.solve_arm(body, bs, bowT, w(ab['pole']), None, fB, nB, ab['solver'], (fix or {}).get('B'), state.get('psiB'), state.get('rollB'))
    state['psiB'] = resB['psi']; state['rollB'] = resB['foreRollDeg']
    handB = body.fk()[body.bone(bs + 'Hand')]
    grip_dir = P.unit(handB[1] - body.pos(bs + 'ForeArm'))
    P_grip = handB[1] + grip_dir * hold['palmAlong']
    back = -Ap
    # ---- draw arm: string hand nock -> anchor -> release back past the ear -> drop
    d = prm['draw']; rd = prm['relDrop']
    nock = P_grip + back * ad['brace']
    anchor = head_pos + frH['w'](ad['anchor'])
    Pstr = nock * (1 - d) + anchor * d + back * (prm['relBack'] + prm['drawExtra']) + F * prm['relOut']
    shD = body.pos(ds + 'Arm').copy()
    dropW = shD + w(ad['dropTarget'])
    lerp = lambda a_, b_, f_: np.array(a_, float) * (1 - f_) + np.array(b_, float) * f_
    elbowT = shD + w(lerp(lerp(ad['elbowNock'], ad['elbowFull'], d), ad['elbowDrop'], rd))
    fD = nl(nl(ad['fingerNock'], ad['fingerFull'], d), ad['fingerDrop'], rd); fD = P.unit(w(fD))
    nD = P.unit(w(nl(nl(ad['palmNock'], ad['palmFull'], d), ad['palmDrop'], rd)))
    if ad['palmRollDeg']: nD = P.rotAxis(fD, ad['palmRollDeg'] * sgnD) @ nD
    # fingers (idempotent: reset to the base pose first)
    def finger_axes(side):
        i = body.bone(side + 'Hand'); mid = body.bone(side + 'HandMiddle1')
        f0 = P.unit(body.rest_world[mid][1] - body.rest_world[i][1])
        n0 = np.array([0, -1.0, 0]); n0 = P.unit(n0 - f0 * float(np.dot(n0, f0)))
        a_f = P.unit(np.cross(f0, n0))
        t1 = body.rest_world[body.bone(side + 'HandThumb2')][1] - body.rest_world[body.bone(side + 'HandThumb1')][1]
        a_t = P.unit(np.cross(P.unit(t1), n0))
        return a_f, a_t
    aB, tB = finger_axes(bs); aD, tD = finger_axes(ds)

    def curl_bow():
        body.reset_fingers(bs)
        for f in ('Index', 'Middle', 'Ring', 'Pinky'):
            body.curl([bs + 'Hand' + f + str(k) for k in (1, 2, 3)], fg['bow']['pinky' if f == 'Pinky' else 'finger'], aB)
        body.curl([bs + 'HandThumb' + str(k) for k in (1, 2, 3)], fg['bow']['thumb'], tB)

    def curl_draw():
        h = prm['hook']; body.reset_fingers(ds)
        for f in ('Index', 'Middle', 'Ring', 'Pinky'):
            a_h = np.array(fg['hook'][f.lower()], float); a_o = np.array(fg['open']['finger'], float)
            body.curl([ds + 'Hand' + f + str(k) for k in (1, 2, 3)], list(a_o * (1 - h) + a_h * h), aD)
        body.curl([ds + 'HandThumb' + str(k) for k in (1, 2, 3)], list(np.array(fg['open']['thumb'], float) * (1 - h) + np.array(fg['hook']['thumb'], float) * h), tD)
    curl_bow()
    wrist = (Pstr - fD * ad['hookDist']) * (1 - rd) + dropW * rd
    prevD = None; resD = None
    for _it in range(3):
        resD = P.solve_arm(body, ds, wrist, elbowT - shD, elbowT, fD, nD, ad['solver'], (fix or {}).get('D'), (prevD if prevD is not None else state.get('psiD')), state.get('rollD'), ad['wristNeutralDrop'] * rd); prevD = resD['psi']
        curl_draw()
        fing = (body.pos(ds + 'HandIndex2') + body.pos(ds + 'HandMiddle2')) / 2
        wrist = wrist + (Pstr - fing) * (1.0 - rd)       # corrects the string point; fades out as the hand lets go (no jump when relDrop crosses a threshold)
    state['psiD'] = prevD; state['rollD'] = resD['foreRollDeg']
    tw = {bs: P.twists(body, bs), ds: P.twists(body, ds)}
    return {'resB': resB, 'resD': resD, 'feet': feet, 'legErr': legerr, 'anchor': anchor, 'Pstr': Pstr, 'fing': fing, 'aim_dir': Ap, 'bowT': bowT, 'wristD': wrist, 'prm': prm,
            'twist': tw, 'shoulderWidth': sw, 'shD': shD, 'shB': shB, 'fr': fr}


def bow_world(body, pz, aim_dir):
    """bow glb (glTF-space) world frame fitted to the bow hand in the CURRENT pose: long axis = fist curl axis (vertical), string side towards the archer.
    returns R (3x3, columns = bow X,Y,Z in world), p (grip centre)"""
    bs = pz['bowHand']; ho = pz['hold']
    Hh = body.fk()[body.bone(bs + 'Hand')]
    fdir = P.unit(Hh[1] - body.pos(bs + 'ForeArm'))
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


def to_unity_hold(body, pz, Rb, pb):
    bs = pz['bowHand']; Hh = body.fk()[body.bone(bs + 'Hand')]
    Rg = Hh[0].T @ Rb; pg = Hh[0].T @ (pb - Hh[1])
    M = np.diag([-1.0, 1, 1]); Ru = M @ Rg @ M; pu = M @ pg
    e = P.euler_zxy_deg(Ru)
    chk = P.euler_to_m(*e); err = float(np.abs(chk - Ru).max())
    return e, pu, err


def angle_between(a, b):
    return math.degrees(math.acos(max(-1.0, min(1.0, float(np.dot(P.unit(a), P.unit(b)))))))


def sheet_times(clip):
    """contact sheet poses per clip (data: nothing hard-coded per clip): start, full draw (just before the release), release, end"""
    dur = float(clip['duration']); ct = clip.get('contactTime')
    if ct is None: return [('start', 0.0), ('mid', dur / 2), ('end', dur)]
    return [('start', 0.0), ('draw', max(0.0, ct - 0.04)), ('mid', ct + 0.08), ('end', dur)]


def build(proj, out, char, want_preview, cfg_path=None, pose_path=None):
    cfg = jload(cfg_path or os.path.join(proj, 'Assets', 'Data', 'bow_clips.json'))
    pz = jload(pose_path or os.path.join(os.path.dirname(cfg_path or os.path.join(proj, 'Assets', 'Data', 'x')), 'bow_pose.json'))
    if char not in cfg['characters']: raise SystemExit('BOWART: %s is not in bow_clips.json characters' % char)
    ros = jload(os.path.join(proj, 'Assets', 'Data', 'roster.json'))
    row = [r for r in ros['characters'] if r['id'] == char][0]
    glb = os.path.join(proj, row['modelGlb']); folder = os.path.dirname(row['modelGlb']).replace('\\', '/')
    body = P.Body(glb)
    fps = float(cfg['fps']); gates = cfg['gates']; tg = pz['twistGate']
    anims = {}; report = {'character': char, 'tool': 'bowart-2', 'clips': {}, 'gatesFailed': [], 'twistGate': {'maxDeg': tg['maxDeg'], 'shoulderMaxDeg': tg['shoulderMaxDeg'], 'shoulderSwingMaxDeg': tg['shoulderSwingMaxDeg']}}
    base_rotch = [n for (n, p) in body.R.anims[pz['base']['state']].keys() if p == 'rotation']
    hips = body.bone('Hips')
    hold_info = None
    bs, ds = pz['bowHand'], pz['drawHand']; fixes = {}
    for clip in cfg['clips']:
        keys = key_params(cfg, clip); dur = float(clip['duration']); n = int(round(dur * fps)) + 1
        st_over = clip_stance(cfg, clip)
        times = np.array([k / fps for k in range(n)]); times[-1] = dur
        rot = {nd: np.zeros((n, 4)) for nd in base_rotch}; hip_t = np.zeros((n, 3))
        met = {'reachErrM': 0.0, 'footSlideM': 0.0, 'footMinY': 9.0, 'maxStepDeg': 0.0, 'bowElbowMinDeg': 999.0, 'drawElbowMinDeg': 999.0, 'anchorErrM': None, 'legReachErrM': 0.0, 'kneeMedialM': -9.0,
               'twistElbowMaxDeg': 0.0, 'twistWristMaxDeg': 0.0, 'twistShoulderMaxDeg': 0.0, 'shoulderSwingMaxDeg': 0.0, 'shoulderFramesSkipped': 0, 'palmResidMaxDeg': 0.0, 'drawElbowDyM': None, 'feetWidthOverShoulders': None}
        feet_ref = {}; prevq = None; trace = []
        # pass 1 (greedy) records every frame's arm problem, dynamic programming then picks the elbow swivel of every frame so the arms take the short way round; pass 2 builds the clip
        state = {}; rec = {'B': [], 'D': []}
        for k in range(n):
            t = float(times[k]); ph = (k % (n - 1)) / (n - 1) if clip.get('loop') else 0.0
            r1 = solve(body, cfg, pz, eval_params(keys, t, clip.get('ease', 'smooth')), ph, st_over, state)
            rec['B'].append(r1['resB']['ctx']); rec['D'].append(r1['resD']['ctx'])
        psis = {'B': P.dp_psi(rec['B'], pz['arms']['bow']['solver'].get('wTrans', 0.02)), 'D': P.dp_psi(rec['D'], pz['arms']['draw']['solver'].get('wTrans', 0.02))}
        fixes[clip['id']] = psis
        state = {}
        for k in range(n):
            t = float(times[k]); ph = (k % (n - 1)) / (n - 1) if clip.get('loop') else 0.0
            prm = eval_params(keys, t, clip.get('ease', 'smooth'))
            res = solve(body, cfg, pz, prm, ph, st_over, state, {'B': psis['B'][k], 'D': psis['D'][k]})
            W = body.fk()
            for nd in base_rotch: rot[nd][k] = body.Q[nd]
            hip_t[k] = body.T[hips]
            met['reachErrM'] = max(met['reachErrM'], res['resB']['reachErr'], res['resD']['reachErr'] if prm['draw'] >= gates.get('reachDrawFrom', 0.5) and prm['relDrop'] < 0.5 else 0.0)
            met['legReachErrM'] = max(met['legReachErrM'], res['legErr']); met['kneeMedialM'] = max(met['kneeMedialM'], P.knee_medial(body))
            for fn, (e, tgt) in res['feet'].items():
                p_now = W[body.bone(fn)][1]
                feet_ref.setdefault(fn, p_now.copy())
                met['footSlideM'] = max(met['footSlideM'], float(np.linalg.norm((p_now - feet_ref[fn])[[0, 2]])), e)
                tb = W[body.bone(fn.replace('Foot', 'ToeBase'))][1]
                met['footMinY'] = min(met['footMinY'], float(min(p_now[1], tb[1])))
            if met['feetWidthOverShoulders'] is None:
                fw = float(np.linalg.norm((W[body.bone(bs + 'Foot')][1] - W[body.bone(ds + 'Foot')][1])[[0, 2]])); met['feetWidthOverShoulders'] = fw / res['shoulderWidth']
            for side in (bs, ds):
                S_, E_, Wr = body.pos(side + 'Arm'), body.pos(side + 'ForeArm'), body.pos(side + 'Hand')
                ang = angle_between(S_ - E_, Wr - E_)
                if side == bs and prm['raise'] > 0.99 and prm['kick'] < 0.001: met['bowElbowMinDeg'] = min(met['bowElbowMinDeg'], ang)
                if side == ds: met['drawElbowMinDeg'] = min(met['drawElbowMinDeg'], ang)
                tw = res['twist'][side]
                met['twistElbowMaxDeg'] = max(met['twistElbowMaxDeg'], abs(tw['elbow'])); met['twistWristMaxDeg'] = max(met['twistWristMaxDeg'], abs(tw['wrist']))
                met['shoulderSwingMaxDeg'] = max(met['shoulderSwingMaxDeg'], tw['shoulderSwing'])
                if tw['shoulderSwing'] <= tg['shoulderSwingMaxDeg']: met['twistShoulderMaxDeg'] = max(met['twistShoulderMaxDeg'], abs(tw['shoulder']))
                else: met['shoulderFramesSkipped'] += 1
            trace.append([round(t, 3), round(res['resB']['psi'], 1), round(res['resD']['psi'], 1), round(res['twist'][bs]['shoulder'], 1), round(res['twist'][ds]['shoulder'], 1), round(res['resD']['palmResidDeg'], 1)])
            met['palmResidMaxDeg'] = max(met['palmResidMaxDeg'], res['resB']['palmResidDeg'], res['resD']['palmResidDeg'] if prm['relDrop'] < 0.5 else 0.0)
            if prm['draw'] > 0.999 and prm['hook'] > 0.99 and prm['raise'] > 0.99 and prm['relBack'] < 1e-6 and prm['drawExtra'] < 1e-6 and prm['relDrop'] < 1e-6:
                met['anchorErrM'] = max(met['anchorErrM'] or 0.0, float(np.linalg.norm(res['fing'] - res['anchor'])))
                met['drawElbowDyM'] = float(res['resD']['elbow'][1] - res['shD'][1])
            if prevq is not None:
                stp = [(L.qangle_deg(prevq[nd], body.Q[nd]), nd) for nd in base_rotch if nd in prevq and not any(f in body.R.nodes[nd]['name'] for f in FINGER_BONES)]
                step, snd = max(stp)
                trace[-1].append([round(step, 1), body.R.nodes[snd]['name'].replace('mixamorig:', '')])
                if step > met['maxStepDeg']: met['maxStepDeg'] = step; met['worstStepBone'] = body.R.nodes[snd]['name']; met['worstStepT'] = round(t, 3)
            prevq = {nd: body.Q[nd].copy() for nd in base_rotch}
            if clip['id'] == pz['hold']['refClip'] and k == 0:
                Rb, pb, nrm = bow_world(body, pz, res['aim_dir'])
                e, pu, err = to_unity_hold(body, pz, Rb, pb)
                hold_info = {'holdRxDeg': round(e[0], 2), 'holdRyDeg': round(e[1], 2), 'holdRzDeg': round(e[2], 2),
                             'holdPx': round(float(pu[0]), 4), 'holdPy': round(float(pu[1]), 4), 'holdPz': round(float(pu[2]), 4), 'eulerRoundTripErr': round(err, 6)}
        if clip.get('loop'): met['loopCloseDeg'] = max(L.qangle_deg(rot[nd][0], rot[nd][-1]) for nd in base_rotch)
        met['frames'] = n; met['durationS'] = dur; met['contactTime'] = clip.get('contactTime')
        report['clips'][clip['id']] = {k: (round(v, 5) if isinstance(v, float) else v) for k, v in met.items()}
        report['clips'][clip['id']]['trace'] = trace
        anims[clip['id']] = (times, rot, hip_t)
        fail = lambda msg: report['gatesFailed'].append('%s: %s' % (clip['id'], msg))
        if met['reachErrM'] > gates['reachErrMaxM']: fail('reach error %.3f m' % met['reachErrM'])
        if met['legReachErrM'] > gates.get('legReachErrMaxM', 0.01): fail('leg cannot reach the planted feet by %.3f m (hipsY too high)' % met['legReachErrM'])
        if met['kneeMedialM'] > gates['kneeMedialMaxM']: fail('knees knocked in %.3f m > %.3f' % (met['kneeMedialM'], gates['kneeMedialMaxM']))
        if met['footSlideM'] > gates['footSlideMaxM']: fail('foot slide %.4f m' % met['footSlideM'])
        if met['footMinY'] < gates['footFloorMinM']: fail('foot below floor %.3f m' % met['footMinY'])
        if met.get('loopCloseDeg', 0) > gates['loopCloseMaxDeg']: fail('loop not closed %.2f deg' % met['loopCloseDeg'])
        if met['maxStepDeg'] > gates['maxJointStepDeg']: fail('joint step %.1f deg/frame (%s at %.2f s)' % (met['maxStepDeg'], met.get('worstStepBone'), met.get('worstStepT', 0)))
        if met['anchorErrM'] is not None and met['anchorErrM'] > gates['anchorErrMaxM']: fail('string hand %.3f m from the anchor' % met['anchorErrM'])
        if met['bowElbowMinDeg'] < gates['bowElbowMinDeg'] and met['bowElbowMinDeg'] < 999: fail('bow elbow not locked: %.0f deg' % met['bowElbowMinDeg'])
        if met['drawElbowMinDeg'] < gates['drawElbowMinDeg']: fail('draw elbow too tight %.0f deg' % met['drawElbowMinDeg'])
        if met['drawElbowDyM'] is not None and not (gates['drawElbowDyMinM'] <= met['drawElbowDyM'] <= gates['drawElbowDyMaxM']): fail('draw elbow %.2f m from shoulder height at full draw' % met['drawElbowDyM'])
        if not (gates['feetWidthMin'] <= met['feetWidthOverShoulders'] <= gates['feetWidthMax']): fail('feet %.2f x shoulder width' % met['feetWidthOverShoulders'])
        if met['twistElbowMaxDeg'] > tg['maxDeg']: fail('TWIST Arm->ForeArm %.1f deg > %.0f' % (met['twistElbowMaxDeg'], tg['maxDeg']))
        if met['twistWristMaxDeg'] > tg['maxDeg']: fail('TWIST ForeArm->Hand %.1f deg > %.0f' % (met['twistWristMaxDeg'], tg['maxDeg']))
        if met['twistShoulderMaxDeg'] > tg['shoulderMaxDeg']: fail('TWIST Shoulder->Arm %.1f deg > %.0f' % (met['twistShoulderMaxDeg'], tg['shoulderMaxDeg']))
        print('%-12s frames %3d  reach %.4f  slide %.4f  step %.1f  bowElbow %.0f  twist sh/el/wr %.0f/%.0f/%.0f  anchor %s  palmResid %.0f' % (clip['id'], n, met['reachErrM'], met['footSlideM'], met['maxStepDeg'], met['bowElbowMinDeg'], met['twistShoulderMaxDeg'], met['twistElbowMaxDeg'], met['twistWristMaxDeg'], met['anchorErrM'], met['palmResidMaxDeg']))
    report['hold'] = hold_info
    sheet = []
    if hold_info is not None:
        Rh_ = P.euler_to_m(hold_info['holdRxDeg'], hold_info['holdRyDeg'], hold_info['holdRzDeg']); M = np.diag([-1.0, 1, 1])
        Rg_hold = M @ Rh_ @ M; pg_hold = M @ np.array([hold_info['holdPx'], hold_info['holdPy'], hold_info['holdPz']])
        for clip in cfg['clips']:
            keys = key_params(cfg, clip); st_over = clip_stance(cfg, clip)
            for lab, tt in sheet_times(clip):
                prm = eval_params(keys, tt, clip.get('ease', 'smooth'))
                kf = min(len(fixes[clip['id']]['B']) - 1, int(round(tt * fps))); res = solve(body, cfg, pz, prm, (tt / clip['duration']) % 1.0 if clip.get('loop') else 0.0, st_over, {}, {'B': fixes[clip['id']]['B'][kf], 'D': fixes[clip['id']]['D'][kf]})
                Hh = body.fk()[body.bone(bs + 'Hand')]
                Rb = Hh[0] @ Rg_hold; pb = Hh[1] + Hh[0] @ pg_hold
                sheet.append({'clip': clip['id'], 't': round(tt, 3), 'fps': fps, 'label': '%s %s t=%.2f' % (clip['id'], lab, tt), 'bowR': Rb.tolist(), 'bowP': pb.tolist(), 'stringHand': res['fing'].tolist(),
                              'gripWrist': Hh[1].tolist(), 'drawWrist': body.pos(ds + 'Hand').tolist(), 'head': body.pos('Head').tolist(), 'twist': {s: {k2: round(v, 1) for k2, v in res['twist'][s].items()} for s in res['twist']}})
    os.makedirs(os.path.join(out, 'out'), exist_ok=True)
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
    json.dump(report, open(os.path.join(out, 'out', 'bow_clips_report.json'), 'w'), indent=1)
    print('wrote', dst, os.path.getsize(dst), 'bytes;', 'gates failed:', report['gatesFailed'] or 'none')
    print('hold', hold_info)
    return report


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--proj', default=os.path.abspath(os.path.join(HERE, '..', '..', '..', '..')))
    ap.add_argument('--out', default=None); ap.add_argument('--char', default='Joan'); ap.add_argument('--preview', action='store_true'); ap.add_argument('--cfg', default=None); ap.add_argument('--pose', default=None)
    a = ap.parse_args()
    proj = a.proj
    out = a.out or os.path.abspath(os.path.join(HERE, '..', '..', 'stage_on'))
    cfgp = a.cfg or os.path.join(HERE, '..', 'Assets', 'Data', 'bow_clips.json')
    if not os.path.exists(cfgp): cfgp = os.path.join(proj, 'Assets', 'Data', 'bow_clips.json')
    posep = a.pose or os.path.join(os.path.dirname(cfgp), 'bow_pose.json')
    build(proj, out, a.char, a.preview, cfgp, posep)

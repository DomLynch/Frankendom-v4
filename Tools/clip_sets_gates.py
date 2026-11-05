"""CLIPSETS gates for attacks / dodges / finishers (offline, CPU, numpy). Re-uses clip_gates.py (Metrics, pose_diff, clip_metrics, rig_audit, knee) of laneCLIPBUILD.
Per clip, new (rebuilt) vs live (what the project plays today) vs raw (the source on its own skeleton):
  G-pose   bone orientation (hips frame) vs the RAW source: mean / max degrees. Plain clips: limits poseMeanDeg / poseMaxDeg (leg bones skipped where the leg fix ran).
           Posture-fixed attacks (declared hips cap, feet IK, blade aim): limits postureMeanDeg / postureMaxDeg, and they must not move away from the live bake by more than liveDiffMeanDeg.
  G-knee   inward knee offset (m, + = knock-kneed) on every sampled frame <= kneeMedialMaxM
  G-toe    toe yaw on frames where BOTH feet are planted (toe height within plantedM of rest) and the torso is upright: mean L/R vs the raw source <= toeYawTolDeg
           (toeYawTolModifiedDeg when the clip is declared modified: posture fix or leg fix). No planted frames = n/a (rolls, dives, falls).
  G-plant  toe never sinks below the floor: min over frames of (toe height - rest toe height) >= -footFloorMaxM
  G-sword  blade segment (forearm axis, 0.95 m) vs own head sphere >= 0 on every frame (same approximation as laneCLIPBUILD)
  G-rig    every file of the character's clip sets carries all 106 joint names (clip_gates.rig_audit)
"""
import json, math, os, sys
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import clip_lib as L
import clip_gates as G
import leg_fix


_REST = {}


def rest_yaw(M):
    """toe yaw of the rig's own rest pose (same measure), so rigs with different foot geometry are compared by how far the foot turned, not by absolute bone offsets"""
    k = id(M)
    if k not in _REST:
        W = M.rig.world(None, 0.0); out = {}
        lat = G.unit(M.P(W, 'LeftUpLeg') - M.P(W, 'RightUpLeg')); lat = G.unit(np.array([lat[0], 0, lat[2]])); fwd = np.cross(lat, np.array([0, 1.0, 0]))
        for s in ('Left', 'Right'):
            v = M.P(W, s + 'ToeBase') - M.P(W, s + 'Foot'); v = np.array([v[0], 0, v[2]]); out[s] = math.degrees(math.atan2(float(np.dot(v, lat)), float(np.dot(v, fwd))))
        _REST[k] = out
    return _REST[k]


def planted_toe(M, anim, t, plantedM, check=True):
    """(yawL, yawR) in degrees relative to the pelvis heading, or None when the frame is not planted+upright"""
    W = M.rig.world(anim, t); r = M.rest
    toeY = {s: float(M.P(W, s + 'ToeBase')[1]) - r['toe'][s + 'Y'] for s in ('Left', 'Right')}
    if check and max(toeY.values()) > plantedM: return None
    lat = G.unit(M.P(W, 'LeftUpLeg') - M.P(W, 'RightUpLeg')); lat = G.unit(np.array([lat[0], 0, lat[2]]))
    up = np.array([0, 1.0, 0]); fwd = np.cross(lat, up)
    tor = G.unit(M.P(W, 'Neck') - M.P(W, 'Hips'))
    if check and tor[1] < 0.85: return None
    out = []
    for s in ('Left', 'Right'):
        v = M.P(W, s + 'ToeBase') - M.P(W, s + 'Foot'); v = np.array([v[0], 0, v[2]])
        out.append(math.degrees(math.atan2(float(np.dot(v, lat)), float(np.dot(v, fwd)))) - rest_yaw(M)[s])
    return out


def planted_times(M, anim, n, plantedM):
    return [t for t in np.linspace(0, M.rig.duration(anim), n) if planted_toe(M, anim, t, plantedM)]


def toe_stats(M, anim, ts):
    """mean toe yaw change (deg, vs the rig's own rest) over the given times (the frames where the REBUILT clip is planted+upright; raw and live are read at the same times)"""
    vals = [y for y in (planted_toe(M, anim, min(t, M.rig.duration(anim)), 0, False) for t in ts) if y]
    if not vals: return None
    v = np.array(vals); return {'L': float(v[:, 0].mean()), 'R': float(v[:, 1].mean()), 'frames': len(vals)}


def pose_vs(Ma, a_anim, Mb, b_anim, n, skip_legs):
    dur = min(Ma.rig.duration(a_anim), Mb.rig.duration(b_anim)); vals = []
    for t in np.linspace(0, dur, n):
        vals.append(G.pose_diff(Ma, Ma.frame(a_anim, t), Mb, Mb.frame(b_anim, t), skip_legs))
    return {'mean': float(np.mean([v[0] for v in vals])), 'max': float(np.max([v[1] for v in vals])), 'finger': float(np.mean([v[2] for v in vals]))}


def run(cfg, ccfg, roster, proj, cid, reps, outdir):
    g = dict(ccfg['gates']); g.update(cfg.get('gates', {})); n = int(g.get('samplesPerClip', 36))
    allbad = []; allrev = []; allinh = []; table = []; rig_files = []
    for setname in ('attacks', 'attacks_raw', 'dodges', 'finishers'):
        if setname not in reps: continue
        rep = reps[setname]; S = cfg['sets'][setname]
        jn, bn = L.load(rep['file']); rn = L.Rig(jn, bn); Mn = G.Metrics(rn)
        livep = os.path.join(proj, S['live'].format(id=cid)) if S.get('live') else (reps['finishers_live']['file'] if 'finishers_live' in reps else os.path.join(proj, [c for c in roster['characters'] if c['id'] == cid][0]['modelGlb']))   # PITCLIPS: no live attack/dodge clip of these names -> live column = None
        jl, bl = L.load(livep); rl = L.Rig(jl, bl); Ml = G.Metrics(rl)
        rows = {r['state']: r for r in S['rows'][cid]}
        rig_files += [rep['file'], livep]
        for info in rep['clips']:
            st = info['state']; row = rows[st]; bad = []
            jr, br = L.load(row['src']); rr = L.Rig(jr, br); Mr = G.Metrics(rr); an = list(rr.anims)[0]
            modified = bool(info.get('posture')) or info.get('legFix') == 'applied'
            posture = bool(info.get('posture'))
            has_live = st in rl.anims   # PITCLIPS
            mn = G.clip_metrics(Mn, None, st, g, n); ml = G.clip_metrics(Ml, None, st, g, n) if has_live else None
            mn['knee'] = G.knee_for(jn, bn, st)
            if ml: ml['knee'] = G.knee_for(jl, bl, st)
            skip = info.get('legFix') == 'applied' or posture
            pn = pose_vs(Mn, st, Mr, an, n, skip); pl = pose_vs(Ml, st, Mr, an, n, skip) if has_live else {'mean': float('nan'), 'max': float('nan'), 'finger': float('nan')}
            pnl = pose_vs(Mn, st, Ml, st, n, True) if st in rl.anims else None
            pts = planted_times(Mn, st, n, g['plantedM']); tn = toe_stats(Mn, st, pts); tl = toe_stats(Ml, st, pts) if has_live else None; tr = toe_stats(Mr, an, pts)
            rowd = {'set': setname, 'state': st, 'duration': info['durationS'], 'frames': info['frames'], 'legFix': info.get('legFix'), 'swivel': info.get('legFixMaxSwivelDeg', 0.0), 'posture': posture,
                    'kneePre': info['kneeMedialPreFixM'], 'new': mn, 'live': ml, 'poseNew': pn, 'poseLive': pl, 'poseNewVsLive': pnl, 'toeNew': tn, 'toeLive': tl, 'toeRaw': tr, 'inherited': []}
            inh = []    # violations the live clip has too (same source behaviour): reported as INHERITED, not as a regression
            def lim(msg, new_v, live_v, bad_if, tol=g['inheritTolM']):
                if not bad_if: return
                if live_v is not None and (new_v <= live_v + tol if msg.startswith('G-knee') else new_v >= live_v - tol): inh.append(msg)
                else: bad.append(msg)
            if posture:      # declared modification (Joan's own posture rows): the rebuilt clip must reproduce the live bake, deviation from raw is reported
                if pnl and pnl['mean'] > g['liveDiffMeanDeg']: bad.append('G-pose %s differs from the live bake by mean %.1f deg (> %.0f)' % (st, pnl['mean'], g['liveDiffMeanDeg']))
            elif pn['mean'] > g['poseMeanDeg'] or pn['max'] > g['poseMaxDeg']: bad.append('G-pose %s mean %.2f max %.2f vs raw (limits %.1f/%.1f)' % (st, pn['mean'], pn['max'], g['poseMeanDeg'], g['poseMaxDeg']))
            lim('G-knee %s inward %.3f m > %.3f' % (st, mn['knee'], g['kneeMedialMaxM']), mn['knee'], ml['knee'] if ml else None, mn['knee'] is not None and mn['knee'] > g['kneeMedialMaxM'])
            if tn and tr:
                ref = tl if (posture and tl) else tr; tol = g['toeYawTolModifiedDeg'] if (info.get('legFix') == 'applied' and not posture) else g['toeYawTolDeg']
                dl = abs(tn['L'] - ref['L']); dr = abs(tn['R'] - ref['R'])
                if dl > tol or dr > tol: bad.append('G-toe %s planted-frame yaw L %.1f/R %.1f vs %s %.1f/%.1f (> %.0f)' % (st, tn['L'], tn['R'], 'live' if ref is tl else 'raw', ref['L'], ref['R'], tol))
            lim('G-plant %s toe %.3f m below the floor line' % (st, mn['floorMin']), mn['floorMin'], ml['floorMin'] if ml else None, mn['floorMin'] < -g['footFloorMaxM'])
            lim('G-sword %s blade %.3f m inside the head sphere' % (st, mn['sword']), mn['sword'], ml['sword'] if ml else None, mn['sword'] < g['sword']['minClearanceM'])
            rowd['inherited'] = inh
            rowd['review'] = []
            if S.get('reviewOnly'): rowd['review'] = bad; allrev += ['%s/%s' % (setname, x) for x in bad]; bad = []
            rowd['failed'] = bad; table.append(rowd); allbad += ['%s/%s' % (setname, x) for x in bad]; allinh += ['%s/%s' % (setname, x) for x in inh]
            print('  %-9s %-18s pose %.2f/%.1f (live %.2f/%.1f) knee %+.3f (live %+.3f, pre %+.3f) toe %s plant %+.3f (live %+.3f) sword %+.3f (live %+.3f)%s' % (
                setname, st, pn['mean'], pn['max'], pl['mean'], pl['max'], mn['knee'], (ml['knee'] if ml else float('nan')), info['kneeMedialPreFixM'], ('%.0f/%.0f' % (tn['L'], tn['R'])) if tn else 'n/a', mn['floorMin'], (ml['floorMin'] if ml else float('nan')), mn['sword'], (ml['sword'] if ml else float('nan')), '  FAIL' if bad else ('  (inherited %d)' % len(inh) if inh else '')))
    # G-rig over every output file + the live ones + the old finisher sources
    ra = dict(ccfg['gates']['rigAudit']); ra['files'] = [f for f in dict.fromkeys(rig_files) if os.path.exists(f)] + [os.path.join(proj, 'Tools/finisher_src_legfix/*.glb')]
    c2 = {'gates': {'rigAudit': ra}}
    body = os.path.join(proj, [c for c in roster['characters'] if c['id'] == cid][0]['modelGlb'])
    rig_rows, rb = G.rig_audit(c2, proj, body)
    new_files = set(os.path.normpath(r['file']) for r in reps.values())
    rb_new = [x for x in rb if any(os.path.basename(f) in x for f in new_files)]
    for x in rig_rows: print('  RIG %-4s %s  %s' % ('106' if x['is106'] else 'NOT', x['file'].replace(proj, '.'), x['kind']))
    allbad += rb_new
    json.dump({'character': cid, 'failed': allbad, 'rig': rig_rows, 'inherited': allinh, 'review': allrev, 'rows': table}, open(os.path.join(outdir, '%s_sets_gates.json' % cid), 'w'), indent=1, default=lambda o: float(o) if hasattr(o, '__float__') else str(o))
    print('%s set gates: %d failed, %d inherited from the source (live clip has the same violation)' % (cid, len(allbad), len(allinh)))
    for x in allinh: print('  INH ', x)
    for x in allrev: print('  REVIEW-ONLY', x)
    for x in allbad: print('  FAIL', x)
    return allbad

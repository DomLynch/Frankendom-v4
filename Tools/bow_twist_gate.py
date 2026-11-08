"""BOWART bow_twist_gate.py: independent TWIST gate on a finished animation glb (CPU, numpy). Reads the glb that will be baked (not the solver's own numbers).

  python bow_twist_gate.py <anim.glb or preview.glb> <bow_pose.json> <report.json out> [clip prefix, default bow_]

For every frame of every clip it measures, for the left and right arm, the roll of each bone against its parent ABOUT THE BONE'S OWN AXIS (swing-twist split of the local rotation relative to the
T-pose rest rotation; axis = the direction of the first child bone in the bone's frame):
  elbow  = Arm -> ForeArm      must stay <= twistGate.maxDeg (20)   candy-wrapper at the elbow
  wrist  = ForeArm -> Hand     must stay <= twistGate.maxDeg (20)   candy-wrapper at the wrist
  shoulder = Shoulder -> Arm   (only frames whose arm swing is <= twistGate.shoulderSwingMaxDeg: past that the arm points back across the body and any roll number is ill-defined; the skipped frame count is reported)  must stay <= twistGate.shoulderMaxDeg (humerus rotation, larger by nature; measured from the minimal-swing orientation, see bow_pose.roll_minswing)
Exit code 1 and a FAIL line per clip when a limit is exceeded."""
import sys, os, json, math
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import bow_pose as P          # also puts the project Tools folder (clip_lib) on the path
import clip_lib as L

MX = 'mixamorig:'
CHAIN = (('shoulder', 'Arm', 'ForeArm'), ('elbow', 'ForeArm', 'Hand'), ('wrist', 'Hand', 'HandMiddle1'))


def run(glb, pose_json, out_json, prefix='bow_'):
    pz = json.load(open(pose_json, encoding='utf-8-sig')); tg = pz['twistGate']
    j, b = L.load(glb); rig = L.Rig(j, b)
    res = {}; bad = []
    for name, ch in rig.anims.items():
        if not name.startswith(prefix): continue
        per = {}
        for side in ('Left', 'Right'):
            for key, bone, child in CHAIN:
                i = rig.idx[MX + side + bone]; c = rig.idx[MX + side + child]
                axis = P.unit(np.array(rig.rt[c], float)); Rr = L.q2m(rig.rq[i])
                if (i, 'rotation') not in ch: continue
                ts, vs = ch[(i, 'rotation')]
                worst = 0.0; wt = 0.0; swmax = 0.0; skipped = 0
                for t, q in zip(ts, vs):
                    Rl = L.q2m(q)
                    if key == 'shoulder':
                        sw = math.degrees(math.acos(max(-1.0, min(1.0, float(np.dot(Rr[:, 1], Rl[:, 1]))))))
                        swmax = max(swmax, sw)
                        if sw > tg['shoulderSwingMaxDeg']: skipped += 1; continue
                        tw = abs(P.roll_minswing(Rr, Rl))
                    else: tw = abs(P.twist_deg(Rr.T @ Rl, axis))
                    if tw > worst: worst = tw; wt = float(t)
                per['%s.%s' % (side, key)] = {'maxDeg': round(worst, 2), 'atS': round(wt, 3)}
                if key == 'shoulder': per['%s.%s' % (side, key)].update({'swingMaxDeg': round(swmax, 1), 'framesSkippedAboveSwing': skipped})
        res[name] = per
        lim = {'shoulder': tg['shoulderMaxDeg'], 'elbow': tg['maxDeg'], 'wrist': tg['maxDeg']}
        for k, v in per.items():
            if v['maxDeg'] > lim[k.split('.')[1]]: bad.append('%s %s twist %.1f deg > %.0f (at %.2f s)' % (name, k, v['maxDeg'], lim[k.split('.')[1]], v['atS']))
    json.dump({'glb': os.path.basename(glb), 'limits': {'elbowWristMaxDeg': tg['maxDeg'], 'shoulderMaxDeg': tg['shoulderMaxDeg']}, 'clips': res, 'failed': bad}, open(out_json, 'w'), indent=1)
    print('TWIST GATE', 'PASS' if not bad else 'FAIL: ' + '; '.join(bad))
    for name, per in res.items():
        print('  %-12s %s' % (name, '  '.join('%s %.1f' % (k, v['maxDeg']) for k, v in sorted(per.items()))))
    return not bad


if __name__ == '__main__':
    ok = run(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else 'bow_')
    sys.exit(0 if ok else 1)

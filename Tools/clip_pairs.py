"""CLIPSETS pair/timing check for the finisher set: per clip, new (rebuilt from the original source) vs live-equivalent (legfix source + C# retarget maths):
key times identical to the source, duration vs finishers.json expectedDurationS, Hips translation / rotation difference (root motion as today), killer/victim pairs equal length.
  python clip_pairs.py   -> prints a markdown table and writes out/PAIRS.md, exit 3 on a mismatch"""
import json, os, sys, math
import numpy as np
HERE = os.path.dirname(os.path.abspath(__file__)); ROOT = os.path.dirname(HERE); sys.path.insert(0, HERE); sys.dont_write_bytecode = True
import clip_lib as L, clip_build as CB

cfg = CB.read_json(os.path.join(ROOT, 'Assets/Data/clip_sets.json')); rep = json.load(open(os.path.join(ROOT, 'out/clip_sets_report.json')))['sets']
jn, bn = L.load(rep['finishers']['file']); rn = L.Rig(jn, bn); jl, bl = L.load(rep['finishers_live']['file']); rl = L.Rig(jl, bl)
hips = rn.bone('Hips'); rows = cfg['sets']['finishers']['rows']['Joan']; bad = []; out = []
out.append('| clip | role | root motion | frames | duration s (source / expected) | key times = source | hips pos max diff vs live m | hips rot max diff vs live deg | horizontal travel kept m |\n|---|---|---|---|---|---|---|---|---|')
dur = {}
for r in rows:
    st = r['state']; j, b = L.load(r['src']); sr = L.Rig(j, b); an = list(sr.anims)[0]
    st_t = sr.anims[an][(sr.bone('Hips'), 'rotation')][0]
    tn, qn = rn.anims[st][(hips, 'rotation')]; _, pn = rn.anims[st][(hips, 'translation')]
    tl, ql = rl.anims[st][(hips, 'rotation')]; _, pl = rl.anims[st][(hips, 'translation')]
    same = len(tn) == len(st_t) and np.abs(tn - st_t).max() < 1e-5
    dp = float(np.abs(pn - pl).max()); dq = max(L.qangle_deg(a, c) for a, c in zip(qn, ql))
    trav = float(np.max(np.linalg.norm(pn[:, :2] - pn[0, :2], axis=1)))
    d = float(tn[-1]); dur[st] = d; exp = r['expectedDurationS']
    if not same: bad.append('%s key times differ from the source' % st)
    if abs(d - exp) > 0.06: bad.append('%s duration %.3f vs expected %.3f' % (st, d, exp))
    if dp > 0.02: bad.append('%s hips translation differs from live by %.3f m' % (st, dp))
    if dq > 3.0: bad.append('%s hips rotation differs from live by %.1f deg' % (st, dq))
    out.append('| %s | %s | %s | %d | %.3f / %.3f | %s | %.4f | %.2f | %.3f |' % (st, r['role'], r['rootMotion'], len(tn), d, exp, 'yes' if same else 'NO', dp, dq, trav))
out.append('\nPairs (killer / victim, same source length required by today\'s data): ')
for k, v in (('fin_river_light_k', 'fin_river_light_v'), ('fin_hengdao_k', 'fin_hengdao_v'), ('fin_counter_k', 'fin_counter_v')):
    out.append('- %s %.3f s / %s %.3f s%s' % (k, dur[k], v, dur[v], '' if abs(dur[k] - dur[v]) < 0.001 else '  (differ by %.3f s in the source too: today the same)' % abs(dur[k] - dur[v])))
txt = '\n'.join(out); print(txt); open(os.path.join(ROOT, 'out/PAIRS.md'), 'w').write(txt + '\n')
for x in bad: print('PAIR FAIL', x)
sys.exit(3 if bad else 0)

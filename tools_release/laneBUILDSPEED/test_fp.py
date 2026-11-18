#!/usr/bin/env python3
r"""test_fp.py - proves the fingerprint rules on a SCRATCH copy (FP_ROOT), never on Assets. 100 synthetic zones: change 1 -> exactly 1 reruns.
python -I test_fp.py   (about 1-2 min; copies only the files the steps hash, into _agents\laneBUILDSPEED\scratch)"""
import json, os, shutil, subprocess, sys, time
from pathlib import Path
LANE = Path(__file__).resolve().parent; REAL = LANE.parents[1]; S = LANE / 'scratch' / time.strftime('%m%d_%H%M%S')   # new folder every run: this lane never deletes
sys.path.insert(0, str(LANE))
os.environ['FP_ROOT'] = str(REAL)
import fp
cfg = fp.CFG
want = set()
for st in cfg['steps'].values():
    rels, miss, lits = fp.step_inputs(st); want |= rels
    for g in st.get('outputs', []): want |= {f.relative_to(REAL).as_posix() for f in fp.expand(g)}
    for g in st.get('zoneRules', {}).get('globalInputs', []):
        if '#' not in g: want |= {f.relative_to(REAL).as_posix() for f in fp.expand(g)}
want |= {'Assets/Data/zone_span.json', 'Assets/Data/props_layout_zone2.json', 'Assets/Data/props_layout_hub.json'}
big = [w for w in want if (REAL / w).stat().st_size > 20 * 1024 * 1024]
want -= set(big)   # scenes / big glbs are represented by tiny stand-ins below
for w in want:
    d = S / w; d.parent.mkdir(parents=True, exist_ok=True); shutil.copyfile(REAL / w, d)
for w in big:
    d = S / w; d.parent.mkdir(parents=True, exist_ok=True); d.write_bytes(('stand-in for ' + w).encode())
zs = json.loads((S / 'Assets/Data/zone_span.json').read_text(encoding='utf-8-sig'))
rows = zs['zones']; base = rows[0]
for i in range(len(rows), 100):
    r = json.loads(json.dumps(base)); r['id'] = 'z%03d' % i; r['root'] = 'Z%03d' % i; r['layout'] = 'props_layout_z%03d.json' % i; r['offsetZ'] = 36.0 * (i + 1); rows.append(r)
for r in rows:
    lp = S / 'Assets/Data' / r['layout']
    if not lp.exists(): lp.write_text(json.dumps({'version': 2, 'rows': [r['id']]}), encoding='utf-8')
(S / 'Assets/Data/zone_span.json').write_text(json.dumps(zs), encoding='utf-8')
env = dict(os.environ, FP_ROOT=str(S))
def run(*a):
    t = time.time(); o = subprocess.run([sys.executable, '-I', str(LANE / 'fp.py')] + list(a), capture_output=True, text=True, env=env).stdout; return o, time.time() - t
def ran(o): return [l.split()[1] for l in o.splitlines() if l.startswith('RUN')]
ok = True
def check(name, got, exp):
    global ok; good = (got == exp); ok &= good; print('%-4s %-46s got %s' % ('PASS' if good else 'FAIL', name, got))
o, t = run('plan'); check('fresh: everything RUN', len(ran(o)), 102)
run('record', 'all', '--apply')
o, t = run('plan'); check('after record: nothing RUN (%.1fs for 100 zones)' % t, ran(o), [])
lay = S / 'Assets/Data/props_layout_z042.json'; lay.write_text(lay.read_text() + ' ', encoding='utf-8')
o, t = run('plan'); check('edit layout of zone 42 -> only zones:z042', ran(o), ['zones:z042'])
run('record', 'zones:z042', '--apply')
zp = S / 'Assets/Data/zone_span.json'; z = json.loads(zp.read_text()); z['zones'][7]['offsetZ'] += 1; zp.write_text(json.dumps(z))
o, t = run('plan'); check('edit row 7 of zone_span -> only zones:z007', ran(o), ['zones:' + z['zones'][7]['id']])
run('record', 'all', '--apply')
b = json.loads(zp.read_text()); b['build']['colliderName'] = 'X'; zp.write_text(json.dumps(b))
o, t = run('plan'); check('edit zone_span build block -> all 100 zones', len(ran(o)), 100)
run('record', 'all', '--apply')
mon = S / 'Assets/Data/attack_clips.json'; mon.write_text(mon.read_text(encoding='utf-8-sig') + ' ', encoding='utf-8')
o, t = run('plan'); check('edit attack_clips.json -> monster only', ran(o), ['monster'])
run('record', 'all', '--apply')
sc = S / 'Assets/Scenes/HadesSlice.unity'; sc.write_bytes(b'changed')
o, t = run('plan'); check('scene changed (output of chain) -> monster/zones stay SKIP, player RUN', ran(o), ['player'])
print('ALL PASS' if ok else 'SOME FAIL'); sys.exit(0 if ok else 1)

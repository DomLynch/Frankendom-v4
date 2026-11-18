#!/usr/bin/env python3
r"""fp.py - laneBUILDSPEED fingerprint engine for the Frankendom build chain. Reads steps.json (nothing hard-coded here).
NEVER writes Assets; only its own _state\fingerprints.json and (plan --write-skip) Tools\zone_skip.json. 'record' needs --apply (dry run otherwise).

  python -I fp.py scan [step]              inputs found per step (declared + literals scanned out of the code files), counts
  python -I fp.py plan [--force a,b]       per step / per zone: RUN or SKIP and WHY, and which Unity launches are needed
  python -I fp.py plan --write-skip        also write Tools\zone_skip.json {"clean":[zone roots]} for the Zone2Build hunk
  python -I fp.py record <step|step:zoneId|all> --apply     store the CURRENT input + output hashes as 'last good' (call after the step succeeded)
  python -I fp.py status                   what is stored
  python -I fp.py time                     hashing cost of the whole plan
"""
import argparse, glob, hashlib, json, os, re, time
from pathlib import Path
LANE = Path(__file__).resolve().parent; P = Path(os.environ.get('FP_ROOT', str(LANE.parents[1])))   # FP_ROOT = scratch copy for tests
CFG = json.loads((LANE / 'steps.json').read_text(encoding='utf-8'))
STATE = P / CFG['stateFile']
LIT = re.compile(r'"((?:Assets/|Data/)[^"\\]*?\.[A-Za-z0-9]{2,6}|[A-Za-z0-9_\-/]+\.json)"')
LIST_CACHE = {}; HASH_CACHE = {}


def sha_file(p):
    k = str(p)
    if k in HASH_CACHE: return HASH_CACHE[k]
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''): h.update(b)
    HASH_CACHE[k] = h.hexdigest(); return HASH_CACHE[k]


def expand(pat):
    key = pat.replace('\\', '/').split('#')[0]
    if key not in LIST_CACHE:
        LIST_CACHE[key] = sorted(Path(x) for x in glob.glob(str(P / key), recursive=True) if Path(x).is_file() and not x.endswith('.meta'))
    return LIST_CACHE[key]


def jhash(path, frag):
    d = json.loads(Path(path).read_text(encoding='utf-8-sig'))
    return hashlib.sha256(json.dumps(d.get(frag), sort_keys=True).encode()).hexdigest()


def scan_code(files):
    found, miss = set(), set()
    for c in files:
        f = P / c
        if not f.exists(): miss.add(c); continue
        for m in LIT.finditer(f.read_text(encoding='utf-8-sig', errors='replace')):
            s = m.group(1)
            cand = [P / s] if s.startswith('Assets/') else [P / CFG['dataDir'] / s.split('Data/', 1)[-1], P / 'Assets' / s]
            ok = [x for x in cand if x.is_file()]
            if ok: found.add(ok[0].relative_to(P).as_posix())
    return sorted(found), sorted(miss)


def hash_set(rels, fragments=()):
    h = hashlib.sha256(); n = 0
    for r in sorted(set(rels)):
        p = P / r
        if p.is_file(): h.update(r.encode()); h.update(sha_file(p).encode()); n += 1
    for rel, frag in fragments: h.update((rel + '#' + frag).encode()); h.update(jhash(P / rel, frag).encode())
    return h.hexdigest(), n


def step_inputs(st):
    code = [c for c in st.get('code', []) if (P / c).exists()]
    lits, miss = scan_code(st.get('code', []))
    rels = set(code) | set(lits)
    for g in st.get('extraInputs', []):
        for f in expand(g): rels.add(f.relative_to(P).as_posix())
    rels -= set(x.replace(chr(92), '/') for x in st.get('excludeInputs', []))
    return rels, miss, lits


def layout_refs(layout_rel):
    p = P / layout_rel; out = set()
    if not p.is_file(): return out
    txt = p.read_text(encoding='utf-8-sig', errors='replace')
    for m in re.finditer(r'"([^"\\]+?\.(?:glb|gltf|fbx|prefab|mat|png|jpg|asset))"', txt):
        s = m.group(1).replace('\\', '/')
        for cand in (s, 'Assets/' + s, 'Assets/Env/' + s):
            if (P / cand).is_file(): out.add(cand); break
    return out


def out_hash(st):
    rels = []
    for g in st.get('outputs', []): rels += [f.relative_to(P).as_posix() for f in expand(g)]
    return hash_set(rels)[0] if rels else ''


def load_state():
    return json.loads(STATE.read_text(encoding='utf-8')) if STATE.exists() else {}


def current(force=()):
    S = load_state(); R = []
    for name, st in CFG['steps'].items():
        rels, miss, lits = step_inputs(st); of = out_hash(st)
        if st.get('perZone'):
            ZR = st['zoneRules']; d = json.loads((P / st['zoneSpanFile']).read_text(encoding='utf-8-sig'))
            g = set(rels); gf = []
            for gi in ZR.get('globalInputs', []):
                if '#' in gi: gf.append(tuple(gi.split('#')))
                else:
                    for f in expand(gi): g.add(f.relative_to(P).as_posix())
            for row in d[ZR['rowKey']]:
                zid = row[ZR['idKey']]; lay = ZR['layoutDir'] + '/' + row[ZR['layoutKey']]
                z = set(g); z.add(lay); z |= layout_refs(lay)
                hh, n = hash_set(z, gf)
                fp = hashlib.sha256((hashlib.sha256(json.dumps(row, sort_keys=True).encode()).hexdigest() + hh).encode()).hexdigest()
                key = name + ':' + zid; last = S.get(key); why = None
                if name in force or key in force or 'all' in force: why = 'forced'
                elif last is None: why = 'never recorded'
                elif last['fp'] != fp: why = 'inputs changed (zone row, layout, its assets or shared zone inputs)'
                R.append(dict(step=name, unit=zid, root=row[ZR['rootKey']], key=key, fp=fp, out=of, n=n + 1, run=why is not None, why=why or 'inputs unchanged'))
        else:
            hh, n = hash_set(rels); last = S.get(name); why = None
            if name in force or 'all' in force: why = 'forced'
            elif last is None: why = 'never recorded'
            elif last['fp'] != hh: why = 'inputs changed'
            elif last.get('out') and last['out'] != of: why = 'output changed since the last run (someone edited it)'
            R.append(dict(step=name, unit=name, key=name, fp=hh, out=of, n=n, run=why is not None, why=why or 'inputs and outputs unchanged'))
    return R


def main():
    ap = argparse.ArgumentParser(); ap.add_argument('cmd'); ap.add_argument('arg', nargs='?'); ap.add_argument('--apply', action='store_true')
    ap.add_argument('--force', default=''); ap.add_argument('--write-skip', action='store_true'); a = ap.parse_args()
    force = set(x for x in a.force.split(',') if x)
    if a.cmd == 'scan':
        for name, st in CFG['steps'].items():
            if a.arg and a.arg != name: continue
            rels, miss, lits = step_inputs(st)
            print('%-8s %4d input files (code %d, scanned literals %d); missing code files: %s' % (name, len(rels), len(st.get('code', [])), len(lits), miss or '-'))
            for l in lits[:50]: print('    scanned:', l)
    elif a.cmd == 'plan':
        R = current(force)
        for r in R: print('%-5s %-26s %s  (%d input files hashed)' % ('RUN' if r['run'] else 'SKIP', r['key'], r['why'], r['n']))
        mon = [r for r in R if r['step'] == 'monster'][0]; zs = [r for r in R if r['step'] == 'zones']; pl = [r for r in R if r['step'] == 'player'][0]
        dirty = [z['unit'] for z in zs if z['run']]
        print('LAUNCH monster : %s' % ('yes' if mon['run'] else 'no (whole Unity start + batch skipped)'))
        print('LAUNCH zones   : %s' % (('yes, rebuild only ' + ','.join(dirty)) if dirty else 'no (all %d zones clean)' % len(zs)))
        print('LAUNCH player  : %s' % ('yes' if pl['run'] else 'no code/scene/asset change: if only data changed use deploy_data.ps1'))
        if a.write_skip:
            clean = [z['root'] for z in zs if not z['run']]
            (P / 'Tools' / 'zone_skip.json').write_text(json.dumps({'clean': clean, 'written': time.strftime('%Y-%m-%d %H:%M:%S'), 'expiresEpoch': int(time.time()) + 2 * 3600}, indent=1), encoding='utf-8')
            print('wrote Tools/zone_skip.json clean=%s' % clean)
    elif a.cmd == 'record':
        R = current(); S = load_state(); n = 0
        for r in R:
            if a.arg in ('all', r['step'], r['key']):
                if not a.apply: print('DRY would record', r['key'], r['fp'][:12]); continue
                S[r['key']] = dict(fp=r['fp'], out=r['out'], t=time.strftime('%Y-%m-%d %H:%M:%S')); n += 1
        if a.apply:
            STATE.parent.mkdir(parents=True, exist_ok=True); STATE.write_text(json.dumps(S, indent=1), encoding='utf-8'); print('recorded', n)
    elif a.cmd == 'status':
        S = load_state()
        for k, v in S.items(): print(k, v['t'], v['fp'][:12])
        if not S: print('nothing recorded')
    elif a.cmd == 'time':
        t = time.time(); R = current(); print('plan hashing: %.0f ms for %d units' % ((time.time() - t) * 1000, len(R)))


if __name__ == '__main__': main()

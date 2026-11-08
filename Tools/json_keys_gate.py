#!/usr/bin/env python3
"""json_keys_gate.py (laneFIXGATES) - reverse of missing_keys.py: every key in a StreamingAssets json should have a reader.
Read-only. A key is 'known' when its exact spelling appears as an identifier or string literal in
  runtime   Assets\\Scripts\\**\\*.cs
  editor    Assets\\Editor\\**\\*.cs (+ Editor_URP~)
  tools     Tools\\*.py *.ps1 *.cs *.json *.bat (not json_keys_allow.json)
or the key is on the allow list (Tools\\json_keys_allow.json: {"file.json": ["key", ...] | "*"}), or starts with '_' (comment keys),
or is id-like (the same spelling also occurs as a string VALUE somewhere in the data: row ids, clip names, character ids).
Also lists whole json files that no source and no other json names (orphans), accepting composed names (a literal ending in '_' that prefixes the file stem).
LEVEL: WARN. Exit 0 unless --strict (exit 1 on any unknown key / orphan) or --max-unknown N (exit 1 when the count grows past N: a ratchet against future drift).
  python json_keys_gate.py [--sa DIR] [--scripts DIR]... [--editor DIR]... [--tools DIR] [--allow FILE] [--report FILE] [--top N] [--strict] [--max-unknown N] [--quiet]
"""
import argparse, json, os, re, sys
HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.dirname(HERE)
if not os.path.isdir(os.path.join(PROJ, 'ProjectSettings')):
    PROJ = r'C:\Users\dom12\Documents\unity_test_hdrp'   # run from the lane folder: read the live project
ap = argparse.ArgumentParser()
ap.add_argument('--sa'); ap.add_argument('--scripts', action='append'); ap.add_argument('--editor', action='append'); ap.add_argument('--tools')
ap.add_argument('--allow'); ap.add_argument('--report'); ap.add_argument('--top', type=int, default=15)
ap.add_argument('--strict', action='store_true'); ap.add_argument('--max-unknown', type=int); ap.add_argument('--quiet', action='store_true')
a = ap.parse_args()
SA = a.sa or os.path.join(PROJ, 'Assets', 'StreamingAssets')
SCR = a.scripts or [os.path.join(PROJ, 'Assets', 'Scripts')]
EDI = a.editor or [os.path.join(PROJ, 'Assets', 'Editor'), os.path.join(PROJ, 'Assets', 'Editor_URP~')]
TOOLS = a.tools or os.path.join(PROJ, 'Tools')
ALLOW = a.allow or os.path.join(TOOLS, 'json_keys_allow.json')
ident = re.compile(r'[A-Za-z_]\w*')
lit = re.compile(r'"((?:\\.|[^"\\])*)"')


def read(p):
    try:
        return open(p, encoding='utf-8-sig', errors='replace').read()
    except OSError:
        return ''


def walk(root, exts, shallow=False):
    for d, ds, fs in os.walk(root):
        if shallow:
            ds[:] = []
        for f in fs:
            if f.lower().endswith(exts):
                yield os.path.join(d, f)


def scan(paths):
    names, lits, text = set(), set(), []
    for p in paths:
        t = read(p)
        text.append(t)
        names.update(ident.findall(t))
        lits.update(lit.findall(t))
    return names, lits, '\n'.join(text)


rt, rt_lit, rt_text = scan([p for r in SCR for p in walk(r, ('.cs',))])
ed, ed_lit, ed_text = scan([p for r in EDI for p in walk(r, ('.cs',))])
tl_files = [p for p in walk(TOOLS, ('.py', '.ps1', '.cs', '.json', '.bat'), shallow=True) if os.path.basename(p) != 'json_keys_allow.json']
tl, tl_lit, tl_text = scan(tl_files)
allow = {}
try:
    allow = json.loads(read(ALLOW)) if os.path.exists(ALLOW) else {}
except Exception:
    allow = {}
allow = {k: v for k, v in allow.items() if not k.startswith('_')}


def keys_of(o, out, path=''):
    if isinstance(o, dict):
        for k, v in o.items():
            out.append((k, path))
            keys_of(v, out, (path + '/' + k) if len(path) < 60 else path)
    elif isinstance(o, list):
        for v in o:
            keys_of(v, out, path + '[]')


def values_of(o, out):
    if isinstance(o, dict):
        for v in o.values():
            values_of(v, out)
    elif isinstance(o, list):
        for v in o:
            values_of(v, out)
    elif isinstance(o, str):
        out.add(o)


files = sorted(os.path.join(SA, f) for f in os.listdir(SA) if f.lower().endswith('.json'))
docs, values = {}, set()
for p in files:
    try:
        docs[p] = json.loads(read(p))
    except Exception:
        print('WARN unparsable json: ' + os.path.basename(p))
        continue
    values_of(docs[p], values)
tot = 0
unk = []
classes = {'runtime': 0, 'editor': 0, 'tools': 0, 'allowed': 0, 'idlike': 0, 'underscore': 0, 'unknown': 0}
casehint = []
lower_rt = {}
for t in rt:
    lower_rt.setdefault(t.lower(), t)
for p, d in docs.items():
    fn = os.path.basename(p)
    seen = set()
    ks = []
    keys_of(d, ks)
    al = allow.get(fn, [])
    for k, path in ks:
        if k in seen:
            continue
        seen.add(k)
        tot += 1
        if k.startswith('_'):
            classes['underscore'] += 1
        elif k in rt:
            classes['runtime'] += 1
        elif k in ed:
            classes['editor'] += 1
        elif k in tl:
            classes['tools'] += 1
        elif al == '*' or k in al:
            classes['allowed'] += 1
        elif k in values:
            classes['idlike'] += 1
        else:
            classes['unknown'] += 1
            unk.append((fn, k, path))
            if k.lower() in lower_rt:
                casehint.append((fn, k, lower_rt[k.lower()]))
prefixes = {l for l in (rt_lit | ed_lit | tl_lit) if l.endswith('_') and len(l) >= 4}
orph = []
all_val_text = '\n'.join(values)
for p in files:
    fn = os.path.basename(p)
    st = fn[:-5]
    named = (st in rt_text) or (st in ed_text) or (st in tl_text) or (st in all_val_text)
    if not named:
        named = any(st.startswith(x) for x in prefixes)
    if not named:
        orph.append(fn)
by = {}
for fn, k, path in unk:
    by.setdefault(fn, []).append(k)
out = []
out.append('json_keys_gate: %d json files, %d distinct keys (per file): runtime %d, editor %d, tools %d, allowed %d, id-like %d, _comment %d, UNKNOWN %d' % (
    len(docs), tot, classes['runtime'], classes['editor'], classes['tools'], classes['allowed'], classes['idlike'], classes['underscore'], classes['unknown']))
for fn in sorted(by, key=lambda f: -len(by[f]))[:a.top]:
    out.append('  WARN %-34s %3d unknown: %s%s' % (fn, len(by[fn]), ', '.join(by[fn][:8]), ' ...' if len(by[fn]) > 8 else ''))
for fn, k, r in casehint[:20]:
    out.append('  WARN case-typo? %s key "%s" ~ code "%s"' % (fn, k, r))
for fn in orph:
    out.append('  WARN orphan json (named by no source, tool or other json): ' + fn)
out.append('RESULT: %s  (%d unknown keys, %d orphan files; WARN level)' % ('WARN' if classes['unknown'] or orph else 'PASS', classes['unknown'], len(orph)))
if not a.quiet:
    print('\n'.join(out))
else:
    print(out[0])
    print(out[-1])
if a.report:
    json.dump({'summary': classes, 'unknown': unk, 'orphans': orph, 'caseHints': casehint}, open(a.report, 'w'), indent=1)
rc = 0
if a.strict and (classes['unknown'] or orph):
    rc = 1
if a.max_unknown is not None and classes['unknown'] > a.max_unknown:
    rc = 1
    print('FAIL: unknown keys %d > allowed %d' % (classes['unknown'], a.max_unknown))
sys.exit(rc)

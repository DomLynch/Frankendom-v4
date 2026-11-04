#!/usr/bin/env python
"""Validate the P2 impact data against the real project (no Unity needed).
Usage: python Tools/validate_hit_reactions.py [--assets <path to Assets>] [--data <folder with the 3 json>] [--matrix]
Checks Assets/Data/hit_reactions.json, blood.json, cues.json:
  hit_reactions: every `state`/`fallback` exists in BOTH controllers (FAIL); every `prefer` that is not imported yet is a WARN (lists the clips still to import);
    every (character x location x direction x weight) resolves to a row (FAIL if none); move rows name real moves in move-defs.json; knockdown rows have an existing fallback.
  blood: ids and victim names valid, multipliers >= 0, ground colour dark (rgb <= 0.25: Dom's rule, no glowing blood).
  cues: every cue exists in CueBus.Manifest, events are known, every event CueBus raises has an enabled row (else CueBus keeps its built-in mapping), moves exist.
Exit 1 on any FAIL. --matrix prints the resolved state for every combination (now / after the preferred clips are imported)."""
import json, re, sys, os, itertools
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
A = os.path.join(ROOT, 'Assets')
if '--assets' in sys.argv: A = sys.argv[sys.argv.index('--assets') + 1]
D_NEW = sys.argv[sys.argv.index('--data') + 1] if '--data' in sys.argv else None   # folder holding the three new json files (default Assets/Data): lets the wip copies be checked before merging
def jl(p):
    with open(p, encoding='utf-8-sig') as f: return json.load(f)
def states(ctrl):
    txt = open(os.path.join(A, 'Characters', ctrl + '.controller'), encoding='utf-8').read(); out = set()
    for blk in re.split(r'(?m)^--- ', txt):
        if blk.startswith('!u!1102'):
            m = re.search(r'(?m)^  m_Name: (.+)$', blk)
            if m: out.add(m.group(1).strip())
    return out
fails, warns = [], []
S = {c: states(c) for c in ('Dracula', 'Pitborn')}
D = os.path.join(A, 'Data')
moves = {m['id']: m for m in jl(os.path.join(D, 'move-defs.json'))['moves']}
LOCS, DIRS, WTS = ('head', 'torso', 'arm', 'legs'), ('front', 'left', 'right', 'back'), ('light', 'heavy', 'kick')

# ---------------- hit_reactions.json ----------------
hr = jl(os.path.join(D_NEW or D, 'hit_reactions.json'))
rows = hr['rows']
def match(rv, v): return rv in ('', '*', None) or rv == v
def score(rv): return 0 if rv in ('', '*', None) else 1
def pick(ch, loc, dr, wt):
    best, bs = None, -1
    for r in rows:
        if not (match(r['character'], ch) and match(r['location'], loc) and match(r['direction'], dr) and match(r['weight'], wt)): continue
        s = score(r['character']) * 8 + score(r['location']) * 4 + score(r['direction']) * 2 + score(r['weight'])
        if s >= bs: best, bs = r, s
    return best
for i, r in enumerate(rows):
    tag = 'hit_reactions row %d (%s/%s/%s/%s)' % (i, r['character'], r['location'], r['direction'], r['weight'])
    if r['location'] not in ('*', '') + LOCS: fails.append(tag + ': bad location')
    if r['direction'] not in ('*', '') + DIRS: fails.append(tag + ': bad direction')
    if r['weight'] not in ('*', '') + WTS: fails.append(tag + ': bad weight')
    if r['character'] not in ('*', '', 'Dracula', 'Pitborn'): fails.append(tag + ': bad character')
    for ch in S:
        if not match(r['character'], ch): continue
        for k in ('state', 'fallback'):
            n = r.get(k, '')
            if n and n not in S[ch]: fails.append('%s: %s "%s" not in %s.controller' % (tag, k, n, ch))
        if not r.get('state'): fails.append(tag + ': empty state')
        if r.get('knockdown') and r.get('fallback', '') not in S[ch]: fails.append(tag + ': knockdown row needs an existing fallback state')
pending = {}
for ch in S:
    for loc, dr, wt in itertools.product(LOCS, DIRS, WTS):
        r = pick(ch, loc, dr, wt)
        if r is None: fails.append('no hit_reactions row for %s %s/%s/%s' % (ch, loc, dr, wt)); continue
        p = r.get('prefer', '')
        if p and p not in S[ch]: pending.setdefault(p, set()).add(ch)
if hr.get('defaultState') not in S['Dracula'] or hr.get('defaultState') not in S['Pitborn']: fails.append('defaultState missing in a controller')
for p in sorted(pending): warns.append('prefer "%s" not imported yet in %s (the row plays its `state` instead)' % (p, '+'.join(sorted(pending[p]))))
seen_dirs = set()
for mr in hr['moves']:
    if mr.get('id') not in (None, '', '*') and mr['id'] not in moves: warns.append('hit_reactions moves[]: id "%s" not in move-defs.json' % mr['id'])
    if mr.get('swingDirection'): seen_dirs.add(mr['swingDirection'])
    if mr.get('location') and mr['location'] not in LOCS: fails.append('hit_reactions moves[]: bad location ' + mr['location'])
for mid, m in moves.items():
    if m.get('direction') and m['direction'] not in seen_dirs and not any(x.get('id') == mid for x in hr['moves']):
        warns.append('move %s swingDirection "%s" has no moves[] row (falls to the "*" torso row)' % (mid, m['direction']))
if '--matrix' in sys.argv:
    print('%-8s %-6s %-6s %-6s -> state (prefer)' % ('char', 'loc', 'dir', 'wt'))
    for ch in S:
        for loc, dr, wt in itertools.product(LOCS, DIRS, WTS):
            r = pick(ch, loc, dr, wt)
            if r: print('%-8s %-6s %-6s %-6s -> %s%s%s' % (ch, loc, dr, wt, r['state'], (' (' + r['prefer'] + ')') if r.get('prefer') else '', ' KNOCKDOWN(off)' if r.get('knockdown') else ''))

# ---------------- blood.json ----------------
b = jl(os.path.join(D_NEW or D, 'blood.json'))
for k, ids in (('weights', WTS), ('locations', LOCS)):
    got = {x['id'] for x in b[k]}
    for i in ids:
        if i not in got: fails.append('blood.%s missing id %s' % (k, i))
    for x in b[k]:
        if x['id'] not in ids: fails.append('blood.%s unknown id %s' % (k, x['id']))
        for f in ('count', 'drops', 'speed', 'up', 'mist', 'ground'):
            if x.get(f, 1) < 0: fails.append('blood.%s.%s.%s negative' % (k, x['id'], f))
names = {v['match'] for v in b['victims']}
for n in ('Dracula', 'Pitborn'):
    if n not in names: fails.append('blood.victims missing ' + n)
for v in b['victims']:
    if v['match'] == 'Dracula' and (v.get('sparks') or v.get('flash')): fails.append('blood: Dracula (flesh) must not spark / flash (Dom rule)')
g = b['ground']
if max(g['color'][:3]) > 0.25: fails.append('blood.ground.color too bright (%s): blood stays dark and non-glowing' % g['color'])
if g['alpha'] > 0.95 or g['max'] > 64: warns.append('blood.ground alpha/max high')

# ---------------- cues.json ----------------
cs = open(os.path.join(A, 'Scripts', 'Combat', 'CueBus.cs'), encoding='utf-8-sig').read()
manifest = set(re.findall(r'\{\s*"([a-z_]+)",\s*new\[\]', cs))
cu = jl(os.path.join(D_NEW or D, 'cues.json'))
EVTS = {'hit', 'block', 'block_perfect', 'parry', 'guard_break', 'attack_start', 'attack_miss', 'roll', 'backstep', 'sidestep', 'kill'}
LAYERS = {'impact', 'voice', 'base'}
for i, r in enumerate(cu['rows']):
    tag = 'cues row %d (%s/%s/%s)' % (i, r['evt'], r['weapon'], r['surface'])
    if r['evt'] not in EVTS: fails.append(tag + ': unknown event')
    if r.get('layer', 'impact') not in LAYERS: fails.append(tag + ': bad layer')
    if not r['play'] and not r.get('disabled'): fails.append(tag + ': empty play')
    for p in r['play']:
        if p['cue'] not in manifest: fails.append('%s: cue "%s" not in CueBus.Manifest' % (tag, p['cue']))
        if p.get('gain', 1) < 0 or p.get('gain', 1) > 1.5: fails.append(tag + ': gain out of range')
        if p.get('who') not in ('', None, 'attacker', 'defender'): fails.append(tag + ': bad who')
    for m in r.get('moves', []):
        if m not in moves: warns.append('%s: move "%s" not in move-defs.json' % (tag, m))
    for k, vals in (('weapon', ('sword', 'maul')), ('surface', ('flesh', 'armour')), ('weight', WTS)):
        if r[k] not in ('*', '') + vals: fails.append('%s: bad %s %s' % (tag, k, r[k]))
for e in EVTS:
    if not any(r['evt'] == e and not r.get('disabled') for r in cu['rows']): warns.append('cues: no enabled row for event "%s" (CueBus keeps its built-in mapping)' % e)
weps = {w['character']: w['weapon'] for w in cu['weapons']}; surf = {w['character']: w['surface'] for w in cu['surfaces']}
if set(weps) != {'Dracula', 'Pitborn'} or set(surf) != {'Dracula', 'Pitborn'}: fails.append('cues weapons/surfaces must name Dracula and Pitborn')
off = [r for r in cu['rows'] if r.get('disabled')]
if off: warns.append('cues: %d disabled stopgap row(s) (pain / light-swing effort): enable after a listen test' % len(off))

print('Dracula states: %d  Pitborn states: %d  hit rows: %d  cue rows: %d  Manifest cues: %d' % (len(S['Dracula']), len(S['Pitborn']), len(rows), len(cu['rows']), len(manifest)))
for w in warns: print('WARN ' + w)
for f in fails: print('FAIL ' + f)
print('FAILS: %d  WARNS: %d' % (len(fails), len(warns)))
sys.exit(1 if fails else 0)

#!/usr/bin/env python
"""Validate the P2 impact data against the real project (no Unity needed).
HCKIT1 v93 (HC4 K1): ROSTER-DRIVEN. No character name lives in this script: the characters, their controllers, weapons and armour come from Assets/Data/roster.json, and the vocabularies
(locations, directions, weights, events, surfaces) and the blood-colour limit come from Tools/validate_rules.json (built-in defaults = the old inline values when the file is missing).
A new enemy = one roster row (+ its rows in hit_reactions / blood / cues): this script then checks it with no code edit.

Usage: python Tools/validate_hit_reactions.py [--assets <path to Assets>] [--data <folder with the 3 json>] [--rules <validate_rules.json>] [--matrix]
Checks Assets/Data/hit_reactions.json, blood.json, cues.json:
  hit_reactions: every `state`/`fallback` exists in the controller of EVERY roster character the row applies to (FAIL); every `prefer` that is not imported yet is a WARN; every
    (roster character x location x direction x weight) resolves to a row (FAIL if none); move rows name real moves in move-defs.json; knockdown rows have an existing fallback.
  blood: ids and victim names valid, multipliers >= 0, a victims[] row for every roster character, armoured=false characters never spark / flash (flesh), ground colour dark (limit from the rules file).
  cues: every cue exists in CueBus.Manifest, events are known, every event CueBus raises has an enabled row, moves exist, weapons[] / surfaces[] name exactly the roster characters and agree with the roster (weapon, armoured).
Exit 1 on any FAIL. --matrix prints the resolved state for every combination (now / after the preferred clips are imported)."""
import json, re, sys, os, itertools
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
A = os.path.join(ROOT, 'Assets')
if '--assets' in sys.argv: A = sys.argv[sys.argv.index('--assets') + 1]
D_NEW = sys.argv[sys.argv.index('--data') + 1] if '--data' in sys.argv else None   # folder holding the three new json files (default Assets/Data): lets the wip copies be checked before merging
RULES_P = sys.argv[sys.argv.index('--rules') + 1] if '--rules' in sys.argv else os.path.join(os.path.dirname(os.path.abspath(__file__)), 'validate_rules.json')
def jl(p):
    with open(p, encoding='utf-8-sig') as f: return json.load(f)

# ---------------- rules (data) ----------------
RULES = {   # = the values this script had inline before v93
    'locations': ['head', 'torso', 'arm', 'legs'], 'directions': ['front', 'left', 'right', 'back'], 'weights': ['light', 'heavy', 'kick'],
    'events': ['hit', 'block', 'block_perfect', 'parry', 'guard_break', 'attack_start', 'attack_miss', 'roll', 'backstep', 'sidestep', 'kill'],
    'cueLayers': ['impact', 'voice', 'base'], 'surfaces': ['flesh', 'armour'], 'cueGainMax': 1.5,
    'blood': {'groundColorMax': 0.25, 'groundAlphaMax': 0.95, 'groundCountMax': 64}}
if os.path.exists(RULES_P):
    r = jl(RULES_P)
    for k, v in r.items():
        if k.startswith('_'): continue
        if isinstance(v, dict) and isinstance(RULES.get(k), dict): RULES[k].update({a: b for a, b in v.items() if not a.startswith('_')})
        else: RULES[k] = v
LOCS, DIRS, WTS = tuple(RULES['locations']), tuple(RULES['directions']), tuple(RULES['weights'])
EVTS, LAYERS, SURFACES = set(RULES['events']), set(RULES['cueLayers']), tuple(RULES['surfaces'])
BL = RULES['blood']

fails, warns = [], []
D = os.path.join(A, 'Data')

# ---------------- roster (data) ----------------
roster = jl(os.path.join(D, 'roster.json'))
CHARS = [c for c in roster['characters'] if c.get('id')]
IDS = [c['id'] for c in CHARS]
RC = {c['id']: c for c in CHARS}
def surface_of(c): return SURFACES[1] if c.get('armoured') else SURFACES[0]   # armoured = metal: 'armour', else 'flesh'
def states(ctrl_path):
    p = os.path.join(A, ctrl_path.split('Assets/', 1)[-1].replace('/', os.sep))   # roster paths start with "Assets/"
    if not os.path.exists(p): return None
    txt = open(p, encoding='utf-8').read(); out = set()
    for blk in re.split(r'(?m)^--- ', txt):
        if blk.startswith('!u!1102'):
            m = re.search(r'(?m)^  m_Name: (.+)$', blk)
            if m: out.add(m.group(1).strip())
    return out
S = {}
for c in CHARS:
    st = states(c.get('controller', ''))
    if st is None: fails.append('roster %s: controller "%s" not found' % (c['id'], c.get('controller', ''))); st = set()
    S[c['id']] = st
moves = {m['id']: m for m in jl(os.path.join(D, 'move-defs.json'))['moves']}

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
    if r['character'] not in ('*', '') + tuple(IDS): fails.append(tag + ': bad character (not a roster id)')
    for ch in S:
        if not match(r['character'], ch): continue
        for k in ('state', 'fallback'):
            n = r.get(k, '')
            if n and n not in S[ch]: fails.append('%s: %s "%s" not in %s controller (%s)' % (tag, k, n, ch, RC[ch].get('controller', '')))
        if not r.get('state'): fails.append(tag + ': empty state')
        if r.get('knockdown') and r.get('fallback', '') not in S[ch]: fails.append(tag + ': knockdown row needs an existing fallback state')
pending = {}
for ch in S:
    for loc, dr, wt in itertools.product(LOCS, DIRS, WTS):
        r = pick(ch, loc, dr, wt)
        if r is None: fails.append('no hit_reactions row for %s %s/%s/%s' % (ch, loc, dr, wt)); continue
        p = r.get('prefer', '')
        if p and p not in S[ch]: pending.setdefault(p, set()).add(ch)
for ch in S:
    if hr.get('defaultState') not in S[ch]: fails.append('defaultState "%s" missing in the %s controller' % (hr.get('defaultState'), ch))
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
    print('%-10s %-6s %-6s %-6s -> state (prefer)' % ('char', 'loc', 'dir', 'wt'))
    for ch in S:
        for loc, dr, wt in itertools.product(LOCS, DIRS, WTS):
            r = pick(ch, loc, dr, wt)
            if r: print('%-10s %-6s %-6s %-6s -> %s%s%s' % (ch, loc, dr, wt, r['state'], (' (' + r['prefer'] + ')') if r.get('prefer') else '', ' KNOCKDOWN(off)' if r.get('knockdown') else ''))

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
for n in IDS:
    if n not in names and RC[n].get('objectName', n) not in names: fails.append('blood.victims missing roster character ' + n)
for v in b['victims']:
    ch = next((c for c in CHARS if v['match'] in (c['id'], c.get('objectName', c['id']))), None)
    if ch is None: warns.append('blood.victims row "%s" matches no roster character' % v['match']); continue
    if not ch.get('armoured') and (v.get('sparks') or v.get('flash')): fails.append('blood: %s (flesh, roster armoured=false) must not spark / flash (Dom rule)' % ch['id'])
g = b['ground']
if max(g['color'][:3]) > BL['groundColorMax']: fails.append('blood.ground.color too bright (%s > %s): blood stays dark and non-glowing' % (g['color'], BL['groundColorMax']))
if g['alpha'] > BL['groundAlphaMax'] or g['max'] > BL['groundCountMax']: warns.append('blood.ground alpha/max high')

# ---------------- cues.json ----------------
cs = open(os.path.join(A, 'Scripts', 'Combat', 'CueBus.cs'), encoding='utf-8-sig').read()
manifest = set(re.findall(r'\{\s*"([a-z_]+)",\s*new\[\]', cs))
cu = jl(os.path.join(D_NEW or D, 'cues.json'))
WEAPONS = tuple(sorted({c['weapon'] for c in CHARS if c.get('weapon')} | {w['weapon'] for w in cu['weapons']}))
for i, r in enumerate(cu['rows']):
    tag = 'cues row %d (%s/%s/%s)' % (i, r['evt'], r['weapon'], r['surface'])
    if r['evt'] not in EVTS: fails.append(tag + ': unknown event')
    if r.get('layer', 'impact') not in LAYERS: fails.append(tag + ': bad layer')
    if not r['play'] and not r.get('disabled'): fails.append(tag + ': empty play')
    for p in r['play']:
        if p['cue'] not in manifest: fails.append('%s: cue "%s" not in CueBus.Manifest' % (tag, p['cue']))
        if p.get('gain', 1) < 0 or p.get('gain', 1) > RULES['cueGainMax']: fails.append(tag + ': gain out of range')
        if p.get('who') not in ('', None, 'attacker', 'defender'): fails.append(tag + ': bad who')
    for m in r.get('moves', []):
        if m not in moves: warns.append('%s: move "%s" not in move-defs.json' % (tag, m))
    for k, vals in (('weapon', WEAPONS), ('surface', SURFACES), ('weight', WTS)):
        if r[k] not in ('*', '') + vals: fails.append('%s: bad %s %s' % (tag, k, r[k]))
for e in EVTS:
    if not any(r['evt'] == e and not r.get('disabled') for r in cu['rows']): warns.append('cues: no enabled row for event "%s" (CueBus keeps its built-in mapping)' % e)
weps = {w['character']: w['weapon'] for w in cu['weapons']}; surf = {w['character']: w['surface'] for w in cu['surfaces']}
if set(weps) != set(IDS) or set(surf) != set(IDS): fails.append('cues weapons[] / surfaces[] must name exactly the roster characters %s (got weapons %s, surfaces %s)' % (sorted(IDS), sorted(weps), sorted(surf)))
for c in CHARS:
    if c['id'] in weps and c.get('weapon') and weps[c['id']] != c['weapon']: fails.append('cues weapons[]: %s is "%s", roster weapon is "%s"' % (c['id'], weps[c['id']], c['weapon']))
    if c['id'] in surf and surf[c['id']] != surface_of(c): fails.append('cues surfaces[]: %s is "%s", roster armoured=%s means "%s"' % (c['id'], surf[c['id']], c.get('armoured', False), surface_of(c)))
off = [r for r in cu['rows'] if r.get('disabled')]
if off: warns.append('cues: %d disabled stopgap row(s) (pain / light-swing effort): enable after a listen test' % len(off))

print('Roster: %s  states: %s  hit rows: %d  cue rows: %d  Manifest cues: %d' % (', '.join(IDS), ' '.join('%s=%d' % (c, len(S[c])) for c in IDS), len(rows), len(cu['rows']), len(manifest)))
for w in warns: print('WARN ' + w)
for f in fails: print('FAIL ' + f)
print('FAILS: %d  WARNS: %d' % (len(fails), len(warns)))
sys.exit(1 if fails else 0)

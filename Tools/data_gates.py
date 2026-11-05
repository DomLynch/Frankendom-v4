#!/usr/bin/env python3
"""data_gates.py - the six data gates for the Frankendom Unity project (HCKIT2 v93). Run before EVERY build; exit code 1 = at least one FAIL.

  python data_gates.py                       gates A-F on the live project (read-only)
  python data_gates.py --gates AC            only some gates
  python data_gates.py --overlay <lane>\\Assets   evaluate "live + this lane's Data / StreamingAssets files" (what the project will look like after the merge)
  python data_gates.py --fix                 gate A only: copy Assets\\Data\\*.json over Assets\\StreamingAssets (what BuildPlayer does at build time)
  python data_gates.py --strict              WARN counts as failure too
  python data_gates.py --json out.json       machine-readable result

A  Data == StreamingAssets   every *.json byte-equal, neither side has an orphan
B  key hygiene               every character key in a character-keyed json is a roster id / @player / @enemy / * / "" (or a documented legacy substring: WARN),
                             never a display name ("PITBORN", "Joan of Arc"), a typo, or a character name inside a JSON KEY (draculaName)
C  coverage                  every roster character has its own row or a fallback row in each character-keyed json (attack_clips per move, ai_adapt per difficulty ...)
D  baked asset vs json       Moves.asset vs move-defs.json, Rules.asset vs rules-defs.json, Difficulty.asset / GameConfig.asset vs difficulty.json
E  controller states         every Animator state named by attack_clips / dodge / finishers exists in the roster controller of the character that plays it
F  duplicate facts           the same fact stated twice must agree (roster weapon / armoured vs cues / blood / hit_flash, heightM vs pairScale, difficulty default ...)

The roster (Assets\\Data\\roster.json) is read, never hard-coded: a new enemy = one roster row, then this script tells you which data rows it still lacks.
"""
import argparse, hashlib, json, math, os, re, shutil, sys, tempfile
from pathlib import Path

PROJECT = Path(os.environ.get('HCKIT2_LIVE', r'C:\Users\dom12\Documents\unity_test_hdrp'))
ROLE_P, ROLE_E, STAR = '@player', '@enemy', '*'

class Res:
    def __init__(s): s.rows = []
    def add(s, gate, level, item, msg=''): s.rows.append((gate, level, item, msg))
    def count(s, gate=None, level=None): return sum(1 for g, l, i, m in s.rows if (gate is None or g == gate) and (level is None or l == level))

def jload(p):
    try: return json.loads(Path(p).read_bytes().decode('utf-8-sig'))
    except Exception as e: return None

def get_path(o, path):
    for k in path.split('.'):
        if o is None: return None
        if isinstance(o, list): return None
        o = o.get(k)
    return o

# -------------------------------------------------------------------------------- setup (data roots, optional overlay)
def setup(args):
    data, sa = PROJECT / 'Assets' / 'Data', PROJECT / 'Assets' / 'StreamingAssets'
    tmp = None
    if args.data: data = Path(args.data)
    if args.sa: sa = Path(args.sa)
    if args.overlay:
        tmp = Path(tempfile.mkdtemp(prefix='data_gates_'))
        for name, src in (('Data', data), ('StreamingAssets', sa)):
            d = tmp / name; d.mkdir()
            for f in Path(src).glob('*.json'): shutil.copyfile(f, d / f.name)
            ov = Path(args.overlay) / name
            if ov.exists():
                for f in ov.glob('*.json'): shutil.copyfile(f, d / f.name)
        data, sa = tmp / 'Data', tmp / 'StreamingAssets'
    return data, sa, tmp

class Roster:
    def __init__(s, data):
        r = jload(data / 'roster.json') or {}
        s.player, s.enemy = r.get('player', ''), r.get('enemy', '')
        s.chars = {c['id']: c for c in r.get('characters', [])}
        s.ids = list(s.chars)
    def resolve(s, key): return s.player if key == ROLE_P else s.enemy if key == ROLE_E else key
    def role_of(s, cid): return ROLE_P if cid == s.player else ROLE_E if cid == s.enemy else ''
    def display(s, cid): return s.chars[cid].get('displayName', cid)

# -------------------------------------------------------------------------------- gate A
def gate_A(R, data, sa, args):
    dj = {f.name: f for f in data.glob('*.json')}; sj = {f.name: f for f in sa.glob('*.json')}
    md = lambda p: hashlib.md5(Path(p).read_bytes()).hexdigest()
    bad = 0
    for n in sorted(set(dj) | set(sj)):
        if n not in sj: R.add('A', 'FAIL', n, 'only in Data (StreamingAssets copy missing)'); bad += 1
        elif n not in dj: R.add('A', 'FAIL', n, 'only in StreamingAssets (orphan, no Data source)'); bad += 1
        elif md(dj[n]) != md(sj[n]): R.add('A', 'FAIL', n, 'Data != StreamingAssets (md5 differs)'); bad += 1
    if args.fix and not args.overlay:
        for n in sorted(dj):
            if n not in sj or md(dj[n]) != md(sj[n]): shutil.copyfile(dj[n], sa / n); R.add('A', 'FIXED', n, 'copied Data -> StreamingAssets')
    R.add('A', 'PASS' if bad == 0 else 'INFO', 'summary', '%d json in Data, %d in StreamingAssets, %d differences' % (len(dj), len(sj), bad))

# -------------------------------------------------------------------------------- tables: (file, array path, key field, allowed extras, label)
# kind: 'char' = key names a roster character, 'obj' = scene object name (roster objectName or id), 'label' = free HUD text that should be "" so the roster supplies it
KEYED = [
    ('ai_adapt.json', 'profiles', 'fighter', 'char'),
    ('ai_rows.json', 'unity.zBias', 'name', 'obj'),
    ('arm_hold.json', 'characters', 'name', 'char'),
    ('attack_clips.json', 'rows', 'character', 'char'),
    ('attack_clips.json', 'bake', 'character', 'char'),
    ('dodge_clips.json', 'bake', 'character', 'char'),
    ('blood.json', 'victims', 'match', 'char'),
    ('clash_fx.json', 'rows', 'targetContains', 'char'),
    ('cues.json', 'weapons', 'character', 'char'),
    ('cues.json', 'surfaces', 'character', 'char'),
    ('cues.json', 'voicePitch', 'character', 'char'),
    ('fight_balance.json', 'fighters', 'key', 'char'),
    ('finishers.json', 'rt.killers', 'id', 'char'),
    ('finishers.json', 'rt.ground.soles', 'fighter', 'char'),
    ('finishers.json', 'clipImport.characters', 'name', 'char'),
    ('foot_ground.json', 'characters', 'name', 'char'),
    ('foot_yaw.json', 'characters', 'name', 'char'),
    ('grip_pose.json', 'characters', 'name', 'char'),
    ('head_clamp.json', 'characters', 'name', 'char'),
    ('weapon_grip.json', 'characters', 'name', 'char'),
    ('guard_turn.json', 'characters', 'id', 'char'),
    ('hit_flash.json', 'rows', 'match', 'char'),
    ('poise.json', 'armour.fighters', 'key', 'char'),
    ('poise.json', 'meter.fighters', 'key', 'char'),
    ('posture.json', 'fighters', 'key', 'char'),
    ('stamina.json', 'fighters', 'key', 'char'),
    ('ragdoll.json', 'massKg', 'contains', 'char'),
    ('wounds.json', 'characters', 'id', 'char'),
    ('move_sheet.json', 'moves', 'subject', 'obj'),
    ('move_sheet_flip.json', 'moves', 'subject', 'obj'),
    ('move_sheet_loco.json', 'moves', 'subject', 'obj'),
    ('opponents.json', 'rows', 'name', 'label'),
]
KEYED_LISTS = [('hdrp_char_layer.json', 'characters'), ('hdrp_char_layer.json', 'restore')]   # plain string arrays of scene object names

def rows_at(obj, path):
    o = get_path(obj, path)
    return o if isinstance(o, list) else None

def gate_B(R, data, sa, args, ros):
    objnames = {c.get('objectName', cid) for cid, c in ros.chars.items()}
    disp = {c.get('displayName', cid) for cid, c in ros.chars.items()}
    ids = set(ros.ids); ok_tokens = {'', STAR, ROLE_P, ROLE_E}
    nbad = nwarn = ntot = 0
    def check(f, where, v, kind):
        nonlocal nbad, nwarn, ntot
        ntot += 1
        if not isinstance(v, str): return
        if kind == 'label':
            if v != '': R.add('B', 'WARN', f + ':' + where, 'label "%s" is a hard-coded display text; "" lets the roster supply the name' % v); nwarn += 1
            return
        if v in ok_tokens or v in ids: return
        if kind == 'obj' and v in objnames: return
        if v in disp and v not in ids: R.add('B', 'FAIL', f + ':' + where, '"%s" is a DISPLAY name, the key must be the roster id' % v); nbad += 1; return
        low = {i.lower(): i for i in ids}
        if v.lower() in low: R.add('B', 'WARN', f + ':' + where, '"%s" differs from roster id "%s" only by case' % (v, low[v.lower()])); nwarn += 1; return
        if any(v in i or v in o for i in ids for o in objnames) or any(i in v or o in v for i in ids for o in objnames):
            R.add('B', 'WARN', f + ':' + where, '"%s" is a LEGACY substring key (works through the compat fallback; use the roster id)' % v); nwarn += 1; return
        R.add('B', 'FAIL', f + ':' + where, '"%s" is not a roster id, role token or "*" (typo or removed character?)' % v); nbad += 1
    for f, path, field, kind in KEYED:
        d = jload(data / f)
        if d is None: continue
        rows = rows_at(d, path)
        if rows is None: continue
        for i, r in enumerate(rows):
            if isinstance(r, dict) and field in r: check(f, '%s[%d].%s' % (path, i, field), r[field], kind)
    for f, path in KEYED_LISTS:
        d = jload(data / f)
        if d is None: continue
        for i, v in enumerate(d.get(path, []) or []):
            if v in ok_tokens or v in objnames or v in ids or v == '@all': continue
            if any(v in o for o in objnames): R.add('B', 'WARN', f + ':%s[%d]' % (path, i), '"%s" legacy substring' % v); nwarn += 1
            # non-roster scene objects (MetaHumanHead ...) are allowed
    r = jload(data / 'roster.json') or {}
    for fld in ('player', 'enemy'):
        if r.get(fld) not in ids: R.add('B', 'FAIL', 'roster.json:' + fld, '"%s" is not a roster id' % r.get(fld)); nbad += 1
    # character names inside JSON KEYS (draculaName / pitbornName ...)
    low = [i.lower() for i in ids]
    for fp in sorted(data.glob('*.json')):
        d = jload(fp)
        if d is None: continue
        def walk(o, p):
            nonlocal nbad
            if isinstance(o, dict):
                for k, v in o.items():
                    kl = str(k).lower()
                    words = [w.lower() for w in re.findall(r'[A-Z]?[a-z0-9]+|[A-Z]+(?![a-z])', str(k))]   # whole camelCase words only: draculaName yes, "latest" / "biteStagger" no
                    if k not in ids and any(l in words for l in low) and not str(k).startswith('_') and len(k) < 40 and fp.name not in ('roster.json',):
                        R.add('B', 'FAIL', fp.name + ':' + p + '.' + k, 'a character name inside a JSON KEY: rename to a role (playerName / enemyName) or key rows by id'); nbad += 1
                    walk(v, p + '.' + k)
            elif isinstance(o, list):
                for i, v in enumerate(o): walk(v, p + '[%d]' % i)
        walk(d, '')
    R.add('B', 'PASS' if nbad == 0 else 'INFO', 'summary', '%d keys checked, %d FAIL, %d WARN' % (ntot, nbad, nwarn))

# -------------------------------------------------------------------------------- gate C
# (file, array path, key field, fallback kinds accepted, strictness, note)
#   fallback kinds: 'star' = a "*" row, 'any' = a "" row, 'defaults' = a top-level `defaults` object, 'code' = the loader has a documented code default
COVER = [
    ('attack_clips.json', 'rows', 'character', ('star', 'any'), 'FAIL', 'per move: see the move-level check below'),
    ('fight_balance.json', 'fighters', 'key', ('star',), 'FAIL', 'no row = multipliers 1 (neutral) but a "*" row makes it explicit'),
    ('cues.json', 'weapons', 'character', ('star',), 'FAIL', 'no weapon = 12 of 36 cue rows silently skipped'),
    ('cues.json', 'surfaces', 'character', ('star',), 'FAIL', 'no surface = hit sounds skipped'),
    ('cues.json', 'voicePitch', 'character', ('star', 'code'), 'WARN', 'default rate 1.0'),
    ('hit_flash.json', 'rows', 'match', ('star',), 'WARN', 'no row = no hit flash'),
    ('arm_hold.json', 'characters', 'name', ('star',), 'WARN', 'no row = feature off'),
    ('weapon_grip.json', 'characters', 'name', ('star',), 'WARN', 'no row = no socket re-pose'),
    ('foot_ground.json', 'characters', 'name', ('star',), 'WARN', 'no row = no foot IK'),
    ('foot_yaw.json', 'characters', 'name', ('defaults',), 'WARN', 'defaults row'),
    ('grip_pose.json', 'characters', 'name', ('defaults',), 'WARN', 'defaults row'),
    ('head_clamp.json', 'characters', 'name', ('defaults',), 'WARN', 'defaults row'),
    ('finishers.json', 'rt.killers', 'id', ('star', 'code'), 'WARN', 'pairScale 1.0 by code'),
    ('finishers.json', 'rt.ground.soles', 'fighter', ('code',), 'WARN', 'SoleRow code defaults'),
    ('finishers.json', 'clipImport.characters', 'name', (), 'WARN', 'hipsRestZ is MEASURED per rig: a new character needs a row (FinisherClipSetup)'),
    ('blood.json', 'victims', 'match', ('code',), 'WARN', 'default blood; sparks / flash from roster.armoured'),
    ('ragdoll.json', 'massKg', 'contains', ('code',), 'WARN', 'defaultMassKg'),
    ('poise.json', 'armour.fighters', 'key', ('code',), 'WARN', 'no row = no hyper-armour'),
    ('poise.json', 'meter.fighters', 'key', ('code',), 'WARN', 'meter defaults'),
    ('posture.json', 'fighters', 'key', ('code',), 'WARN', 'defaultMax'),
    ('stamina.json', 'fighters', 'key', ('code',), 'WARN', 'defaultMax'),
    ('guard_turn.json', 'characters', 'id', ('code',), 'WARN', 'RowFor null = no guard turn'),
    ('wounds.json', 'characters', 'id', ('star',), 'FAIL', 'wounds has the "*" row pattern'),
    ('ai_rows.json', 'unity.zBias', 'name', ('code',), 'WARN', 'no row = no stair-side bias (role rows @player / @enemy cover player and enemy)'),
]

def own_row(rows, field, ros, cid):
    for r in rows:
        if not isinstance(r, dict): continue
        k = r.get(field)
        if isinstance(k, str) and k and k != STAR and ros.resolve(k) == cid: return r
    return None

def gate_C(R, data, sa, args, ros):
    for f, path, field, fb, sev, note in COVER:
        d = jload(data / f)
        if d is None: R.add('C', 'WARN', f, 'file missing'); continue
        rows = rows_at(d, path) or []
        star = next((r for r in rows if isinstance(r, dict) and r.get(field) == STAR), None)
        anyrow = next((r for r in rows if isinstance(r, dict) and r.get(field) == ''), None)
        defaults = get_path(d, 'defaults') if 'defaults' in fb else None
        nown = 0
        for cid in ros.ids:
            if own_row(rows, field, ros, cid) is not None: nown += 1; continue
            item = '%s:%s [%s]' % (f, path, cid)
            fbrow = star if ('star' in fb and star) else anyrow if ('any' in fb and anyrow) else defaults if ('defaults' in fb and defaults is not None) else None
            if fbrow is not None:
                off = isinstance(fbrow, dict) and fbrow.get('enabled') is False
                R.add('C', 'WARN' if off else 'PASS', item, 'uses the %s row%s' % ('"*"' if fbrow is star else '"" ' if fbrow is anyrow else 'defaults', ' and that default is OFF (feature disabled for this character)' if off else ''))
            elif 'code' in fb: R.add('C', 'INFO', item, 'no row; code default (%s)' % note)
            else: R.add('C', sev, item, 'NO ROW and no fallback: %s' % note)
        if nown == len(ros.ids): R.add('C', 'PASS', '%s:%s' % (f, path), 'every roster character has its own row')
    # attack_clips per move
    d = jload(data / 'attack_clips.json') or {}; rows = [r for r in d.get('rows', []) if r.get('enabled', True)]
    moves = sorted({r['move'] for r in rows})
    for cid in ros.ids:
        lacking = [m for m in moves if not any(r['move'] == m and (r['character'] == cid or r['character'] in ('', STAR)) for r in rows)]
        if lacking: R.add('C', 'WARN', 'attack_clips.json moves [%s]' % cid, '%d move(s) fall back to the OLD whole-clip squash: %s' % (len(lacking), ', '.join(lacking)))
        else: R.add('C', 'PASS', 'attack_clips.json moves [%s]' % cid, 'all %d moves have an own or default row' % len(moves))
    # ai_adapt per difficulty / bot
    ad = jload(data / 'ai_adapt.json') or {}; dif = jload(data / 'difficulty.json') or {}
    levels = [r['id'] for r in dif.get('rows', []) if isinstance(r, dict) and isinstance(r.get('id'), int)] if isinstance(dif.get('rows'), list) else []
    levels = levels or list(range(1, 11))
    def fighter_ok(fk, cid): return fk == '' or fk == STAR or ros.resolve(fk) == cid
    profiles = ad.get('profiles', [])
    for cid in ros.ids:
        miss = [l for l in levels if not any(not p.get('bot') and fighter_ok(p.get('fighter', ''), cid) and p.get('minDiff', 0) <= l <= p.get('maxDiff', 99) for p in profiles)]
        botok = any(p.get('bot') and fighter_ok(p.get('fighter', ''), cid) for p in profiles)
        role = ros.role_of(cid)
        if role == ROLE_E or cid == ros.enemy:
            R.add('C', 'FAIL' if miss else 'PASS', 'ai_adapt.json difficulty [%s]' % cid, ('enemy has NO adaptation profile for difficulty level(s) %s' % miss) if miss else 'all %d difficulty levels covered' % len(levels))
        if role == ROLE_P or cid == ros.player:
            R.add('C', 'WARN' if not botok else 'PASS', 'ai_adapt.json bot [%s]' % cid, 'no bot profile for the player bot (playerBot runs without adaptation)' if not botok else 'player bot covered')
        if role == '':
            pass
        if role == '' and miss: R.add('C', 'INFO', 'ai_adapt.json difficulty [%s]' % cid, 'not enemy/player now; would have NO adaptation if made the enemy (levels %s)' % miss)
    # zBias roles
    d = jload(data / 'ai_rows.json') or {}; zb = get_path(d, 'unity.zBias') or []
    for role, cid in ((ROLE_P, ros.player), (ROLE_E, ros.enemy)):
        if not any(z.get('name') in (role, cid) or (z.get('name') or '') == ros.chars.get(cid, {}).get('objectName') for z in zb):
            R.add('C', 'WARN', 'ai_rows.json zBias [%s %s]' % (role, cid), 'no stair-side bias row for this role')
    R.add('C', 'INFO', 'summary', '%d PASS, %d WARN, %d FAIL, %d INFO' % (R.count('C', 'PASS'), R.count('C', 'WARN'), R.count('C', 'FAIL'), R.count('C', 'INFO')))

# -------------------------------------------------------------------------------- gate D (baked assets vs json)
def parse_asset(p):
    import yaml
    t = Path(p).read_bytes().decode('utf-8-sig')
    i = t.find('MonoBehaviour:')
    return yaml.safe_load(t[i:]).get('MonoBehaviour', {}) if i >= 0 else {}

def feq(a, b, tol=1e-4):
    try: return abs(float(a) - float(b)) <= tol * max(1.0, abs(float(a)), abs(float(b)))
    except Exception: return a == b

def gate_D(R, data, sa, args, ros):
    try: import yaml  # noqa
    except Exception: R.add('D', 'WARN', 'pyyaml', 'PyYAML missing: gate D skipped (pip install pyyaml)'); return
    ad = PROJECT / 'Assets' / 'Data'
    # Moves
    mf = ad / 'Moves.asset'
    if mf.exists():
        a = parse_asset(mf); am = {m['id']: m for m in a.get('moves', [])}
        jd = jload(data / 'move-defs.json') or {}; jm = {m['id']: m for m in jd.get('moves', [])}
        diffs = 0
        for mid, m in am.items():
            j = jm.get(mid)
            if j is None: R.add('D', 'WARN', 'Moves.asset/' + mid, 'in the asset but not in move-defs.json'); continue
            exp = {}
            for k, v in j.items():
                if k in ('chainPath', 'feintUntil', 'path', 'direction', 'id', 'chained', 'chain', 'vsGuard'): continue
                exp[k] = v
            ch = j.get('chained'); cn = j.get('chain'); vs = j.get('vsGuard')
            exp['hasChained'] = 1 if ch else 0
            if ch: exp.update(chainedWindup=ch['windup'], chainedActive=ch['active'], chainedRecovery=ch['recovery'])
            if cn: exp['chainWindow'] = cn['window']
            exp['hasVsGuard'] = 1 if vs else 0
            if vs: exp.update({'vsGuardStagger': vs.get('stagger', 0), 'vsGuardStaminaDamage': vs.get('staminaDamage', 0)})
            bad = []
            for k, v in exp.items():
                if k not in m: continue
                vv = 0 if v is None else int(v) if isinstance(v, bool) else v
                if not feq(m[k], vv): bad.append('%s asset=%s json=%s' % (k, m[k], vv))
            if cn and sorted(cn.get('follow', [])) != sorted(m.get('chainFollow') or []): bad.append('chainFollow differs')
            if bad: diffs += 1; R.add('D', 'FAIL', 'Moves.asset/' + mid, 'value drift: ' + '; '.join(bad[:6]))
        only_json = sorted(set(jm) - set(am))
        if only_json: R.add('D', 'WARN', 'move-defs.json', '%d move row(s) exist only in json and reach the game through MoveDefsOverlay (editing them is fine; the asset is authoritative for the %d shared rows): %s' % (len(only_json), len(am), ', '.join(only_json)))
        R.add('D', 'PASS' if diffs == 0 else 'INFO', 'Moves.asset vs move-defs.json', '%d shared rows compared, %d with drift' % (len(set(am) & set(jm)), diffs))
    # Rules
    rf = ad / 'Rules.asset'
    if rf.exists():
        a = parse_asset(rf); keys, vals = a.get('keys', []), a.get('values', [])
        jr = {r['id']: r for r in (jload(data / 'rules-defs.json') or {}).get('rules', [])}
        drift = 0
        for k, v in zip(keys, vals):
            r = jr.get(k)
            if r is None: R.add('D', 'WARN', 'Rules.asset/' + k, 'in the asset but not in rules-defs.json'); continue
            if 'n' in r: e = r['n']
            elif 'piDiv' in r: e = math.pi / r['piDiv']
            else: e = 1 if r.get('b') else 0
            if not feq(v, e, 2e-3): drift += 1; R.add('D', 'FAIL', 'Rules.asset/' + k, 'asset=%s json=%s' % (v, e))
        miss = sorted(set(jr) - set(keys))
        if miss: R.add('D', 'WARN', 'rules-defs.json', '%d rule id(s) only in json (no effect unless RulesOverlay exists): %s' % (len(miss), ', '.join(miss[:8])))
        R.add('D', 'PASS' if drift == 0 else 'INFO', 'Rules.asset vs rules-defs.json', '%d keys compared, %d drift' % (len(keys), drift))
    # Difficulty / GameConfig
    dj = jload(data / 'difficulty.json') or {}
    df = ad / 'Difficulty.asset'
    if df.exists():
        a = parse_asset(df); jrows = dj.get('rows') if isinstance(dj.get('rows'), list) else []
        jd = {r.get('id'): r for r in jrows if isinstance(r, dict)}; drift = 0
        for r in a.get('rows', []):
            j = jd.get(r['id'])
            if j is None: continue
            for k, v in r.items():
                if k in j and not feq(v, j[k]): drift += 1; R.add('D', 'FAIL', 'Difficulty.asset/row%s.%s' % (r['id'], k), 'asset=%s json=%s' % (v, j[k]))
        dl = a.get('defaultLevel')
        if dl is not None and dj.get('default') is not None and dl != dj['default']:
            R.add('D', 'WARN', 'Difficulty.asset defaultLevel', 'asset=%s but difficulty.json default=%s; the asset is NOT read at runtime (dead duplicate, delete after Dom OK = phase 3)' % (dl, dj['default']))
        R.add('D', 'PASS' if drift == 0 else 'INFO', 'Difficulty.asset vs difficulty.json', '%d rows compared, %d drift' % (len(a.get('rows', [])), drift))
    gf = ad / 'GameConfig.asset'
    if gf.exists():
        a = parse_asset(gf)
        if 'difficulty' in a and dj.get('default') is not None and a['difficulty'] != dj['default']:
            R.add('D', 'WARN', 'GameConfig.asset difficulty', 'asset=%s, difficulty.json default=%s (dead duplicate, phase 3)' % (a['difficulty'], dj['default']))
        if 'pitbornHeight' in a: R.add('D', 'WARN', 'GameConfig.asset pitbornHeight', 'stale field (%s) the class no longer has; phase 3' % a['pitbornHeight'])

# -------------------------------------------------------------------------------- gate E (controller states)
def controller_states(path):
    t = Path(path).read_text(encoding='utf-8', errors='replace')
    out = set()
    for blk in re.split(r'(?m)^--- !u!', t):
        if blk.startswith('1102 '):
            m = re.search(r'(?m)^  m_Name: (.*)$', blk)
            if m: out.add(m.group(1).strip())
    return out

def gate_E(R, data, sa, args, ros):
    states = {}
    for cid in ros.ids:
        cp = PROJECT / ros.chars[cid].get('controller', '')
        if not ros.chars[cid].get('controller') or not cp.exists(): R.add('E', 'FAIL', cid, 'controller "%s" missing' % ros.chars[cid].get('controller')); states[cid] = None; continue
        states[cid] = controller_states(cp)
        R.add('E', 'INFO', cid, '%d states in %s' % (len(states[cid]), cp.name))
    def need(clip, who, what, level_other='WARN'):
        if not clip: return
        for cid in who:
            if states.get(cid) is not None and clip not in states[cid]:
                lvl = 'FAIL' if cid in (ros.player,) else level_other
                R.add('E', lvl, '%s [%s]' % (what, cid), 'state "%s" not in %s' % (clip, ros.chars[cid]['controller'].split('/')[-1]))
    ac = jload(data / 'attack_clips.json') or {}
    for r in ac.get('rows', []):
        if not r.get('enabled', True): continue
        who = ros.ids if r['character'] in ('', STAR) else [ros.resolve(r['character'])]
        who = [w for w in who if w in states]
        # exact row: its own controller must have the clip OR the fallback (neither = FAIL, the move cannot play); a "*" row: every roster controller (WARN)
        for cid in who:
            st = states.get(cid)
            if st is None: continue
            c, fb = r.get('clip', ''), r.get('fallbackClip', '')
            hasc, hasf = (not c) or c in st, (not fb) or fb in st
            what = 'attack_clips %s %s [%s]' % (r['character'] or '""', r['move'], cid)
            if (c and not hasc) and not (fb and hasf): R.add('E', 'FAIL' if r['character'] not in ('', STAR) else 'WARN', what, 'neither clip "%s" nor fallbackClip "%s" exists in %s' % (c, fb, ros.chars[cid]['controller'].split('/')[-1]))
            elif c and not hasc: R.add('E', 'WARN', what, 'clip "%s" missing, the fallbackClip "%s" plays instead' % (c, fb))
    dg = jload(data / 'dodge.json') or {}
    for sect, fl in (('roll', ['clip']), ('backstep', ['clip']), ('sidestep', ['clip', 'clipLeft', 'clipRight', 'clipBack'])):
        for fld in fl: need((dg.get(sect) or {}).get(fld, ''), [c for c in ros.ids], 'dodge.%s.%s' % (sect, fld))
    need((dg.get('guard') or {}).get('moveClipFallback', ''), ros.ids, 'dodge.guard.moveClipFallback')
    fi = jload(data / 'finishers.json') or {}
    for e in fi.get('entries', []):
        if not e.get('enabled', True) or e.get('placeholder'): continue
        side = e.get('attackerSide', 'player'); killer = ros.player if side == 'player' else ros.enemy; victim = ros.enemy if side == 'player' else ros.player
        need(e.get('attackerClip', ''), [killer], 'finishers.%s.attackerClip' % e['id'])
        need(e.get('victimClip', ''), [victim], 'finishers.%s.victimClip' % e['id'])
    if R.count('E', 'FAIL') == 0 and R.count('E', 'WARN') == 0: R.add('E', 'PASS', 'summary', 'every state named by the data exists in the controller that plays it')

# -------------------------------------------------------------------------------- gate F (duplicate facts)
def gate_F(R, data, sa, args, ros):
    cues = jload(data / 'cues.json') or {}; blood = jload(data / 'blood.json') or {}; hf = jload(data / 'hit_flash.json') or {}
    def row(rows, field, cid): return next((r for r in rows or [] if isinstance(r, dict) and r.get(field) == cid), None)
    for cid, c in ros.chars.items():
        w = row(cues.get('weapons'), 'character', cid); s = row(cues.get('surfaces'), 'character', cid)
        if w and c.get('weapon') and w['weapon'] != c['weapon']: R.add('F', 'FAIL', 'weapon [%s]' % cid, 'roster.weapon=%s but cues.weapons=%s' % (c['weapon'], w['weapon']))
        exp = 'armour' if c.get('armoured') else 'flesh'
        if s and s['surface'] != exp: R.add('F', 'FAIL', 'surface [%s]' % cid, 'roster.armoured=%s expects "%s" but cues.surfaces=%s' % (c.get('armoured'), exp, s['surface']))
        b = row(blood.get('victims'), 'match', cid)
        if b and (bool(b.get('sparks')) != bool(c.get('armoured')) or bool(b.get('flash')) != bool(c.get('armoured'))): R.add('F', 'FAIL', 'blood [%s]' % cid, 'roster.armoured=%s but blood victim sparks=%s flash=%s' % (c.get('armoured'), b.get('sparks'), b.get('flash')))
        h = row(hf.get('rows'), 'match', cid)
        if h and bool(h.get('enabled')) != bool(c.get('armoured')): R.add('F', 'WARN', 'hit_flash [%s]' % cid, 'roster.armoured=%s but hit_flash.enabled=%s' % (c.get('armoured'), h.get('enabled')))
    fi = jload(data / 'finishers.json') or {}
    ref = ros.chars.get('Dracula') or next(iter(ros.chars.values()), {})
    for k in (fi.get('rt', {}) or {}).get('killers', []):
        kid = k.get('id')
        if kid in ros.chars and ros.chars[kid].get('heightM') and ref.get('heightM') and ros.chars[kid]['id'] != ref['id']:
            exp = ros.chars[kid]['heightM'] / ref['heightM']
            if not feq(exp, k.get('pairScale', 1), 2e-2): R.add('F', 'WARN', 'pairScale [%s]' % kid, 'heightM ratio %.4f but rt.killers pairScale %.4f (same fact twice; compute one from the other)' % (exp, k['pairScale']))
        elif kid in ros.chars and not ros.chars[kid].get('heightM') and k.get('pairScale', 1) != 1:
            R.add('F', 'INFO', 'pairScale [%s]' % kid, 'pairScale %.4f is a hand-measured number; roster.heightM is 0 so nothing cross-checks it' % k['pairScale'])
    op = jload(data / 'opponents.json') or {}
    en = ros.display(ros.enemy) if ros.enemy in ros.chars else ros.enemy
    for i, r in enumerate(op.get('rows', []) or []):
        n = r.get('name', '')
        if n and n.lower() != en.lower(): R.add('F', 'WARN', 'opponents.rows[%d].name' % i, '"%s" differs from the roster enemy "%s" (stale copy of a fact the roster owns)' % (n, en))
    dj = jload(data / 'difficulty.json') or {}
    df = PROJECT / 'Assets' / 'Data' / 'Difficulty.asset'
    if df.exists():
        m = re.search(r'defaultLevel:\s*(\d+)', df.read_text(encoding='utf-8', errors='replace'))
        if m and dj.get('default') is not None and int(m.group(1)) != dj['default']: R.add('F', 'WARN', 'difficulty default', 'difficulty.json=%s, Difficulty.asset=%s (asset is dead)' % (dj['default'], m.group(1)))
    mh = jload(data / 'mhhead_attach.json') or {}
    if isinstance(mh.get('fighterName'), str) and mh['fighterName'] not in ('', ros.player):
        R.add('F', 'WARN', 'mhhead_attach.fighterName', '"%s" is the MetaHuman head owner but the roster player is "%s" (head sits on a non-player)' % (mh['fighterName'], ros.player))
    if R.count('F', 'FAIL') == 0 and R.count('F', 'WARN') == 0: R.add('F', 'PASS', 'summary', 'no duplicate fact disagrees')


# -------------------------------------------------------------------------------- gate G (only with --overlay): no character gains or loses a row
def gate_G(R, data, sa, args, ros):
    before = PROJECT / 'Assets' / 'Data'
    if not args.overlay: R.add('G', 'INFO', 'summary', 'needs --overlay (compares live rows with live+overlay rows)'); return
    bros = Roster(before); nchg = 0
    def own(rows, field, r_, cid):
        out = []
        for r in rows or []:
            if not isinstance(r, dict): continue
            k = r.get(field)
            if isinstance(k, str) and k and k != STAR and r_.resolve(k) == cid:
                c = dict(r); c[field] = '<key>'; out.append(json.dumps(c, sort_keys=True))
        return sorted(out)
    for f, path, field, kind in KEYED:
        b, a = jload(before / f), jload(data / f)
        if b is None or a is None: continue
        for cid in ros.ids:
            ob, oa = own(rows_at(b, path), field, bros, cid), own(rows_at(a, path), field, ros, cid)
            if ob != oa:
                nchg += 1
                what = 'GAINS %d row(s)' % (len(oa) - len(ob)) if len(oa) > len(ob) else 'LOSES %d row(s)' % (len(ob) - len(oa)) if len(oa) < len(ob) else 'own row CONTENT changed'
                R.add('G', 'FAIL', '%s:%s [%s]' % (f, path, cid), '%s through the merge (a role token or key rename must never change which rows a character has)' % what)
    R.add('G', 'PASS' if nchg == 0 else 'INFO', 'summary', 'own-row sets per roster id compared across %d keyed arrays: %d changed' % (len(KEYED), nchg))

# -------------------------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser(); ap.add_argument('--gates', default='ABCDEFG'); ap.add_argument('--data'); ap.add_argument('--sa'); ap.add_argument('--overlay')
    ap.add_argument('--fix', action='store_true'); ap.add_argument('--strict', action='store_true'); ap.add_argument('--json'); ap.add_argument('--quiet', action='store_true', help='hide PASS and INFO lines')
    args = ap.parse_args(); R = Res()
    data, sa, tmp = setup(args); ros = Roster(data)
    names = {'A': 'Data == StreamingAssets', 'B': 'key hygiene', 'C': 'coverage per roster character', 'D': 'baked asset vs json', 'E': 'controller states', 'F': 'duplicate facts', 'G': 'row coverage unchanged by the merge'}
    if not ros.ids: print('FAIL: roster.json unreadable'); return 1
    for g in args.gates.upper():
        if g == 'G' and not args.overlay: continue
        try:
            {'A': lambda: gate_A(R, data, sa, args), 'B': lambda: gate_B(R, data, sa, args, ros), 'C': lambda: gate_C(R, data, sa, args, ros),
             'D': lambda: gate_D(R, data, sa, args, ros), 'E': lambda: gate_E(R, data, sa, args, ros), 'F': lambda: gate_F(R, data, sa, args, ros), 'G': lambda: gate_G(R, data, sa, args, ros)}[g]()
        except Exception as e:
            R.add(g, 'FAIL', 'gate crashed', repr(e))
    print('data_gates: project=%s  roster=%s  player=%s  enemy=%s%s' % (PROJECT, ','.join(ros.ids), ros.player, ros.enemy, '  OVERLAY=' + args.overlay if args.overlay else ''))
    for g in args.gates.upper():
        print('\n== GATE %s: %s' % (g, names[g]))
        for gg, lvl, item, msg in R.rows:
            if gg != g or (args.quiet and lvl in ('PASS', 'INFO')): continue
            print('  [%-5s] %s%s' % (lvl, item, ('  -  ' + msg) if msg else ''))
        print('  => %d FAIL, %d WARN' % (R.count(g, 'FAIL'), R.count(g, 'WARN')))
    nf, nw = R.count(level='FAIL'), R.count(level='WARN')
    print('\nRESULT: %s  (%d FAIL, %d WARN)' % ('FAIL' if nf or (args.strict and nw) else 'PASS', nf, nw))
    if args.json: Path(args.json).write_text(json.dumps([dict(gate=g, level=l, item=i, msg=m) for g, l, i, m in R.rows], indent=1), encoding='utf-8')
    if tmp: shutil.rmtree(tmp, ignore_errors=True)
    return 1 if nf or (args.strict and nw) else 0

if __name__ == '__main__':
    sys.exit(main())

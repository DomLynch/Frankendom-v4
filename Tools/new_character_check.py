#!/usr/bin/env python3
"""new_character_check.py - dry-run "new character" checklist (HCKIT5 v95). Read-only: writes nothing under the project.

  python new_character_check.py Monster01                  roster id not in roster.json yet -> a FAKE row is built (clone of --like, default = the roster enemy, names/paths renamed) and checked
  python new_character_check.py Pitborn                    an id that IS in roster.json is checked as it is
  options: --as enemy|player|none   role the id would get (fake rows default: enemy)
           --like <id>              roster row cloned for a fake id (default: the roster enemy)
           --row file.json          use this json object as the roster row instead of the clone
           --data <dir> --sa <dir>  evaluate other Data / StreamingAssets folders (default: the live ones; use laneHCKIT5\\scratch\\Assets\\Data to see the state AFTER the merge)
           --strict                 exit code 1 when anything is still MISSING (default exit 0: it is a to-do list)

It answers: which roster fields, which json rows, which assets and which Animator states does this character still need, and which Editor steps build the rest.
The row / fallback logic is laneHCKIT2's data_gates.py (KEYED / COVER tables, own_row, gates B C E F run on a temp copy that contains the (fake) row), not a second copy of it.
"""
import argparse, copy, json, os, re, shutil, sys, tempfile
from pathlib import Path

PROJECT = Path(os.environ.get('HCKIT2_LIVE', r'C:\Users\dom12\Documents\unity_test_hdrp'))
HERE = Path(__file__).resolve().parent
for cand in (PROJECT / 'Tools', PROJECT / '_agents' / 'laneHCKIT2' / 'tools', HERE):
    if (cand / 'data_gates.py').exists(): sys.path.insert(0, str(cand)); break
import data_gates as G   # noqa: E402

STAR = '*'
ROW_FIELDS = [('id', True), ('displayName', True), ('objectName', True), ('weapon', True), ('controller', True), ('modelGlb', True), ('animGlb', True), ('guardMask', True),
              ('finisherClipFolder', True), ('heightM', True), ('armoured', True), ('scaleFloor', False), ('headRenderer', False), ('clipSet', False), ('templateFor', False), ('lightLayerGroup', False)]


def paths_resolve(s):
    try:
        sys.path.insert(0, str(PROJECT / 'Tools')); import paths as P; return P.resolve(s)
    except Exception:
        return s.replace('${kitsOut}', 'C:/Users/dom12/Documents/kits_out').replace('${projectRoot}', str(PROJECT).replace('\\', '/'))


def exists(rel):
    return (PROJECT / rel).exists()


def fake_row(cid, like):
    r = copy.deepcopy(like)
    r.update(id=cid, displayName=cid, objectName=cid, controller='Assets/Characters/%s.controller' % cid, modelGlb='Assets/Characters/%s/%s_combat.glb' % (cid, cid),
             animGlb='Assets/Characters/%s/%s_anim.glb' % (cid, cid), guardMask='Assets/Characters/GuardUpper_%s.mask' % cid, finisherClipFolder='Assets/Characters/%s/FinisherClips' % cid)
    r['heightM'] = 0 if 'heightM' not in like else like['heightM']
    r['clipSet'] = 'mixamo_sword'
    r.pop('loadout', None); r.pop('headRenderer', None); r.pop('templateFor', None)
    r['notes'] = 'FAKE row built by new_character_check.py from "%s" (dry run, not saved)' % like.get('id')
    return r


def states_needed(data, ros, cid, role):
    """(state, why) pairs the data asks this character for; same sources as gate E plus the clipSet."""
    need = {}
    ac = G.jload(data / 'attack_clips.json') or {}
    for r in ac.get('rows', []):
        if not r.get('enabled', True): continue
        who = ros.ids if r['character'] in ('', STAR) else [ros.resolve(r['character'])]
        if cid in who:
            for k in ('clip', 'fallbackClip'):
                if r.get(k): need.setdefault(r[k], 'attack_clips %s %s (%s)' % (r['character'] or '""', r['move'], k))
    own_attack = any(r.get('character') == cid for r in ac.get('rows', []))
    for fn in ('attack_clips.json', 'dodge_clips.json'):
        d = G.jload(data / fn) or {}
        cs = (ros.chars[cid].get('clipSet') or '')
        for s in d.get('clipSets', []):
            if s.get('id') == cs:
                for c in s.get('clips', []): need.setdefault(c['state'], '%s clipSet %s' % (fn, cs))
        for b in d.get('bake', []):
            if b['character'] == cid:
                for c in b.get('clips', []): need.setdefault(c['state'], '%s bake' % fn)
    dg = G.jload(data / 'dodge.json') or {}
    for sect, fl in (('roll', ['clip']), ('backstep', ['clip']), ('sidestep', ['clip', 'clipLeft', 'clipRight', 'clipBack'])):
        for f in fl:
            v = (dg.get(sect) or {}).get(f, '')
            if v: need.setdefault(v, 'dodge.%s.%s' % (sect, f))
    v = (dg.get('guard') or {}).get('moveClipFallback', '')
    if v: need.setdefault(v, 'dodge.guard.moveClipFallback')
    fi = G.jload(data / 'finishers.json') or {}
    for e in fi.get('entries', []):
        if not e.get('enabled', True) or e.get('placeholder'): continue
        side = e.get('attackerSide', 'player'); killer = ros.player if side == 'player' else ros.enemy; victim = ros.enemy if side == 'player' else ros.player
        if cid == killer and e.get('attackerClip'): need.setdefault(e['attackerClip'], 'finishers %s attackerClip' % e['id'])
        if cid == victim and e.get('victimClip'): need.setdefault(e['victimClip'], 'finishers %s victimClip' % e['id'])
    return need


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('id'); ap.add_argument('--as', dest='role', default=None, choices=['enemy', 'player', 'none'])
    ap.add_argument('--like', default=''); ap.add_argument('--row', default=''); ap.add_argument('--data', default=''); ap.add_argument('--sa', default='')
    ap.add_argument('--strict', action='store_true')
    a = ap.parse_args()
    data0 = Path(a.data) if a.data else PROJECT / 'Assets' / 'Data'
    sa0 = Path(a.sa) if a.sa else PROJECT / 'Assets' / 'StreamingAssets'
    tmp = Path(tempfile.mkdtemp(prefix='newchar_'))
    try:
        for name, src in (('Data', data0), ('StreamingAssets', sa0)):
            d = tmp / name; d.mkdir()
            for f in src.glob('*.json'): shutil.copyfile(f, d / f.name)
        data, sa = tmp / 'Data', tmp / 'StreamingAssets'
        rj = G.jload(data / 'roster.json')
        ids = [c['id'] for c in rj['characters']]
        fake = a.id not in ids
        like_id = a.like or rj['enemy']
        if fake:
            like = next((c for c in rj['characters'] if c['id'] == like_id), rj['characters'][0])
            row = json.loads(Path(a.row).read_text(encoding='utf-8-sig')) if a.row else fake_row(a.id, like)
            rj['characters'].append(row)
            role = a.role or 'enemy'
        else:
            row = next(c for c in rj['characters'] if c['id'] == a.id)
            role = a.role or ('player' if rj['player'] == a.id else 'enemy' if rj['enemy'] == a.id else 'none')
        if role == 'enemy': rj['enemy'] = a.id
        elif role == 'player': rj['player'] = a.id
        for d in (data, sa): (d / 'roster.json').write_text(json.dumps(rj, indent=2), encoding='utf-8')
        cid = a.id
        ros = G.Roster(data)
        out = []; miss = []
        def P(s=''): out.append(s)
        def MISS(s): miss.append(s)

        P('NEW CHARACTER CHECK  id=%s  %s  role=%s   (data: %s)' % (cid, 'FAKE row (not in roster.json)' if fake else 'row from roster.json', role, data0))
        P('=' * 110)
        # ------------------------------------------------------------------ 1 roster row
        P('1. ROSTER ROW (Assets/Data/roster.json, one object in "characters"; copy to StreamingAssets)')
        if fake:
            P('   paste this (clone of "%s", renamed; edit weapon / heightM / armoured for the monster):' % like_id)
            for ln in json.dumps(row, indent=2).splitlines(): P('     ' + ln)
        for k, req in ROW_FIELDS:
            v = row.get(k)
            if v in (None, '') and k not in ('heightM', 'armoured', 'scaleFloor'):
                if req: P('   [ROW  ] roster.%s missing' % k); MISS('roster.%s' % k)
                else: P('   [info ] roster.%s not set (optional)' % k)
        P('   needs no code edit: Roster.cs reads the row, every json loader resolves it by id (RosterResolver); optional keys clipSet / templateFor / lightLayerGroup are read by the Editor tools.')
        # ------------------------------------------------------------------ 2 json rows
        P(); P('2. JSON ROWS (what each character-keyed file has for "%s"; the fallback a row-less character gets)' % cid)
        need_rows = []
        for f, path, field, fb, sev, note in G.COVER:
            d = G.jload(data / f)
            if d is None: P('   [WARN ] %s missing' % f); continue
            rows = G.rows_at(d, path) or []
            own = G.own_row(rows, field, ros, cid)
            star = next((r for r in rows if isinstance(r, dict) and r.get(field) == STAR), None)
            anyrow = next((r for r in rows if isinstance(r, dict) and r.get(field) == ''), None)
            deflt = G.get_path(d, 'defaults') if 'defaults' in fb else None
            tag = '%s:%s' % (f, path)
            if own is not None: P('   [OK   ] %-46s own row' % tag)
            else:
                fbrow = star if ('star' in fb and star) else anyrow if ('any' in fb and anyrow) else deflt if ('defaults' in fb and deflt is not None) else None
                if fbrow is not None:
                    off = isinstance(fbrow, dict) and fbrow.get('enabled') is False
                    if off: P('   [ROW  ] %-46s no own row; the fallback is OFF (%s)  -> add a row to switch it on' % (tag, note)); need_rows.append(tag)
                    else: P('   [OK   ] %-46s fallback row (neutral)' % tag)
                elif 'code' in fb: P('   [ok   ] %-46s code default (%s)' % (tag, note))
                else:
                    P('   [ROW  ] %-46s NO ROW and no fallback: %s' % (tag, note)); need_rows.append(tag)
                    if 'clipImport' not in path: MISS(tag)
        # attack rows per move
        ac = G.jload(data / 'attack_clips.json') or {}
        rows = [r for r in ac.get('rows', []) if r.get('enabled', True)]
        moves = sorted({r['move'] for r in rows})
        own_moves = {r['move'] for r in rows if r['character'] == cid}
        star_moves = {r['move'] for r in rows if r['character'] in ('', STAR)}
        sq = [m for m in moves if m not in own_moves and m not in star_moves]
        P('   attack_clips.json rows: own %d / %d moves, "*" fallback covers %d, %d fall back to the OLD whole-clip squash%s' % (len(own_moves), len(moves), len(star_moves - own_moves), len(sq), (': ' + ', '.join(sq)) if sq else ''))
        if sq: need_rows.append('attack_clips.json rows: ' + ', '.join(sq)); MISS('attack_clips rows ' + ','.join(sq))
        elif cid not in {r['character'] for r in rows}: P('   [info ] the character plays the shared "*" attack rows (Pitborn-measured times); add own rows (character: "%s") once its clips are baked' % cid)
        # files with a clipImport/mats block
        fi = G.jload(data / 'finishers.json') or {}
        if not any(c.get('name') == cid for c in (fi.get('clipImport') or {}).get('characters', [])):
            P('   [ROW  ] finishers.json clipImport.characters: no row for %s (name, referenceState "guard_idle", hipsRestZ measured on its rig, hipsCorrection [1,0,0,0])' % cid); need_rows.append('finishers.clipImport.characters'); MISS('finishers.clipImport row')
        cm = G.jload(data / 'hdrp_char_mats.json') or {}
        txt = json.dumps(cm)
        if row.get('modelGlb') and row['modelGlb'] not in txt and cid not in txt:
            P('   [ROW  ] hdrp_char_mats.json: no extract / materials block for %s (glb material names -> Mats; Tools/char_tex_install.py writes the textures)' % cid); need_rows.append('hdrp_char_mats'); MISS('hdrp_char_mats block')
        cs = G.jload(data / 'character_setup.json') or {}
        if not any(c.get('id') == cid for c in cs.get('characters', [])): P('   [info ] character_setup.json characters[]: no row for %s (optional: rig profile defaults to rigs[0] = mixamo106, template = roster templateFor)' % cid)
        if row.get('clipSet'):
            ok_a = any(s.get('id') == row['clipSet'] for s in ac.get('clipSets', []))
            dc = G.jload(data / 'dodge_clips.json') or {}
            ok_d = any(s.get('id') == row['clipSet'] for s in dc.get('clipSets', []))
            if ok_a and ok_d: P('   [OK   ] clipSet "%s" exists in attack_clips.json and dodge_clips.json: bake entries for %s are built from the roster row (no bake row needed)' % (row['clipSet'], cid))
            else: P('   [ROW  ] clipSet "%s" missing from %s' % (row['clipSet'], ' and '.join(n for n, ok in (('attack_clips.json', ok_a), ('dodge_clips.json', ok_d)) if not ok))); MISS('clipSet ' + row['clipSet'])
        elif not any(b['character'] == cid for b in ac.get('bake', [])):
            P('   [ROW  ] no clipSet on the roster row and no attack_clips.json bake row: no attack / dodge clips will be baked for %s' % cid); MISS('clipSet or bake row')
        # ------------------------------------------------------------------ 3 assets
        P(); P('3. ASSETS (paths from the roster row; "will be made" = produced by an Editor step below)')
        def asset(label, rel, made=''):
            if not rel: return
            if exists(rel): P('   [OK   ] %-22s %s' % (label, rel))
            elif made: P('   [made ] %-22s %s   (%s)' % (label, rel, made))
            else: P('   [ASSET] %-22s %s   MISSING' % (label, rel)); MISS(label + ' ' + rel)
        asset('combat model glb', row.get('modelGlb')); asset('anim glb', row.get('animGlb'))
        asset('controller', row.get('controller'), 'copied from Assets/Characters/_ClipTemplate.controller by Hades > Characters > Set up enemy')
        asset('guard mask', row.get('guardMask'), 'GuardLayerSetup.RunFor builds it from the character\'s own guard_idle bindings')
        fold = os.path.dirname(row.get('modelGlb', '')).replace('\\', '/') if row.get('modelGlb') else 'Assets/Characters/' + cid
        asset('finisher clip folder', row.get('finisherClipFolder'), 'JoanSwap.ImportModel creates it; FinisherClipSetup fills it')
        if row.get('weapon'):
            wl = list((PROJECT / 'Assets' / 'Characters' / 'Weapons').glob('%s*' % row['weapon']))
            if wl: P('   [OK   ] %-22s %s' % ('weapon "%s"' % row['weapon'], wl[0].name))
            else: P('   [ASSET] %-22s no Assets/Characters/Weapons/%s* asset' % ('weapon "%s"' % row['weapon'], row['weapon'])); MISS('weapon ' + row['weapon'])
        if row.get('clipSet'):
            for fn, pat in (('attack_clips.json', '{folder}/{id}_attacks_v2.glb'), ('dodge_clips.json', '{folder}/{id}_dodges.glb')):
                d = G.jload(data / fn) or {}
                s = next((x for x in d.get('clipSets', []) if x.get('id') == row['clipSet']), None)
                if s:
                    outglb = s['outGlb'].replace('{id}', cid).replace('{folder}', fold)
                    asset('baked ' + ('attacks' if 'attack' in fn else 'dodges'), outglb, 'python Tools/bake_%s_clips.py %s' % ('attack' if 'attack' in fn else 'dodge', cid))
                    src_missing = [c['source'] for c in s.get('clips', []) if c.get('source') and not Path(paths_resolve(c['source'])).exists()]
                    if src_missing: P('   [ASSET] %d clip source file(s) of set %s not on disk, e.g. %s' % (len(src_missing), row['clipSet'], src_missing[0])); MISS('clip sources')
        # ------------------------------------------------------------------ 4 controller states
        P(); P('4. ANIMATOR STATES the data asks %s for (gate E logic + clipSet)' % cid)
        need = states_needed(data, ros, cid, role)
        cpath = PROJECT / row.get('controller', '')
        have = None
        if cpath.exists(): have = G.controller_states(cpath)
        else:
            tpl = PROJECT / (cs.get('controllerTemplate') or 'Assets/Characters/_ClipTemplate.controller')
            if tpl.exists(): have = G.controller_states(tpl); P('   controller does not exist: it will be copied from the null-motion template %s (%d states)' % (tpl.name, len(have)))
            else: P('   controller does not exist and the null-motion template %s is not in the project yet (laneCLIPBUILD: Tools > Combat > Create Null-Motion Clip Template)' % (cs.get('controllerTemplate') or '_ClipTemplate.controller'))
        if have is None: have = set()
        miss_states = [(s, w) for s, w in sorted(need.items()) if s not in have]
        P('   %d state names needed, %d in the controller, %d missing' % (len(need), len(need) - len(miss_states), len(miss_states)))
        for s, w in miss_states:
            made = 'baked by the clip pipeline, then CharacterSetup.EnsureStates / Attack|DodgeClipSetup adds it' if (s.startswith('atk_') or s.startswith('dodge_')) else 'FinisherClipSetup' if s.startswith('fin_') else 'comes from the combat glb clip of that name (FillNullStates)'
            P('   [STATE] %-18s %-52s -> %s' % (s, w, made))
        # ------------------------------------------------------------------ 5 gates filtered to this id
        P(); P('5. data_gates.py (laneHCKIT2) on a temp copy that contains this row, filtered to %s' % cid)
        R = G.Res()
        class Args: fix = False; overlay = None
        for g, fn in (('B', G.gate_B), ('C', G.gate_C), ('E', G.gate_E), ('F', G.gate_F)):
            fn(R, data, sa, Args, ros)
        shown = 0
        for g, lvl, item, msg in R.rows:
            if lvl in ('FAIL', 'WARN') and (('[%s]' % cid) in item or cid in item or ('[%s ' % cid) in item or ('%s]' % cid) in item):
                P('   [%s %s] %s  %s' % (g, lvl, item, msg)); shown += 1
        if not shown: P('   (no FAIL / WARN for this id)')
        P('   whole-project totals: %d FAIL, %d WARN (not only this id)' % (R.count(None, 'FAIL'), R.count(None, 'WARN')))
        # ------------------------------------------------------------------ 6 editor steps
        P(); P('6. EDITOR STEPS once the row + assets exist (Unity open, one batch or menu each; all idempotent)')
        for i, s in enumerate(['roster.json row (+ StreamingAssets copy), set "%s": "%s" if it is the %s' % ('enemy' if role == 'enemy' else 'player', cid, role),
                               'python Tools/bake_attack_clips.py %s ; python Tools/bake_dodge_clips.py %s   (needs roster clipSet or a bake row)' % (cid, cid),
                               'Hades > Characters > Set up %s from roster row   (controller from template, import, clips for this id only, guard mask, missing states, scene object, references, light layer, rim)' % (role if role != 'none' else 'enemy'),
                               'Tools > Finishers > Add Finisher Clips (needs the finishers.clipImport row), Tools/char_tex_install.py + HdrpCharMats.Run for the skin',
                               'python Tools/data_gates.py --quiet   and   python Tools/new_character_check.py %s   until nothing is MISSING' % cid], 1): P('   %d. %s' % (i, s))
        # ------------------------------------------------------------------ 7 known TODOs
        todo = (cs.get('todo') or {}).get(cid)
        if todo:
            P(); P('7. DATA TODO recorded in character_setup.json for %s (not invented here; owner: %s)' % (cid, todo.get('owner', '')))
            for k in ('missingAttackRows', 'missingStates', 'finisherStates'):
                if todo.get(k): P('   %-18s %s' % (k, ', '.join(todo[k])))
                if todo.get(k + 'Note'): P('      %s' % todo[k + 'Note'])
        P(); P('SUMMARY  %d item(s) still MISSING%s' % (len(miss), (': ' + '; '.join(miss[:12]) + (' ...' if len(miss) > 12 else '')) if miss else ''))
        print('\n'.join(out))
        sys.exit(1 if (a.strict and miss) else 0)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == '__main__':
    main()

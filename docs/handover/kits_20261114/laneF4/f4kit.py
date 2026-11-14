#!/usr/bin/env python3
"""F4 "gear loop you can SEE" kits (file-only agent, 2026-11-14). Style = laneLG\\lgkit.py: DRY RUN BY DEFAULT, --go writes, md5 before/after, byte-identical revert, refuses the live build.
Class of every kit is in its name and in KITS[..]['cls']:  CONTENT = wiring / ids / visibility / missing rows (safe to ship)   TUNING = numbers (HOLD for Dom, he has not played yet)
  python -I f4kit.py list
  python -I f4kit.py <kit> make [--rebase]            builds <kit>\\base + <kit>\\after + MD5.json from the tree twins (or, for a chained kit, from the previous kit's after)
  python -I f4kit.py <kit> apply  [--target DIR] [--go]   DIR = StreamingAssets of a QA copy; none = BOTH tree twins (Assets\\Data + Assets\\StreamingAssets)
  python -I f4kit.py <kit> revert [--target DIR] [--go]   byte-identical; refuses unless the file == the kit's after md5
  python -I f4kit.py <kit> proof                      scratch copy only: apply, md5 == after, revert, bytes == base. Writes <kit>\\PROOF.txt. Touches nothing live.
Every edit = one regex that must match exactly once + a JSON mutation; make asserts json(after) == mutate(json(base)).
Refuses a target inside a build named by Builds\\PLAY*.bat and Builds\\Play. Nothing here writes outside laneF4 unless you run apply --go without --target."""
import os, re, sys, json, glob, shutil, hashlib, time
REPO = r'C:\Users\dom12\Documents\unity_test_hdrp'; HERE = os.path.dirname(os.path.abspath(__file__))
TWINS = [os.path.join(REPO, 'Assets', 'Data'), os.path.join(REPO, 'Assets', 'StreamingAssets')]
LG_SIGDROPS_AFTER = os.path.join(REPO, '_agents', 'laneLG', 'sigdrops', 'after', 'gear.json')   # Dom's call: Erlking also drops erlking_axe (kits dev's sigdrops, base ca98899989 -> 0e1d13ee68)

# ---------------- DATA (every number / id the kits write lives here, not in the logic) ----------------
ERLKING_AXE_ROW = {'item': 'erlking_axe', 'buyValue': 45, 'sellable': True, 'sellOverride': 0}   # 45 = war_axe's row (same glb family, weight 9 x 5); PLACEHOLDER like every row, Dom owns the price
Z7_ARMOURER_ADD = ['pitborn_chest']   # existing item (armor 10, poise 8, glb present, row buyValue 120) that no drop and no stall hands out today
# presentation only: loot_ui.json rarities common/fine/rare/relic colour the pillar + label. Rule: starting / basic stall = common, upper stall tier = fine, boss reward = rare, Dom's hero pieces = relic.
RARITY = {'joan_tunic': 'common', 'joan_boots': 'common', 'joan_bracers': 'common', 'joan_greaves': 'common', 'cuirass_joan': 'common', 'arms_joan': 'common', 'legarmour_joan': 'common', 'joan_shield': 'common',
          'joan_helmet': 'fine', 'bascinet_joan': 'fine', 'joan_tabard': 'fine', 'cap_joan': 'fine',
          'knight_sword': 'common', 'war_axe': 'fine', 'iron_maul': 'fine', 'boar_spear': 'fine',
          'pitborn_maul': 'rare', 'sobek_trident': 'rare', 'pitborn_pauldron_joan': 'rare', 'pitborn_chest': 'rare', 'erlking_axe': 'relic'}
WEAPON_STAT_ITEMS = ['knight_sword', 'war_axe', 'iron_maul', 'boar_spear', 'pitborn_maul', 'sobek_trident', 'erlking_axe']   # Joan-usable mainHand weapons; claws of monsters excluded
WEAPON_STATS = [('damage', 'damageMul'), ('speed', 'speedMul'), ('reach', 'reachMul'), ('posture', 'postureMul')]   # statList stat id <- gear.json weapon{} field (the numbers are COPIED from weapon{} at make time, never typed)
UI_STAT_ROWS = [('damage', 'Damage', 2, True), ('speed', 'Speed', 2, True), ('reach', 'Reach', 2, True), ('posture', 'Posture', 2, True)]
# TUNING (Dom's numbers after he plays): make the z2..z6 rewards real steps. old -> new, each asserted at make.
TUNE = [('joan_tabard', 'armor', 3, 8), ('cap_joan', 'armor', 3, 7), ('cap_joan', 'poise', 1, 3)]   # (item, stats key, old, new)
TUNE_W = [('sobek_trident', 'damageMul', '1.0', '1.2'), ('erlking_axe', 'damageMul', '1.0', '1.6')]   # (item, weapon field, old text, new text); the statList copy follows

# ---------------- op builders ----------------
def _item_rx(i, tail): return r'("id":\s*"%s",\s*"(?:shield|name)".*?%s)' % (i, tail)   # items start with "id", then "shield" (most) or "name" (joan_shield)
def _it(doc, i): return [x for x in doc['items'] if x['id'] == i][0]

def op_market_row():
    def mut(d): d['rows'].append(dict(ERLKING_AXE_ROW))
    txt = ',\n    {\n      "item": "%(item)s",\n      "buyValue": %(buyValue)d,\n      "sellable": true,\n      "sellOverride": 0\n    }' % ERLKING_AXE_ROW
    return {'rx': r'("item":\s*"sobek_trident",\s*"buyValue":\s*60,\s*"sellable":\s*true,\s*"sellOverride":\s*0\s*\})', 'ins': txt, 'mut': mut}

def op_z7_stock():
    ops = []
    for iid in Z7_ARMOURER_ADD:
        def mut(d, iid=iid): [s for s in d['stalls'] if s['id'] == 'z7_trade_a'][0]['items'].append(iid)
        ops.append({'rx': r'("id":\s*"z7_trade_a",.*?"items":\s*\[[^\]]*"joan_greaves")', 'ins': ',\n        "%s"' % iid, 'mut': mut})
    return ops

def _fmt(v): return repr(float(v)) if not float(v).is_integer() else '%.1f' % float(v)
def op_gearvis(base_doc):
    ops = []
    for iid, rar in RARITY.items():
        it = _it(base_doc, iid)
        ins = '      "rarity": "%s",\n' % rar; stl = None
        if iid in WEAPON_STAT_ITEMS:
            stl = [{'stat': s, 'value': float(it['weapon'][f])} for s, f in WEAPON_STATS]
            ins += '      "statList": [\n' + ',\n'.join('        { "stat": "%s", "value": %s }' % (e['stat'], _fmt(e['value'])) for e in stl) + '\n      ],\n'
        def mut(d, iid=iid, rar=rar, stl=stl):
            x = _it(d, iid); x['rarity'] = rar
            if stl: x['statList'] = [dict(e) for e in stl]
        ops.append({'rx': _item_rx(iid, r'"dropChance":\s*[\d.]+,\n'), 'ins': ins, 'mut': mut, 'after_group': True})
    return ops

def op_ui_stats():
    txt = ''.join(',\n    { "stat": "%s", "label": "%s", "decimals": %d, "higherIsBetter": %s }' % (s, l, d, 'true' if h else 'false') for s, l, d, h in UI_STAT_ROWS)
    def mut(d): d['stats'].extend({'stat': s, 'label': l, 'decimals': dd, 'higherIsBetter': h} for s, l, dd, h in UI_STAT_ROWS)
    return {'rx': r'("stat":\s*"weight",\s*"label":\s*"Weight",\s*"decimals":\s*1,\s*"higherIsBetter":\s*false\s*\})', 'ins': txt, 'mut': mut}

def op_tune():
    ops = []
    for iid, key, old, new in TUNE:
        def mut(d, iid=iid, key=key, old=old, new=new):
            s = _it(d, iid)['stats']; assert s[key] == old, (iid, key, s[key]); s[key] = new
        ops.append({'rx': _item_rx(iid, r'"stats":\s*\{[^}]*?"%s":\s*' % key), 'old': str(old), 'new': str(new), 'mut': mut})
    for iid, f, old, new in TUNE_W:
        def mut(d, iid=iid, f=f, old=old, new=new):
            it = _it(d, iid); assert abs(it['weapon'][f] - float(old)) < 1e-9; it['weapon'][f] = float(new)
            for e in it['statList']:
                if e['stat'] == [s for s, ff in WEAPON_STATS if ff == f][0]: e['value'] = float(new)
        ops.append({'rx': _item_rx(iid, r'"weapon":\s*\{.*?"%s":\s*' % f), 'old': old, 'new': new, 'mut': mut})
        sid = [s for s, ff in WEAPON_STATS if ff == f][0]   # the statList copy (gearvis) must move with the weapon number: one edit, no drift
        ops.append({'rx': _item_rx(iid, r'"statList":\s*\[(?:(?!"stat": "%s").)*?"stat": "%s", "value": ' % (sid, sid)), 'old': old, 'new': new, 'mut': lambda d: None})
    return ops

# kit: file, base = tree twins (baseMd5) or a file/prev kit (baseFrom); ops callable(base_doc)->list
KITS = {
 'mkt':        {'cls': 'CONTENT', 'file': 'market.json', 'baseMd5': '4b8d2aa597', 'ops': lambda b: [op_market_row()],
                'what': 'market.json: add the missing sell row erlking_axe (without it the trader refuses the axe the moment Erlking drops it: not-sellable)'},
 'mkt_z7':     {'cls': 'CONTENT', 'file': 'market.json', 'baseMd5': '4b8d2aa597', 'ops': lambda b: [op_market_row()] + op_z7_stock(),
                'what': 'mkt + z7 Armourer also stocks pitborn_chest (existing item + existing price): zone 7 finally sells something the hub does not. ALTERNATIVE to mkt (do not stack). Stock choice = loot design, Dom may veto'},
 'gearvis':    {'cls': 'CONTENT', 'file': 'gear.json', 'baseMd5': 'ca98899989', 'ops': lambda b: op_gearvis(b),
                'what': 'gear.json: rarity on every obtainable item (loot pillar + label colour were all one grey) + statList damage/speed/reach/posture on Joan-usable weapons (equip screen could show only armor/poise/weight, so a weapon step was invisible). Numbers COPIED from weapon{}'},
 'gearvis_sd': {'cls': 'CONTENT', 'file': 'gear.json', 'baseFrom': LG_SIGDROPS_AFTER, 'baseMd5': '0e1d13ee68', 'ops': lambda b: op_gearvis(b),
                'what': 'same as gearvis, for a tree that already carries the kits dev\'s sigdrops (Erlking drops erlking_axe). gear.json kits do not stack: pick gearvis OR gearvis_sd'},
 'uistats':    {'cls': 'CONTENT', 'file': 'loot_ui.json', 'baseMd5': 'f28ab4016c', 'ops': lambda b: [op_ui_stats()],
                'what': 'loot_ui.json stats[]: labels for damage/speed/reach/posture so the equip screen lists and compares them. Needs gearvis or gearvis_sd to show anything'},
 't_curve':    {'cls': 'TUNING', 'file': 'gear.json', 'baseFrom': 'gearvis_sd', 'ops': lambda b: op_tune(),
                'what': 'HOLD FOR DOM. z4 tabard armor 3->8, z6 cap 3/1->7/3, sobek_trident damage 1.0->1.2, erlking_axe damage 1.0->1.6 (statList copy moves with it). Chained AFTER gearvis_sd (which needs sigdrops)'},
}

def md5(b): return hashlib.md5(b).hexdigest()[:10]
def rd(p): return open(p, 'rb').read()
def wr(p, b): os.makedirs(os.path.dirname(p), exist_ok=True); open(p, 'wb').write(b)

def base_bytes(g):
    s = KITS[g]; f = s['file']
    if 'baseFrom' in s:
        bf = s['baseFrom']
        if bf in KITS:
            p = os.path.join(HERE, bf, 'after', f)
            if not os.path.exists(p): sys.exit('make %s first (chained base)' % bf)
            return rd(p)
        return rd(bf)
    a, b = rd(os.path.join(TWINS[0], f)), rd(os.path.join(TWINS[1], f))
    if a != b: sys.exit('twins differ: ' + f)
    return a

def build(g):
    s = KITS[g]; a = base_bytes(g); f = s['file']
    if '--rebase' not in sys.argv and 'baseMd5' in s and md5(a) != s['baseMd5']: sys.exit('BASE CHANGED since staging: %s %s (staged on %s). Re-read the file, then make --rebase' % (f, md5(a), s['baseMd5']))
    t = a.decode('utf-8'); assert '\r' not in t and not t.startswith('\ufeff'), 'file is not plain LF utf-8'
    base_doc = json.loads(t); pa = json.loads(t); nt = t
    for e in s['ops'](base_doc):
        ms = list(re.finditer(e['rx'], nt, re.S))
        if len(ms) != 1: sys.exit('EDIT FAILED: %d matches for %s' % (len(ms), e['rx'][:120]))
        m = ms[0]
        if 'ins' in e: nt = nt[:m.end()] + e['ins'] + nt[m.end():]
        else:   # number swap right after the matched prefix
            tail = re.match(r'[-\d.]+', nt[m.end():]); assert tail and tail.group(0) == e['old'], (e['rx'][:80], tail and tail.group(0), e['old'])
            nt = nt[:m.end()] + str(e['new']) + nt[m.end() + len(tail.group(0)):]
        e['mut'](pa)
    pb = json.loads(nt); assert pa == pb, 'after != base + the listed edits'
    return a, nt.encode('utf-8')

def guard(d):
    bd = os.path.normcase(os.path.abspath(d)); names = set()
    for p in glob.glob(os.path.join(REPO, 'Builds', 'PLAY*.bat')): names |= set(re.findall(r'%~dp0(HDRP_[^\\"]+)\\', open(p, errors='ignore').read()))
    for n in names:
        if os.path.normcase(os.path.join(REPO, 'Builds', n)) in bd: sys.exit('REFUSED: %s is inside %s, named by a PLAY*.bat launcher (live/rollback). Use a QA copy.' % (d, n))
    if os.path.normcase(os.path.join(REPO, 'Builds', 'Play')) in bd: sys.exit('REFUSED: Builds\\Play')

def run(g, cmd, tg, go):
    s = KITS[g]; f = s['file']; GD = os.path.join(HERE, g)
    man = json.load(open(os.path.join(GD, 'MD5.json'))); rev = cmd == 'revert'
    dirs = [tg] if tg else TWINS
    for d in dirs: guard(d)
    src, want = ('base', 'after') if rev else ('after', 'base')
    assert md5(rd(os.path.join(GD, 'base', f))) == man['base'] and md5(rd(os.path.join(GD, 'after', f))) == man['after'], 'kit files changed after make'
    for d in dirs:
        cur = md5(rd(os.path.join(d, f))); ok = cur == man[want]
        print('%-7s %-14s %-14s now %s (expect %s %s) -> %s %s  [%s]' % ('OK' if ok else ('ALREADY' if cur == man[src] else 'BAD'), os.path.basename(d.rstrip('\\')), f, cur, want, man[want], src, man[src], s['cls']))
        if not ok and cur != man[src]: sys.exit('REFUSED: %s in %s is neither the kit base nor its after' % (f, d))
    if not go: print('DRY RUN (%s %s): nothing written. Add --go.' % (g, cmd.upper())); return
    stamp = time.strftime('%Y%m%d_%H%M%S'); bk = os.path.join(GD, 'backup', stamp)
    for i, d in enumerate(dirs):
        os.makedirs(os.path.join(bk, str(i)), exist_ok=True); shutil.copy2(os.path.join(d, f), os.path.join(bk, str(i), f)); shutil.copyfile(os.path.join(GD, src, f), os.path.join(d, f))
    open(os.path.join(GD, 'STATE.txt'), 'a', encoding='utf-8').write('%s %s %s dirs=%s backup=%s\n' % (stamp, g, cmd.upper(), dirs, bk))
    print('WROTE %s into %s; backup %s' % (src, dirs, bk))

def proof(g):
    s = KITS[g]; f = s['file']; GD = os.path.join(HERE, g); man = json.load(open(os.path.join(GD, 'MD5.json')))
    sc = os.path.join(HERE, '_scratch', g, 'StreamingAssets'); shutil.rmtree(os.path.join(HERE, '_scratch', g), ignore_errors=True); os.makedirs(sc)
    shutil.copyfile(os.path.join(GD, 'base', f), os.path.join(sc, f)); b0 = rd(os.path.join(sc, f)); lines = []
    assert md5(b0) == man['base']
    sys.argv = [sys.argv[0], g, 'apply']; run(g, 'apply', sc, True); a1 = rd(os.path.join(sc, f)); lines.append('apply  -> md5 %s (expect %s) %s' % (md5(a1), man['after'], 'OK' if md5(a1) == man['after'] and a1 == rd(os.path.join(GD, 'after', f)) else 'FAIL'))
    run(g, 'revert', sc, True); b1 = rd(os.path.join(sc, f)); lines.append('revert -> md5 %s (expect %s) byte-identical to base: %s' % (md5(b1), man['base'], 'YES' if b1 == b0 else 'NO'))
    run(g, 'apply', sc, True); run(g, 'revert', sc, True)   # twice round trip
    ok = b1 == b0 and a1 != b0 and rd(os.path.join(sc, f)) == b0
    txt = '%s [%s] %s\nscratch copy %s\n%s\nTWICE round trip byte-identical: %s\nPROOF %s\n' % (g, s['cls'], f, sc, '\n'.join(lines), 'YES' if ok else 'NO', 'OK' if ok else 'FAILED')
    open(os.path.join(GD, 'PROOF.txt'), 'w', encoding='utf-8').write(txt); print(txt)
    if not ok: sys.exit(1)

def main():
    if len(sys.argv) >= 2 and sys.argv[1] == 'list':
        for k, v in KITS.items(): print('%-11s %-7s %-13s %s' % (k, v['cls'], v['file'], v['what']))
        return
    if len(sys.argv) < 3 or sys.argv[1] not in KITS: print(__doc__); return
    g, cmd = sys.argv[1], sys.argv[2]; f = KITS[g]['file']; GD = os.path.join(HERE, g)
    if cmd == 'make':
        base, after = build(g)
        wr(os.path.join(GD, 'base', f), base); wr(os.path.join(GD, 'after', f), after)
        json.dump({'file': f, 'class': KITS[g]['cls'], 'base': md5(base), 'after': md5(after)}, open(os.path.join(GD, 'MD5.json'), 'w'), indent=1)
        print(g, KITS[g]['cls'], f, 'md5', md5(base), '->', md5(after)); return
    if cmd == 'proof': proof(g); return
    if cmd not in ('apply', 'revert'): print(__doc__); return
    tg = sys.argv[sys.argv.index('--target') + 1] if '--target' in sys.argv else None
    run(g, cmd, tg, '--go' in sys.argv)
main()

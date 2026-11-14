#!/usr/bin/env python3
"""LG kits (base = live HDRP_v109lg, tree twins equal lg). Staged by the Frankendom bandits dev, 2026-11-13. DRY RUN BY DEFAULT. HOT, data only.
Kits: ride (mount.json seat per variant), pitglow (accent_lights L10_Z1_FurnaceEntry), z6pool (look_zones z6_erlking row).
  python -I lgkit.py <kit> make [--rebase]
  python -I lgkit.py <kit> apply  [--target DIR] [--go]    DIR = StreamingAssets of a QA copy; none = BOTH tree twins
  python -I lgkit.py <kit> revert [--target DIR] [--go]    byte-identical; refuses unless the file == the kit's after md5
Every edit is one number in the raw text; make asserts after == base + exactly those values (JSON equality).
Refuses a target inside a build named by Builds\\PLAY*.bat and Builds\\Play."""
import os, re, sys, json, glob, shutil, hashlib, time
REPO = r'C:\Users\dom12\Documents\unity_test_hdrp'; HERE = os.path.dirname(os.path.abspath(__file__))
TWINS = [os.path.join(REPO, 'Assets', 'Data'), os.path.join(REPO, 'Assets', 'StreamingAssets')]
def E(rx, old, new, path): return {'rx': rx, 'old': old, 'new': new, 'path': path}
def I(rx, text, key, val): return {'rx': rx, 'ins': text, 'key': key, 'val': val}   # insert a new top-level key after the matched line
def V(i, k): return ('variants', i, 'horse', k)
KITS = {
 'ride': {'file': 'mount.json', 'baseMd5': 'fa8535aeca', 'edits': [
   E(r'("id":\s*"warhorse_demon".*?"seatRaiseM":\s*)0\.82(?=,)', '0.82', '0.87', ['variants', 0, 'horse', 'seatRaiseM']),
   E(r'("id":\s*"warhorse_demon".*?"seatBackM":\s*)0\.15(?=,)', '0.15', '0.12', ['variants', 0, 'horse', 'seatBackM']),
   E(r'("id":\s*"skeletal_camel".*?"seatRaiseM":\s*)0\.866(?=,)', '0.866', '0.916', ['variants', 1, 'horse', 'seatRaiseM']),
   E(r'("id":\s*"skeletal_camel".*?"seatBackM":\s*)0\.156(?=,)', '0.156', '0.126', ['variants', 1, 'horse', 'seatBackM']),
   E(r'("id":\s*"warhorse_brown".*?"seatRaiseM":\s*)0\.72(?=,)', '0.72', '0.77', ['horse', 'seatRaiseM']),
   E(r'("id":\s*"warhorse_brown".*?"seatBackM":\s*)0\.13(?=,)', '0.13', '0.10', ['horse', 'seatBackM'])]},
 'hidekey': {'file': 'mount.json', 'baseMd5': 'fa8535aeca', 'edits': [I(r'("debugLog":\s*false,\r?\n)', '  "hideWeaponWhileMounted": true,\n', 'hideWeaponWhileMounted', True)]},
 'camback': {'file': 'mount.json', 'baseMd5': 'fa8535aeca', 'edits': [
   E(r'("camera":\s*\{\s*"backMul":\s*)1\.25(?=,)', '1.25', '1.6', ['camera', 'backMul']),
   E(r'("camera":\s*\{\s*"backMul":\s*[\d.]+,\s*"heightMul":\s*)1\.15(?=\s*\})', '1.15', '1.25', ['camera', 'heightMul'])]},
 'pitglow': {'file': 'accent_lights.json', 'baseMd5': '5efee8804a', 'edits': [
   E(r'("name":\s*"L10_Z1_FurnaceEntry".*?"pos":\s*\[\s*0\.0,\s*)1\.4(?=,)', '1.4', '2.2', ['rows|name=L10_Z1_FurnaceEntry', 'pos', 1]),
   E(r'("name":\s*"L10_Z1_FurnaceEntry".*?"candela":\s*)6000(?=,)', '6000', '20000', ['rows|name=L10_Z1_FurnaceEntry', 'candela']),
   E(r'("name":\s*"L10_Z1_FurnaceEntry".*?"range":\s*)10\.0(?=,)', '10.0', '14.0', ['rows|name=L10_Z1_FurnaceEntry', 'range'])]},
 'z6pool': {'file': 'look_zones.json', 'baseMd5': 'f769ee5d14', 'edits': [
   E(r'("id":\s*"z6_erlking",\s*"zFrom".*?"keyElevationDeg":\s*)38\.0(?=,)', '38.0', '30.0', ['rows|id=z6_erlking', 'keyElevationDeg']),
   E(r'("id":\s*"z6_erlking",\s*"zFrom".*?"bounceLux":\s*)700\.0(?=,)', '700.0', '400.0', ['rows|id=z6_erlking', 'bounceLux'])]},
}
KITS['z6pool2'] = {'file': 'look_zones.json', 'baseMd5': '6960edbdaf', 'edits': [   # STRONGER z6 (base = lk): bounce 700 -> 250 (z6pool was 400), key 38 -> 30
   E(r'("id":\s*"z6_erlking",\s*"zFrom".*?"keyElevationDeg":\s*)38\.0(?=,)', '38.0', '30.0', ['rows|id=z6_erlking', 'keyElevationDeg']),
   E(r'("id":\s*"z6_erlking",\s*"zFrom".*?"bounceLux":\s*)700\.0(?=,)', '700.0', '250.0', ['rows|id=z6_erlking', 'bounceLux'])]}
KITS['z6lilac'] = {'file': 'accent_lights.json', 'baseMd5': 'c9c624a04b', 'edits': [   # the pale 8500 K down-pools under the court entry (z 262-267, where Joan stands) cut hard; rows stay, only candela
   E(r'("name":\s*"AccV_Erlking_A".*?"candela":\s*)30000(?=,)', '30000', '6000', ['rows|name=AccV_Erlking_A', 'candela']),
   E(r'("name":\s*"AccV_Erlking_B".*?"candela":\s*)30000(?=,)', '30000', '6000', ['rows|name=AccV_Erlking_B', 'candela']),
   E(r'("name":\s*"L10_Z6_PaleEntry".*?"candela":\s*)45000(?=,)', '45000', '12000', ['rows|name=L10_Z6_PaleEntry', 'candela'])]}
def _fa(zid, n, old, new): return E(r'("id":\s*"bandit_%s_p_%d",\s*"enabled":\s*true,.*?"aggro":\s*)%s(?=,)' % (zid, n, re.escape(old)), old, new, ['zones|id=' + {'z5': 'z5_humbaba', 'z6': 'z6_erlking'}[zid], 'spawns|id=bandit_%s_p_%d' % (zid, n), 'aggro'])
KITS['fodder'] = {'file': 'zones.json', 'baseMd5': '9ec689fe63', 'edits': [   # PRE-boss raiders z5/z6 sat 4.7-12.5 m from the arrival spot with aggro 12-13 = in aggro on frame 1; aggro cut so Joan can step in first (leash unchanged)
   _fa('z5', 1, '12.0', '6.0'), _fa('z5', 2, '12.0', '4.0'), _fa('z5', 3, '12.0', '6.0'),
   _fa('z6', 1, '13.0', '6.0'), _fa('z6', 2, '13.0', '6.0'), _fa('z6', 3, '13.0', '6.0')]}
def _fo(zid, n):
    e = E(r'("id":\s*"bandit_%s_p_%d",\s*"enabled":\s*)true(?=,)' % (zid, n), 'true', 'false', ['zones|id=' + {'z5': 'z5_humbaba', 'z6': 'z6_erlking'}[zid], 'spawns|id=bandit_%s_p_%d' % (zid, n), 'enabled']); e['bool'] = True; return e
KITS['fodderoff'] = {'file': 'zones.json', 'baseMd5': '9ec689fe63', 'edits': [_fo(z, n) for z in ('z5', 'z6') for n in (1, 2, 3)]}   # ALTERNATIVE to fodder2: the six PRE-boss raider spawn rows enabled false (post-boss groups z5_a/b, z6_a/b untouched)
def _ip(rx, text, key, val): e = I(rx, text, key, val); e['parent'] = ['engage']; return e
KITS['spangrace'] = {'file': 'zones.json', 'baseMd5': '9ec689fe63', 'edits': [   # DATA half of the ARRIVALGRACE v1 C# patch (laneLG\arrivalgrace): inert until that code is built (JsonUtility ignores unknown keys)
   _ip(r'("graceAfterArriveSeconds":\s*8\.0,\r?\n)', '    "spanGraceSeconds": 10.0,\n', 'spanGraceSeconds', 10.0),
   _ip(r'("spanGraceSeconds":\s*10\.0,\r?\n)', '    "spanGraceZones": ["z5_humbaba", "z6_erlking"],\n', 'spanGraceZones', ['z5_humbaba', 'z6_erlking'])]}
def _rs(key, old, new): return E(r'("respawn":\s*\{.*?"%s":\s*)%s(?=,)' % (key, re.escape(old)), old, new, ['hp', 'respawn', key])
_RETRY = [_rs('delayS', '3.0', '2.0'), _rs('coverS', '0.5', '0.4'), _rs('foeResetDelayS', '2.5', '2.0')]   # F3: death -> standing up 3.0 s -> 2.0 s (foe reset follows max(delayS, foeResetDelayS))
KITS['retry'] = {'file': 'zones.json', 'baseMd5': '9ec689fe63', 'edits': list(_RETRY)}
KITS['retryls'] = {'file': 'zones.json', 'baseMd5': '0f092887dc', 'edits': list(_RETRY)}   # retry rows only, on ls (zones.json already carries spangrace: the span grace keys are kept)
KITS['spanretry'] = {'file': 'zones.json', 'baseMd5': '9ec689fe63', 'edits': list(KITS['spangrace']['edits']) + list(_RETRY)}   # spangrace (needs the arrivalgrace code) + retry in ONE kit, same file, so they never stack
# F1 boot-black data cuts. loading_card.json base = live lp (card kit applied), startup_warm.json base = live lp
KITS['f1card'] = {'file': 'loading_card.json', 'baseMd5': '1b1f2b6d88', 'edits': [
   E(r'("minSeconds":\s*)3\.0(?=,)', '3.0', '1.5', ['minSeconds']), E(r'("maxSeconds":\s*)38\.0(?=,)', '38.0', '4.0', ['maxSeconds']),
   E(r'("extraAfterPrewarmS":\s*)1\.0(?=,)', '1.0', '0.3', ['extraAfterPrewarmS']), E(r'("fadeS":\s*)0\.6(?=,)', '0.6', '0.4', ['fadeS']),
   E(r'("calmForS":\s*)1\.5(?=,)', '1.5', '0.5', ['calmForS'])]}
KITS['f1warm'] = {'file': 'startup_warm.json', 'baseMd5': '0f1d81d0a1', 'edits': [
   E(r'("gearCapSeconds":\s*)25\.0(?=,)', '25.0', '3.0', ['cover', 'gearCapSeconds']), E(r'("holdFramesAfter":\s*)24(?=,)', '24', '12', ['cover', 'holdFramesAfter']),
   E(r'("gearAttachFrames":\s*)20(?=\r?\n)', '20', '10', ['cover', 'gearAttachFrames'])]}
# F3 signature rewards (content, Dom's loot design) + F4 power-step numbers (TUNING, Dom's after he plays). bases = ls tree twins
def L(rx, lit, path, val): return {'rx': rx, 'lit': lit, 'path': path, 'val': val}
_ek = r'"id":\s*"erlking_axe",\s*"shield".*?'
KITS['sigdrops'] = {'file': 'gear.json', 'baseMd5': 'ca98899989', 'edits': [   # Erlking drops its antler great-axe (glb exists, 26 MB) next to the kettle cap
   L(r'(' + _ek + r'"dropFrom":\s*)(?P<old>\[\s*\])', '["Erlking"]', ['items|id=erlking_axe', 'dropFrom'], ['Erlking']),
   E(r'(' + _ek + r'"dropChance":\s*)0\.0(?=,)', '0.0', '1.0', ['items|id=erlking_axe', 'dropChance']),
   E(r'("victim":\s*"Erlking",\s*"max":\s*)1(?=\s*\})', '1', '2', ['drop', 'maxPerKillBy|victim=Erlking', 'max'])]}
KITS['sigfull'] = {'file': 'gear.json', 'baseMd5': 'ca98899989', 'edits': list(KITS['sigdrops']['edits']) + [   # + TUNING: the z6 reward stops being the weakest armour step (cap 3/1 -> 7/3)
   E(r'("id":\s*"cap_joan",\s*"shield".*?"armor":\s*)3(?=,)', '3', '7', ['items|id=cap_joan', 'stats', 'armor']),
   E(r'("id":\s*"cap_joan",\s*"shield".*?"poise":\s*)1(?=,)', '1', '3', ['items|id=cap_joan', 'stats', 'poise'])]}
def _ab(aid, old, new):
    return L(r'("id":\s*"%s".*?"weapons":\s*\[\s*"%s")(?P<old>)' % (aid, old), ',\n        "%s"' % new, ['abilities|id=' + aid, 'weapons'], [old, new])
KITS['sigabil'] = {'file': 'abilities.json', 'baseMd5': '47755302f5', 'edits': [   # the boss weapons get the abilities of their weapon family at once (Joan only)
   _ab('maul_slam', 'iron_maul', 'pitborn_maul'), _ab('maul_smash', 'iron_maul', 'pitborn_maul'),
   _ab('spear_lunge', 'boar_spear', 'sobek_trident'), _ab('spear_sweep', 'boar_spear', 'sobek_trident'),
   _ab('axe_cleave', 'war_axe', 'erlking_axe'), _ab('axe_chop', 'war_axe', 'erlking_axe')]}
# F3 CONTENT kits (wiring / missing rows, no armor or poise number touched). gear.json base ca98899989, boss_phase.json base read at make time.
def _tel(boss, rad, sec): return L(r'("id":\s*"%s".*?"telegraph":\s*)(?P<old>\{\s*\})' % boss, '{ "radiusM": %s, "seconds": %s }' % (rad, sec), ['rows|id=' + boss, 'telegraph'], {'radiusM': rad, 'seconds': sec})
KITS['tells'] = {'file': 'boss_phase.json', 'baseMd5': '793a9a7f29', 'edits': [_tel('Pitborn', 2.4, 0.6), _tel('Humbaba', 3.0, 0.7)]}   # the two bosses whose phase change had NO ground-ring tell (rows empty {}); radius/seconds copied from the nearest filled rows (Sobek 2.4/0.6, Grendel 2.8/0.65, Erlking 3.2/0.75)
_ER = KITS['sigdrops']['edits'][:2]   # erlking_axe dropFrom + dropChance
KITS['f3gear'] = {'file': 'gear.json', 'baseMd5': 'ca98899989', 'edits': list(_ER) + [
   L(r'("id":\s*"bascinet_joan",\s*"shield".*?"dropFrom":\s*)(?P<old>\[\s*\])', '["Erlking"]', ['items|id=bascinet_joan', 'dropFrom'], ['Erlking']),
   E(r'("id":\s*"bascinet_joan",\s*"shield".*?"dropChance":\s*)0\.0(?=,)', '0.0', '1.0', ['items|id=bascinet_joan', 'dropChance']),
   E(r'("victim":\s*"Erlking",\s*"max":\s*)1(?=\s*\})', '1', '3', ['drop', 'maxPerKillBy|victim=Erlking', 'max']),   # cap + bascinet (armor 5) + antler axe
   dict(L(r'("id":\s*"chest",\s*"group":\s*"",\s*"visual":\s*true\s*\},)(?P<old>)', '\n    {\n      "id": "tabard",\n      "group": "",\n      "visual": true\n    },', ['slots'], None), listins=(2, {'id': 'tabard', 'group': '', 'visual': True})),
   L(r'("id":\s*"joan_tabard",\s*"shield".*?"slot":\s*)(?P<old>"chest")', '"tabard"', ['items|id=joan_tabard', 'slot'], 'tabard')]}   # tabard gets its OWN slot: it stacks on the cuirass instead of replacing it (armor 6 + 3 instead of 6 -> 3)
# f3lw (14:19): f3gear WITHOUT the antler axe, merged on top of F4's gearvis. f3lwcore = the edits, staged on the gearvis AFTER file (laneF4\gearvis\after, e9855d48b4); the f3lw kit itself is built by laneLG\f3lw\make_f3lw.py (base = tree gear.json, after = f3lwcore after)
_f3 = KITS['f3gear']['edits']
KITS['f3lwcore'] = {'file': 'gear.json', 'baseMd5': 'e9855d48b4', 'edits': [_f3[2], _f3[3], E(r'("victim":\s*"Erlking",\s*"max":\s*)1(?=\s*\})', '1', '2', ['drop', 'maxPerKillBy|victim=Erlking', 'max']), _f3[5], _f3[6]]}   # bascinet drop (+chance), Erlking max 1->2 (cap + bascinet), tabard slot + item slot
KITS['f3lw'] = {'file': 'gear.json', 'baseMd5': 'ca98899989', 'edits': []}   # apply/revert only; made by f3lw\make_f3lw.py
KITS['f3lw_ui'] = {'file': 'loot_ui.json', 'baseMd5': 'f28ab4016c', 'edits': [   # the Tab equip screen lists loot_ui.json slots (LootScreen / LootSave read LootCfg.slots, NOT gear.json slots): without this row a worn tabard is invisible there and not saved
   dict(L(r'("id":\s*"chest",\s*"label":\s*"Chest",\s*"group":\s*"" \},)(?P<old>)', '\n    { "id": "tabard", "label": "Tabard", "group": "" },', ['slots'], None), listins=(2, {'id': 'tabard', 'label': 'Tabard', 'group': ''}))]}
# F4 uistats REBASED on the lw tree (loot_ui.json cd33e731da = f28ab4016c + f3lw_ui tabard slot). Same four stat rows as laneF4 uistats (80f400a9bb on the old base), so the two never conflict: use THIS one on lw.
_UIS = [('damage', 'Damage', 2, True), ('speed', 'Speed', 2, True), ('reach', 'Reach', 2, True), ('posture', 'Posture', 2, True)]
KITS['f4ui'] = {'file': 'loot_ui.json', 'baseMd5': 'cd33e731da', 'edits': [dict(L(
   r'("stat":\s*"weight",\s*"label":\s*"Weight",\s*"decimals":\s*1,\s*"higherIsBetter":\s*false\s*\})(?P<old>)',
   ''.join(',\n    { "stat": "%s", "label": "%s", "decimals": %d, "higherIsBetter": %s }' % (s, l, d, 'true' if h else 'false') for s, l, d, h in _UIS), ['stats'], None),
   listext=[{'stat': s, 'label': l, 'decimals': d, 'higherIsBetter': h} for s, l, d, h in _UIS])]}
# F5 (first five minutes) kits, bases = lw tree twins. f5menu: the read-only Controls panel is missing three things a new player needs; f5shots: store-page opening frames (QA tooling rows, only read by -zoneshot runs)
_MR = [{'action': 'Pick up / equip loot (after the fight)', 'keys': 'E', 'pad': 'X / A'}, {'action': 'Equipment screen (after the fight)', 'keys': 'Tab', 'pad': 'Start'}, {'action': 'Call / dismount horse (once bought)', 'keys': 'M'}]
KITS['f5menu'] = {'file': 'menu.json', 'baseMd5': '5da8a0510d', 'edits': [dict(L(
   r'("action":\s*"Pause menu",\s*"keys":\s*"Esc",\s*"pad":\s*"Start"\s*\})(?P<old>)',
   ''.join(',\n   {\n    "action": "%s",\n    "keys": "%s"%s\n   }' % (r['action'], r['keys'], (',\n    "pad": "%s"' % r['pad']) if 'pad' in r else '') for r in _MR), ['controls', 'rows'], None), listext=_MR)]}
_SH = {'zone': 'pit_yard', 'stand': 'hearth', 'aimFighters': False, 'sideM': 9.0, 'camY': 1.6, 'lookY': 1.3, 'midBias': 0.0, 'headCloseUp': False, 'closeM': 2.4, 'headY': 1.95}
_FS = [dict(_SH, id='f5_open_t5', settleSeconds=5.0, cam={'spot': 'hearth', 'off': [2.5, 2.4, -4.0]}, look={'spot': 'hearth', 'off': [2.5, 1.4, 8.0]}, fov=50.0),
       dict(_SH, id='f5_open_t20', settleSeconds=20.0, cam={'spot': 'hearth', 'off': [2.5, 2.4, -4.0]}, look={'spot': 'hearth', 'off': [2.5, 1.4, 8.0]}, fov=50.0),
       dict(_SH, id='f5_open_wide', settleSeconds=6.0, cam={'spot': 'hearth', 'off': [2.5, 6.3, -10.5]}, look={'spot': 'hearth', 'off': [2.5, 2.0, 7.0]}, fov=50.0)]
KITS['f5shots'] = {'file': 'zone_shots.json', 'baseMd5': '129b806e38', 'edits': [dict(L(
   r'("shots":\s*\[)(?P<old>)', ''.join('\n  ' + json.dumps(r) + ',' for r in _FS), ['shots'], None), listmulti=(0, _FS))]}
# f1gear: ALTERNATIVE to f1warm (same file, base 0f1d81d0a1). Keeps the cover up until Joan's gear is ON (attach frames 20 -> 100: the saved loadout applies 20 frames after load, then every glb piece instantiates + binds over more frames) with a moderate gear cap (25 -> 8 s) instead of f1warm's 3 s, so a normal launch is not long-black and a normal gear parse is not cut off.
KITS['f1gear'] = {'file': 'startup_warm.json', 'baseMd5': '0f1d81d0a1', 'edits': [
   E(r'("gearCapSeconds":\s*)25\.0(?=,)', '25.0', '8.0', ['cover', 'gearCapSeconds']), E(r'("holdFramesAfter":\s*)24(?=,)', '24', '12', ['cover', 'holdFramesAfter']),
   E(r'("gearAttachFrames":\s*)20(?=\r?\n)', '20', '100', ['cover', 'gearAttachFrames'])]}
# f1bundle = f1gear + the RELOADCOVER v1 data keys (cover.onReload / reloadHoldFrames / reloadCapSeconds / reloadHoldSim); the keys are inert until the code patch laneLG\reloadcover is built (JsonUtility ignores unknown keys)
def _cv(rx_prev, text, key, val): e = I(rx_prev, text, key, val); e['parent'] = ['cover']; return e
KITS['f1bundle'] = {'file': 'startup_warm.json', 'baseMd5': '0f1d81d0a1', 'edits': list(KITS['f1gear']['edits']) + [
   _cv(r'("gearCapSeconds":\s*8\.0,\r?\n)', '  "onReload": true,\n', 'onReload', True),
   _cv(r'("onReload":\s*true,\r?\n)', '  "reloadHoldFrames": 100,\n', 'reloadHoldFrames', 100),
   _cv(r'("reloadHoldFrames":\s*100,\r?\n)', '  "reloadCapSeconds": 4.0,\n', 'reloadCapSeconds', 4.0),
   _cv(r'("reloadCapSeconds":\s*4\.0,\r?\n)', '  "reloadHoldSim": false,\n', 'reloadHoldSim', False)]}
# 16:35 bundle: my rows ON TOP of the laneF5 kit afters (same file, so one merged kit each; built by laneLG\bundle\chain.py, applied with the lw_* names)
KITS['f5menurows'] = dict(KITS['f5menu'], baseMd5='f9e3a880fd')        # my 3 Controls-panel rows on laneF5 f5menu after (menu.json f9e3a880fd)
KITS['f5menubootrows'] = dict(KITS['f5menu'], baseMd5='53d539a30d')    # same rows on laneF5 f5menuboot after (53d539a30d)
KITS['f4uirows'] = dict(KITS['f4ui'], baseMd5='1526dcd16a')            # my Damage/Speed/Reach/Posture stat rows on laneF5 f5loot after (loot_ui.json 1526dcd16a)
KITS['lw_menu'] = {'file': 'menu.json', 'baseMd5': '5da8a0510d', 'edits': []}
KITS['lw_menuboot'] = {'file': 'menu.json', 'baseMd5': '5da8a0510d', 'edits': []}
KITS['lw_loot'] = {'file': 'loot_ui.json', 'baseMd5': 'cd33e731da', 'edits': []}
KITS['ridelj'] = dict(KITS['ride'], baseMd5='7430bdc5df')   # ride seat on top of lj (li + camback): same seat edits, base = mount.json after camback
def md5(b): return hashlib.md5(b).hexdigest()[:10]
def rd(p): return open(p, 'rb').read()
def walk(o, path):
    for k in path:
        if isinstance(k, str) and '|' in k:
            lst, sel = k.split('|'); kk, vv = sel.split('='); o = [x for x in o[lst] if x.get(kk) == vv][0]
        else: o = o[k]
    return o
def setv(o, path, val):
    o = walk(o, path[:-1]); old = o[path[-1]]; o[path[-1]] = val; return old
def build(g):
    s = KITS[g]; f = s['file']; sd = os.environ.get('LGKIT_SRC')   # make-only: LGKIT_SRC = a dir holding the staged-on file (e.g. the lj state), instead of the tree twins
    a, b = (rd(os.path.join(sd, f)),) * 2 if sd else (rd(os.path.join(TWINS[0], f)), rd(os.path.join(TWINS[1], f)))
    if a != b: sys.exit('twins differ: ' + f)
    if '--rebase' not in sys.argv and md5(a) != s['baseMd5']: sys.exit('BASE CHANGED since staging: %s %s (staged on %s)' % (f, md5(a), s['baseMd5']))
    t = a.decode('utf-8'); nt = t; pa = json.loads(t.lstrip('\ufeff'))
    for e in s['edits']:
        ms = list(re.finditer(e['rx'], nt, re.S))
        if len(ms) != 1: sys.exit('EDIT FAILED: %d matches for %s' % (len(ms), e['rx']))
        m = ms[0]
        if 'ins' in e:
            nt = nt[:m.end()] + e['ins'].replace('\n', '\r\n' if '\r\n' in m.group(1) else '\n') + nt[m.end():]; (walk(pa, e['parent']) if e.get('parent') else pa)[e['key']] = e['val']; continue
        if 'lit' in e:   # literal swap of the named group 'old' (lists / strings): path + val assert the JSON result
            nt = nt[:m.start('old')] + e['lit'].replace('\n', '\r\n' if '\r\n' in nt else '\n') + nt[m.end('old'):]
            if e.get('listext') is not None: walk(pa, e['path']).extend(e['listext'])   # append objects to the list
            elif e.get('listmulti') is not None:   # insert several objects at an index, in order
                for qi, qo in enumerate(e['listmulti'][1]): walk(pa, e['path']).insert(e['listmulti'][0] + qi, qo)
            elif e.get('listins') is not None: walk(pa, e['path']).insert(e['listins'][0], e['listins'][1])   # list insert: (index, object)
            else: setv(pa, e['path'], e['val'])
            continue
        nt = nt[:m.start()] + m.group(1) + e['new'] + nt[m.end():]
        if e.get('bool'):
            old = setv(pa, e['path'], e['new'] == 'true'); assert old == (e['old'] == 'true'), (e['path'], old); continue   # boolean literal swap true<->false
        old = setv(pa, e['path'], float(e['new'])); assert abs(old - float(e['old'])) < 1e-9, (e['path'], old)
    pb = json.loads(nt.lstrip('\ufeff')); assert pa == pb, 'after != base + the listed values'
    return a, nt.encode('utf-8')
def guard(d):
    bd = os.path.normcase(os.path.abspath(d)); names = set()
    for p in glob.glob(os.path.join(REPO, 'Builds', 'PLAY*.bat')): names |= set(re.findall(r'%~dp0(HDRP_[^\\"]+)\\', open(p, errors='ignore').read()))
    for n in names:
        if os.path.normcase(os.path.join(REPO, 'Builds', n)) in bd: sys.exit('REFUSED: %s is inside %s, named by a PLAY*.bat launcher (live/rollback). Use a QA copy.' % (d, n))
    if os.path.normcase(os.path.join(REPO, 'Builds', 'Play')) in bd: sys.exit('REFUSED: Builds\\Play')
def main():
    if len(sys.argv) < 3 or sys.argv[1] not in KITS: print(__doc__); return
    g, cmd = sys.argv[1], sys.argv[2]; f = KITS[g]['file']; GD = os.path.join(HERE, g); go = '--go' in sys.argv
    if cmd == 'make':
        base, after = build(g); os.makedirs(os.path.join(GD, 'base'), exist_ok=True); os.makedirs(os.path.join(GD, 'after'), exist_ok=True)
        open(os.path.join(GD, 'base', f), 'wb').write(base); open(os.path.join(GD, 'after', f), 'wb').write(after)
        json.dump({'file': f, 'base': md5(base), 'after': md5(after)}, open(os.path.join(GD, 'MD5.json'), 'w'), indent=1)
        print(g, f, 'md5', md5(base), '->', md5(after)); return
    if cmd not in ('apply', 'revert'): print(__doc__); return
    man = json.load(open(os.path.join(GD, 'MD5.json'))); rev = cmd == 'revert'; tg = sys.argv[sys.argv.index('--target') + 1] if '--target' in sys.argv else None
    dirs = [tg] if tg else TWINS
    for d in dirs: guard(d)
    src, want = ('base', 'after') if rev else ('after', 'base')
    assert md5(rd(os.path.join(GD, 'base', f))) == man['base'] and md5(rd(os.path.join(GD, 'after', f))) == man['after'], 'kit files changed after make'
    for d in dirs:
        cur = md5(rd(os.path.join(d, f))); ok = cur == man[want]
        print('%-7s %s %-18s now %s (expect %s %s) -> %s %s' % ('OK' if ok else ('ALREADY' if cur == man[src] else 'BAD'), os.path.basename(d.rstrip('\\')), f, cur, want, man[want], src, man[src]))
        if not ok and cur != man[src]: sys.exit('REFUSED: %s in %s is neither the kit base nor its after' % (f, d))
    if not go: print('DRY RUN (%s %s): nothing written. Add --go.' % (g, cmd.upper())); return
    stamp = time.strftime('%Y%m%d_%H%M%S'); bk = os.path.join(GD, 'backup', stamp)
    for i, d in enumerate(dirs):
        os.makedirs(os.path.join(bk, str(i)), exist_ok=True); shutil.copy2(os.path.join(d, f), os.path.join(bk, str(i), f)); shutil.copyfile(os.path.join(GD, src, f), os.path.join(d, f))
    open(os.path.join(GD, 'STATE.txt'), 'a', encoding='utf-8').write('%s %s %s dirs=%s backup=%s\n' % (stamp, g, cmd.upper(), dirs, bk))
    print('WROTE %s into %s; backup %s' % (src, dirs, bk))
main()

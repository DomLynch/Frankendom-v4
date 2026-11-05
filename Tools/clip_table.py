"""CLIPBUILD clip_table.py: turns <Id>_gates.json (written by clip_gates.py) into a before/after markdown table (live = current project glb, new = staged rebuild).
  python clip_table.py out\\Joan_gates.json > out\\BEFORE_AFTER.md"""
import json, sys

g = json.load(open(sys.argv[1], encoding='utf-8')); rows = {r['state']: r for r in g['rows']}
f = lambda v, n=1: '-' if v is None else ('%.' + str(n) + 'f') % v
print('# %s: base clips, live vs rebuilt from raw Mixamo (offline FK, numpy)\n' % g['character'])
print('Failed gates: %d%s\n' % (len(g['failed']), '' if not g['failed'] else ' (' + '; '.join(g['failed'][:12]) + (' ...' if len(g['failed']) > 12 else '') + ')'))
print('## A. Rotation difference from the RAW Mixamo source (degrees, mean over bones/groups; 0 = identical rotations)\n')
print('| clip | live body mean | live arms | live forearms | live UpLeg | live fingers | new body mean | new fingers |\n|---|---|---|---|---|---|---|---|')
for s, r in rows.items():
    p, q = r['pose'], r['poseLive']
    if not p: continue
    gq = (q or {}).get('groups', {})
    print('| %s | %s | %s | %s | %s | %s | %s | %s |' % (s, f(q and q['mean']), f(gq.get('arms')), f(gq.get('forearms')), f(gq.get('UpLeg')), f(q and q['finger']), f(p['mean'], 2), f(p['finger'])))
print('\n## B. Stance, legs and neck (frame 0 toe yaw: 0 straight, L+ / R- = toes out)\n')
print('| clip | toe yaw L/R live | toe yaw L/R new | knee inward m live | new | arm-torso gap m live (L/R) | new (L/R) | head ext deg | sword-head clearance m live | new |\n|---|---|---|---|---|---|---|---|---|---|')
for s, r in rows.items():
    a, b = r['live'], r['new']
    print('| %s | %s / %s | %s / %s | %s | %s | %s / %s | %s / %s | %s | %s | %s |' % (s, f(a['toeL0']), f(a['toeR0']), f(b['toeL0']), f(b['toeR0']), f(a['knee'], 3), f(b['knee'], 3), f(a['armL'], 3), f(a['armR'], 3), f(b['armL'], 3), f(b['armR'], 3), f(b['head']), f(a['sword'], 2), f(b['sword'], 2)))
print('\n## C. Leg fix on the rebuilt clips\n')
print('| clip | leg fix | max swivel deg |\n|---|---|---|')
for s, r in rows.items():
    if r['legFix'] == 'applied': print('| %s | applied | %s |' % (s, f(r['swivel'])))
print('\n## D. 106-joint rig audit\n')
print('| file | joints | 106? |\n|---|---|---|')
for x in g.get('rig', []): print('| %s | %s | %s |' % (x['file'].replace('\\', '/').split('unity_test_hdrp/')[-1], x['kind'] if not x['kind'].startswith('skinned') else str(x['joints']) + ' (skinned)', 'yes' if x['is106'] else 'NO'))

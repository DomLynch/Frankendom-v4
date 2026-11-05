import json
R=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets"
for d in ("Data","StreamingAssets"):
    P=R+"\\"+d+"\\gear.json"; j=json.load(open(P,encoding='utf-8-sig'))
    ids={i['id'] for i in j['items']}
    if 'pitborn_chest' not in ids:
        j['items'].append({"id":"pitborn_chest","name":"Pitborn cuirass","slot":"chest","glb":"Gear/pitborn_chest.glb","resource":"","part":"","coversZones":[],"hidesLayers":[],"stats":{"armor":10,"poise":8,"weight":6},"dropFrom":["Pitborn"],"dropChance":1.0,"notes":"v87 PLACEHOLDER (Dom): Pitborn's chest piece drop. The glb does not exist yet, the art comes later: a missing glb = stats only, no visual, no exception. Drops with the pauldrons and the maul on the first win."})
    if 'pitborn_maul' not in ids:
        j['items'].append({"id":"pitborn_maul","name":"Pitborn maul","slot":"mainHand","glb":"","resource":"","part":"","coversZones":[],"hidesLayers":[],"stats":{"armor":0,"poise":0,"weight":9},"dropFrom":["Pitborn"],"dropChance":1.0,"notes":"v87 (Dom): Pitborn's own weapon as a main-hand drop. mainHand is a data-only slot (visual false): equipping records the item and applies its stats; swapping Joan's sword mesh for the maul on WeaponSocket_R needs a small weapon-swap component (v88)."})
    j['drop']['maxPerKill']=3
    open(P,'w',encoding='utf-8',newline='').write(json.dumps(j,indent=2)); print(d,'gear.json: items',len(j['items']),'maxPerKill',j['drop']['maxPerKill'])

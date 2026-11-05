import json
R=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets"
for d in ("Data","StreamingAssets"):
    P=R+"\\"+d+"\\joan_swap.json"; j=json.load(open(P,encoding='utf-8-sig'))
    j['sourceGlb']="C:/Users/dom12/Documents/kits_out/3d/joan/assembled/joan_v6_mixamo_dark.glb"
    j['materials']=[m for m in j['materials'] if m['glbMaterial']!='Ch13_hair']
    if not any(m['glbMaterial']=='tripo_material' for m in j['materials']):
        j['materials'].append({"glbMaterial":"tripo_material","mat":"Assets/Characters/Joan/Mats/joan_head_0.mat","tex":"joan_head","smoothness":0.3})
    open(P,'w',encoding='utf-8',newline='').write(json.dumps(j,indent=2)); print(d,'joan_swap -> v6 rows')
    P=R+"\\"+d+"\\hdrp_char_mats.json"; m=json.load(open(P,encoding='utf-8-sig'))
    for e in m.get('extract',[]):
        if 'Joan' in e.get('glb',''):
            for g in e['groups']:
                if g['glbMaterial']=='Ch13_hair': g.update(glbMaterial='tripo_material',maskOut='joan_head_mask.png',parts=[{"base":"joan_head_base.jpg","normal":"joan_head_normal.jpg","mr":"joan_head_mr.jpg"}])
    open(P,'w',encoding='utf-8',newline='').write(json.dumps(m,indent=2)); print(d,'hdrp_char_mats joan group -> head')

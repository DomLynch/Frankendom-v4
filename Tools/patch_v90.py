import json,re
R=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets"
# V51: DodgeClipSetup.Run() so Joan's dodge states use Joan's own leg-fixed Joan_dodges.glb clips (they pointed at Dracula's DodgeClips .anim)
P=R+r"\Editor\HdrpBatch.cs"; b=open(P,'rb').read(); s=b.decode('utf-8')
if 'DodgeClipSetup.Run()' not in s:
    a='AttackClipSetup.Run(); HdrpGltfMats.Refresh();'
    assert s.count(a)==1; s=s.replace(a,'AttackClipSetup.Run(); DodgeClipSetup.Run(); HdrpGltfMats.Refresh();'); open(P,'wb').write(s.encode('utf-8')); print('V51 +DodgeClipSetup')
for d in ("Data","StreamingAssets"):
    P=R+"\\"+d+"\\finishers.json"; t=open(P,encoding='utf-8').read()
    new='C:/Users/dom12/Documents/unity_test_hdrp/Tools/finisher_src_legfix'
    n=re.sub(r'("sourceDir":\s*")[^"]*(")',lambda m:m.group(1)+new+m.group(2),t,count=1); assert n!=t or new in t
    open(P,'w',encoding='utf-8',newline='').write(n); json.loads(n.lstrip('\ufeff')); print(d,'finishers sourceDir ->',new)
    P=R+"\\"+d+"\\hit_feel.json"; t=open(P,encoding='utf-8').read(); n=t
    rep=[('"countBase": 14, "countPerStrength": 14, "countRandom": 0.25, "countScaleHeavy": 1.3','"countBase": 7, "countPerStrength": 6, "countRandom": 0.25, "countScaleHeavy": 1.15'),
         ('"intensity": 8.0, "glintIntensity": 8.0, "kelvin": 2200, "coolKelvin": 1700, "coolAt": 0.55, "glintWhiteMix": 0.55','"intensity": 4.0, "glintIntensity": 2.0, "kelvin": 1800, "coolKelvin": 1300, "coolAt": 0.35, "glintWhiteMix": 0.1'),
         ('"sideSpread": 2.4, "upMin": 0.15, "upRange": 1.4','"sideSpread": 0.9, "upMin": 0.1, "upRange": 0.8'),
         ('"lifeMin": 0.18, "lifeRange": 0.27','"lifeMin": 0.09, "lifeRange": 0.11'),
         ('"sizeMin": 0.03, "sizeRange": 0.03, "lengthScale": 2.2','"sizeMin": 0.012, "sizeRange": 0.010, "lengthScale": 1.0'),
         ('"glintLife": 0.07, "glintSize": 0.3, "glintSizePerStrength": 0.25','"glintLife": 0.04, "glintSize": 0.10, "glintSizePerStrength": 0.08'),
         ('"seconds": 0.05, "lumen": 9000','"seconds": 0.04, "lumen": 4500')]
    for a,b2 in rep:
        if a in n: n=n.replace(a,b2)
        else: print('  MISSING',a[:50])
    open(P,'w',encoding='utf-8',newline='').write(n); json.loads(n.lstrip('\ufeff')); print(d,'hit_feel sparks toned down')
for d in ("Data","StreamingAssets"):
    P=R+"\\"+d+"\\loot_ui.json"; t=open(P,encoding='utf-8').read(); n=t
    for a,b2 in (('"intensity": 0.7,','"intensity": 0.4,'),('"groundIntensity": 0.6,','"groundIntensity": 0.35,'),('"alpha": 0.9,','"alpha": 0.55,')):
        if a in n: n=n.replace(a,b2,1)
        else: print('  MISSING',a)
    open(P,'w',encoding='utf-8',newline='').write(n); json.loads(n.lstrip('\ufeff')); print(d,'loot pillar darker / lower alpha')

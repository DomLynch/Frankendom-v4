import json,re
R=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets"
P=R+r"\Scripts\Combat\HitFeel.cs"; b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b; s=b.decode('utf-8-sig').replace('\r\n','\n')
if 'suppressInFinisher' not in s:
    def rep(a,n):
        global s
        assert s.count(a)==1,(a[:70],s.count(a)); s=s.replace(a,n)
    rep('public int countBase = 14, countPerStrength = 14;','public int suppressInFinisher = 1; public int countBase = 14, countPerStrength = 14;   // v89: 1 = no spark burst / flash while a finisher owns the clock (Goose: spark storm in finishers)')
    rep('        RequestStop(e, dmg);\n\n        bool metal = false;','        RequestStop(e, dmg);\n        if (cfg.sparks != null && cfg.sparks.suppressInFinisher != 0 && RTFinisher.Busy) return;   // v89: no sparks / flash during a finisher\n\n        bool metal = false;')
    s=s.replace('\n','\r\n') if crlf else s; o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o; open(P,'wb').write(o); print('HitFeel patched')
for d in ("Data","StreamingAssets"):
    P=R+"\\"+d+"\\hit_feel.json"; t=open(P,encoding='utf-8').read()
    if 'suppressInFinisher' not in t:
        t=t.replace('"poolSize": 320,','"suppressInFinisher": 1, "poolSize": 320,',1); open(P,'w',encoding='utf-8',newline='').write(t)
    json.loads(t.lstrip('\ufeff')); print(d,'hit_feel ok')
    P=R+"\\"+d+"\\hud.json"; t=open(P,encoding='utf-8').read(); n=t.replace('"victoryTint": [0.02, 0.015, 0.0, 1.0]','"victoryTint": [0.0, 0.0, 0.0, 1.0]'); open(P,'w',encoding='utf-8',newline='').write(n); json.loads(n.lstrip('\ufeff')); print(d,'victoryTint neutral:', n!=t)


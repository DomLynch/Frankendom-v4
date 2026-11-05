import re,json
R=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets"
for d in ("Data","StreamingAssets"):
    P=R+"\\"+d+"\\finishers.json"; raw=open(P,'rb').read(); s=raw.decode('utf-8'); bom=s.startswith('\ufeff'); 
    i=s.index('"id": "decapitation"') if '"id": "decapitation"' in s else s.index('"id":  "decapitation"')
    j=s.find('"id"',i+30)
    # entry bound: next entry id after the shotDefs ids; find '"attackerMove"' of next entry instead
    k=s.find('"enabled"',i+300)
    seg_end=s.find('"distanceScaleByKiller"',i)+60
    seg=s[i:seg_end]
    n=seg
    n2,c1=re.subn(r'("distance":\s*)1\.1\b',r'\g<1>1.16',n,count=1)
    n3,c2=re.subn(r'("pairOffset":\s*\[\s*0\.0,\s*0\.0,\s*)1\.1\b',r'\g<1>1.16',n2,count=1)
    print(d,c1,c2)
    if c1 and c2:
        s=s[:i]+n3+s[seg_end:]; open(P,'wb').write(s.encode('utf-8'))
    # validate
    json.loads(s.lstrip('\ufeff'))
for d in ("Data","StreamingAssets"):
    P=R+"\\"+d+"\\controls.json"; t=open(P,encoding='utf-8').read(); n=re.sub(r'"sideReadChance":\s*0\.55','"sideReadChance": 0.5',t); print(d,'side',n!=t); open(P,'w',encoding='utf-8',newline='').write(n)

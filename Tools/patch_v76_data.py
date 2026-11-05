import re,json
R=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets"
for d in ("Data","StreamingAssets"):
    P=R+"\\"+d+"\\hit_stop.json"; t=open(P,encoding='utf-8').read()
    if '"heavy_left"' not in t:
        t=t.replace('    { "id": "heavy_overhead",   "frames": 0, "ms": 80 },','    { "id": "heavy_overhead",   "frames": 0, "ms": 80 },\n    { "id": "heavy_left",       "frames": 0, "ms": 80 },\n    { "id": "heavy_right",      "frames": 0, "ms": 80 },\n    { "id": "heavy_thrust",     "frames": 0, "ms": 80 },\n    { "id": "light_overhead",   "frames": 0, "ms": 50 },',1)
        open(P,'w',encoding='utf-8',newline='').write(t)
    json.loads(t); print(d,'hit_stop ok','heavy_left' in t)
    P=R+"\\"+d+"\\controls.json"; t=open(P,encoding='utf-8').read(); n=re.sub(r'"sideReadChance":\s*0\.55\b','"sideReadChance": 0.525',t); open(P,'w',encoding='utf-8',newline='').write(n); json.loads(n); print(d,'side',n!=t)

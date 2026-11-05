import json
R=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets"
P=R+r"\Data\attack_clips.json"; j=json.load(open(P,encoding="utf-8-sig"))
yaw={"yawAbsolute":True,"footYawMaxDeg":35.0,"hipsYawMaxDeg":22.0,"chestYawMaxDeg":28.0}
for b in j["bake"]:
    for c in b["clips"]:
        if c["state"] in ("atk_cut_out","atk_cut_in"): c["posture"].update(yaw)
for r in j["rows"]:
    if r.get("clip")=="atk_cut_out" and r["move"] in ("light_right","heavy_right"): r["contactTime"]=0.85
    if r.get("clip")=="atk_cut_in" and r["move"] in ("light_left","heavy_left"): r["contactTime"]=1.32
txt=json.dumps(j,indent=2,ensure_ascii=False)
for d in ("Data","StreamingAssets"): open(R+"\\"+d+"\\attack_clips.json","w",encoding="utf-8",newline="").write(txt)
print("ok")

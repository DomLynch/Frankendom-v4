import json, os, shutil, re
R=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets"
# --- move-defs.json: clone rows (one move per line)
for d in ("Data","StreamingAssets"):
    P=os.path.join(R,d,"move-defs.json"); raw=open(P,encoding="utf-8-sig").read()
    if '"id":"heavy_left","direction"' in raw.replace(' ',''): print("move-defs ALREADY",d); continue
    lines=raw.split("\n"); out=[]; base={}
    for ln in lines:
        m=re.search(r'"id":"(\w+)"',ln)
        if m and ln.strip().startswith("{"): base[m.group(1)]=json.loads(ln.strip().rstrip(","))
    def clone(src,new,**kw):
        r=json.loads(json.dumps(base[src])); r["id"]=new; r.update(kw); return "    "+json.dumps(r,separators=(",",":"))+","
    add=[clone("heavy_overhead","heavy_left",direction="left",chainPath=None,chained=None,chain=None),
         clone("heavy_overhead","heavy_right",direction="right",chainPath=None,chained=None,chain=None),
         clone("heavy_overhead","heavy_thrust",direction="thrust",chainPath=None,chained=None,chain=None),
         clone("light_right","light_overhead",direction="overhead",path="heavy_overhead",chainPath=None,chained=None,chain=None)]
    for ln in lines:
        out.append(ln)
        if re.search(r'"id":"heavy_overhead"',ln) and ln.strip().startswith("{"): out.extend(add)
    open(P,"w",encoding="utf-8",newline="").write("\n".join(out)); print("move-defs patched",d)
# --- attack_clips.json
src=os.path.join(R,"Data","attack_clips.json"); j=json.load(open(src,encoding="utf-8-sig"))
if any(r.get("clip")=="atk_cut_out" for r in j["rows"]): print("clips ALREADY")
else:
    def row(move,clip,st,ct,en,hold=0.0,step=0.3,note=""):
        return {"character":"Dracula","move":move,"enabled":True,"clip":clip,"startTime":st,"contactTime":ct,"endTime":en,"holdClipTime":hold,
                "fallbackClip":"light_attack_R","fbStartTime":0.0,"fbContactTime":0.27,"fbEndTime":1.1,"fbHoldClipTime":0.0,
                "windupSpeed":0.0,"recoverySpeed":0.0,"blendIn":0.04,"blendOut":0.12,"rootMotion":"inplace","maxStep":0.5,"useStepIn":True,"stepIn":step,
                "source":"V3 pick (A2 report)","note":note}
    for r in j["rows"]:
        if r.get("character")=="Dracula" and r.get("move")=="light_right": r.update(clip="atk_cut_out",startTime=0.45,contactTime=0.90,endTime=1.5,holdClipTime=0.0,source="104990902 Stable Sword Outward Slash",note="V3 light_right: hand R-to-L at chest height")
        if r.get("character")=="Dracula" and r.get("move")=="light_left":
            r.update(clip="atk_cut_in",startTime=0.80,contactTime=1.28,endTime=1.7,holdClipTime=0.0,source="104990901 Stable Sword Inward Slash",note="L-to-R; start 0.8 skips the early R-to-L pass")
            r["fallbackClip"]="light_attack_L"; r["fbStartTime"]=0.55; r["fbContactTime"]=0.93; r["fbEndTime"]=1.7
    new=[row("heavy_right","atk_cut_out",0.45,0.90,1.5,0.75,0.4,"same clip as light_right, parked at the cocked-right pose while held (V3)"),
         row("heavy_left","atk_cut_in",0.80,1.28,1.7,1.10,0.4,"same clip as light_left, parked while held (V3)"),
         row("heavy_thrust","atk_thrust",0.10,0.40,0.95,0.30,0.7,"atk_thrust with a hold (hand pulled back at 0.3 s)"),
         row("light_overhead","atk_heavy",0.35,0.72,1.0,0.0,0.4,"atk_heavy played faster, no hold")]
    for n in new:
        if n["move"]=="heavy_thrust": n["fallbackClip"]="thrust"
        if n["move"]=="light_overhead": n["fallbackClip"]="heavy_release"
        j["rows"].append(n)
    for b in j["bake"]:
        if b["character"]=="Dracula":
            b["clips"] += [
              {"state":"atk_cut_out","source":"C:/Users/dom12/Documents/kits_out/3d/combat_clips_unity/clips_flagged_excluded/104990902-stable-sword-outward-slash.glb","rootMotion":"inplace","maxStep":0.0,"posture":{"ankleLiftCapM":0.03,"maxStrideM":0.35,"contactHandY":1.35}},
              {"state":"atk_cut_in","source":"C:/Users/dom12/Documents/kits_out/3d/combat_clips_unity/clips/104990901-stable-sword-inward-slash.glb","rootMotion":"inplace","maxStep":0.0,"posture":{"ankleLiftCapM":0.03,"maxStrideM":0.35,"contactHandY":1.35}}]
    txt=json.dumps(j,indent=2,ensure_ascii=False)
    for d in ("Data","StreamingAssets"): open(os.path.join(R,d,"attack_clips.json"),"w",encoding="utf-8",newline="").write(txt)
    print("attack_clips patched")


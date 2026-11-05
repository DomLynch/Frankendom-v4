import json,re,os
R=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets"
# --- JoanSwap.cs
P=R+r"\Editor\JoanSwap.cs"; b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b; s=b.decode('utf-8-sig').replace('\r\n','\n')
if 'v77' not in s:
    def rep(a,n):
        global s
        assert s.count(a)==1,(a[:70],s.count(a)); s=s.replace(a,n)
    rep('public class MatRow { public string glbMaterial = "", mat = "", tex = ""; public float smoothness = 0.25f; }','public class MatRow { public string glbMaterial = "", mat = "", tex = "", ext = "jpg"; public float smoothness = 0.25f, alphaClip = 0f; public bool doubleSided = false; }   // v77: ext = base texture extension (png keeps alpha), alphaClip > 0 = alpha-tested cutout (hair cards), doubleSided')
    rep('string b = dir + "/Tex/" + row.tex + "_base.jpg", nrm','string b = dir + "/Tex/" + row.tex + "_base." + (string.IsNullOrEmpty(row.ext) ? "jpg" : row.ext), nrm')
    rep('        m.SetFloat("_Smoothness", row.smoothness); m.SetFloat("_Metallic", 0f);\n','''        m.SetFloat("_Smoothness", row.smoothness); m.SetFloat("_Metallic", 0f);
        if (row.alphaClip > 0f) { m.SetFloat("_AlphaCutoffEnable", 1f); m.SetFloat("_AlphaCutoff", row.alphaClip); m.EnableKeyword("_ALPHATEST_ON"); m.SetOverrideTag("RenderType", "TransparentCutout"); m.renderQueue = 2450; }   // v77: hair cards are alpha-tested, not blended
        if (row.doubleSided) { m.SetFloat("_DoubleSidedEnable", 1f); m.SetFloat("_CullMode", 0f); m.SetFloat("_CullModeForward", 0f); m.EnableKeyword("_DOUBLESIDED_ON"); }
''')
    rep('        inst.transform.localScale = Vector3.one * s;\n','''        inst.transform.localScale = Vector3.one * s;
        // v77: the pre-scale measure (1.762 m) disagreed with the post-scale one (1.417 m at heightM 1.58): re-measure and correct, up to 3 passes
        for (int pass = 0; pass < 3 && jr.heightM > 0f; pass++) { float hh = WorldHeight(inst); if (hh < 0.1f || Mathf.Abs(hh - jr.heightM) < 0.004f) break; s *= jr.heightM / hh; inst.transform.localScale = Vector3.one * s; }
''')
    s=s.replace('\n','\r\n') if crlf else s; o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o; open(P,'wb').write(o); print('JoanSwap patched')
else: print('JoanSwap ALREADY')
# --- joan_swap.json rows (Data + StreamingAssets)
for d in ("Data","StreamingAssets"):
    P=R+"\\"+d+"\\joan_swap.json"
    if not os.path.exists(P): continue
    j=json.load(open(P,encoding='utf-8-sig'))
    for m in j['materials']:
        if m['glbMaterial']=='Ch13_hair': m.update(ext='png',alphaClip=0.4,doubleSided=True,smoothness=0.15)
    open(P,'w',encoding='utf-8',newline='').write(json.dumps(j,indent=2)); print(d,'joan_swap rows')

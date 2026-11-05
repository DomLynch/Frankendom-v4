import json,re
R=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets"
# JoanSwap.cs: tint field + update existing materials
P=R+r"\Editor\JoanSwap.cs"; b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b; s=b.decode('utf-8-sig').replace('\r\n','\n')
if 'v84 tint' not in s:
    def rep(a,n):
        global s
        assert s.count(a)==1,(a[:70],s.count(a)); s=s.replace(a,n)
    rep('public float smoothness = 0.25f, alphaClip = 0f; public bool doubleSided = false; }','public float smoothness = 0.25f, alphaClip = 0f; public float[] tint = null; public bool doubleSided = false; }   // v84 tint: optional _BaseColor multiplier (de-red the Tripo head)')
    rep('        var m = AssetDatabase.LoadAssetAtPath<Material>(row.mat);\n        if (m != null) return m;','        var m = AssetDatabase.LoadAssetAtPath<Material>(row.mat);\n        if (m != null) { m.SetFloat("_Smoothness", row.smoothness); if (row.tint != null && row.tint.Length >= 3) m.SetColor("_BaseColor", new Color(row.tint[0], row.tint[1], row.tint[2], 1f)); EditorUtility.SetDirty(m); AssetDatabase.SaveAssets(); return m; }   // v84: an existing material takes the row values again')
    rep('        m.SetFloat("_Smoothness", row.smoothness); m.SetFloat("_Metallic", 0f);\n','        m.SetFloat("_Smoothness", row.smoothness); m.SetFloat("_Metallic", 0f);\n        if (row.tint != null && row.tint.Length >= 3) m.SetColor("_BaseColor", new Color(row.tint[0], row.tint[1], row.tint[2], 1f));\n')
    s=s.replace('\n','\r\n') if crlf else s; o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o; open(P,'wb').write(o); print('JoanSwap patched')
else: print('JoanSwap ALREADY')
for d in ("Data","StreamingAssets"):
    P=R+"\\"+d+"\\joan_swap.json"; j=json.load(open(P,encoding='utf-8-sig'))
    for m in j['materials']:
        if m['glbMaterial']=='tripo_material': m.update(smoothness=0.25,tint=[0.93,1.0,1.03])
    open(P,'w',encoding='utf-8',newline='').write(json.dumps(j,indent=2)); print(d,'head row')
    P=R+"\\"+d+"\\hdrp_furnace.json"; t=open(P,encoding='utf-8').read(); n=t
    for name in ('Furnace_RectInterior','Furnace_ThresholdFill'):
        i=n.index('"name":  "%s"'%name); j2=n.index('"volumetricDimmer":  2.0',i); assert j2-i<1500,(name,j2-i)
        n=n[:j2]+'"volumetricDimmer":  0.5'+n[j2+len('"volumetricDimmer":  2.0'):]
    open(P,'w',encoding='utf-8',newline='').write(n); json.loads(n.lstrip('\ufeff')); print(d,'furnace dimmers 0.5')

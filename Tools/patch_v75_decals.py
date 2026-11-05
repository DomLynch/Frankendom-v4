import sys
P=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\Combat\WoundDecals.cs"
b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b
s=b.decode('utf-8-sig').replace('\r\n','\n')
if 'v75 leak fix' in s: print('ALREADY'); sys.exit(0)
def rep(a,n):
    global s
    assert s.count(a)==1,(a[:70],s.count(a)); s=s.replace(a,n)
# 1. destroyed cached materials must not be reused
rep('string key = kind + "#" + variant; for (int i = 0; i < decalMats.Count; i++) if (decalMats[i].name == key) return decalMats[i];',
    'string key = kind + "#" + variant; for (int i = decalMats.Count - 1; i >= 0; i--) { if (decalMats[i] == null) { decalMats.RemoveAt(i); continue; } if (decalMats[i].name == key) return decalMats[i]; }   // v75 leak fix: a destroyed material is dropped, never handed to a projector')
rep('var m = new Material(decalShader) { name = key };','var m = new Material(decalShader) { name = key, hideFlags = HideFlags.DontUnloadUnusedAsset };')
# 2. create the projector on an INACTIVE object: HDRP registers a decal with a null material on AddComponent (OnEnable), which later made DecalSet.RenderIntoDBuffer throw every frame (command buffers leaked, 40 GB, access violation)
rep('var go = new GameObject("WoundDecal_" + kind);','var go = new GameObject("WoundDecal_" + kind); go.SetActive(false);   // v75 leak fix: inactive until the projector has its material')
rep('        s.list.Add(d); return d;\n    }','        go.SetActive(true);\n        s.list.Add(d); return d;\n    }')
s=s.replace('\n','\r\n') if crlf else s
o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o
open(P,'wb').write(o); print('patched')

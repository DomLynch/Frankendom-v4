P=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\VerifyGround.cs"
b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b; s=b.decode('utf-8-sig').replace('\r\n','\n')
if 'v77 head fallback' in s: print('ALREADY'); raise SystemExit
a="        bool hrOn = hr != null && hr.enabled && hr.gameObject.activeInHierarchy;\n        float gap = neckBone"
assert s.count(a)==1
s=s.replace(a,"        if (hr == null)   // v77 head fallback: a character whose head is part of the body mesh (Joan, chef rig) has no renderer named like the head: use its first skinned body mesh\n            foreach (var r in drac.GetComponentsInChildren<SkinnedMeshRenderer>(true)) { if (InsideNamed(r.transform, HC.metaHumanName) || r.name.IndexOf(\"metahuman\", StringComparison.OrdinalIgnoreCase) >= 0) continue; if (r.enabled && r.gameObject.activeInHierarchy) { hr = r; break; } }\n"+a)
s=s.replace('\n','\r\n') if crlf else s; o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o; open(P,'wb').write(o); print('patched')

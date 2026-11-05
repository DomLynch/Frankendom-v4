P=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\VerifyGround.cs"
b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b; s=b.decode('utf-8-sig').replace('\r\n','\n')
if 'v85 alive only' in s: print('ALREADY'); raise SystemExit
a="                    Vector3 cc = Vector3.zero; int cn = 0; foreach (var ff in fighters) if (ff && ff.gameObject.activeInHierarchy) { cc += ff.transform.position; cn++; }\n                    if (cn > 0) {"
assert s.count(a)==1
n="                    Vector3 cc = Vector3.zero; int cn = 0; bool anyDead = false; foreach (var ff in fighters) if (ff && ff.gameObject.activeInHierarchy) { cc += ff.transform.position; cn++; if (ff.Dead) anyDead = true; }   // v85 alive only: after a death / the victory screen the camera frames the corpse on purpose (a 60-s fight left 72% of a 150-s window 'out of view')\n                    if (cn > 0 && !anyDead && GameLoop.Outcome == GameLoop.Result.None) {"
s=s.replace(a,n)
s=s.replace('\n','\r\n') if crlf else s; o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o; open(P,'wb').write(o); print('patched')

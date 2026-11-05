P=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets\Editor\JoanSwap.cs"
b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b; s=b.decode('utf-8-sig').replace('\r\n','\n')
if 'v79 idempotent' in s: print('ALREADY'); raise SystemExit
def rep(a,n):
    global s
    assert s.count(a)==1,(a[:70],s.count(a)); s=s.replace(a,n)
rep('var old = FindRoot(sc, jr.objectName); if (old != null && old != tRoot) Object.DestroyImmediate(old);   // rebuild is idempotent',
    'var old = FindRoot(sc, jr.objectName); if (old != null && old == tRoot) old = null; if (old != null) old.name = jr.objectName + "_old";   // v79 idempotent: the previous Joan is kept until every scene reference to it has been re-pointed to the new one (v77 shipped a dead camera because the old object was destroyed first)')
rep('        Debug.Log("JOANSWAP built \'"','        if (old != null) { int rp = Repoint(old, root, sc); Object.DestroyImmediate(old); Debug.Log("JOANSWAP idempotent rebuild: " + rp + " scene references re-pointed from the previous " + jr.objectName + " object to the new one"); }\n        Debug.Log("JOANSWAP built \'"')
s=s.replace('\n','\r\n') if crlf else s; o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o; open(P,'wb').write(o); print('patched')

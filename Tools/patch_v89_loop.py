P=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\LoopTest.cs"
b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b; s=b.decode('utf-8-sig').replace('\r\n','\n')
a='Check((P.Dead && !D.Dead) ? res == GameLoop.Result.Victory : (D.Dead ? res == GameLoop.Result.Defeat : true), tag + "outcome " + resName + " does not match who died");'
assert s.count(a)==1
n='bool pDown = P.Dead || P.health <= 0.01f, dDown = D.Dead || D.health <= 0.01f;   // v89: a finisher defers the Dead flag, health 0 is the truth\n            Check((pDown && !dDown) ? res == GameLoop.Result.Victory : (dDown ? res == GameLoop.Result.Defeat : true), tag + "outcome " + resName + " does not match who died (P down " + pDown + ", D down " + dDown + ")");'
s=s.replace(a,n)
s=s.replace('\n','\r\n') if crlf else s; o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o; open(P,'wb').write(o); print('patched')

import sys
def patch(P,reps):
    b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b
    s=b.decode('utf-8-sig').replace('\r\n','\n')
    for a,n in reps:
        if n in s: print('ALREADY',P); return
        assert s.count(a)>=1,(P,a[:70],s.count(a)); s=s.replace(a,n)
    s=s.replace('\n','\r\n') if crlf else s
    o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o
    open(P,'wb').write(o); print('patched',P)
C=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\Combat"
patch(C+r"\WoundDecals.cs",[
("        float roll = shape.baseRollDeg;\n        if (kind == \"slash\")",
"""        // v69 (B1 tier a): put the mark on the victim's real surface, not on a guessed body radius: ray along the swing against the victim's colliders; no hit = the old guessed point
        bool onSurface = false;
        { var cols = victim.GetComponentsInChildren<Collider>(); float best = float.MaxValue; var ray = new Ray(point - a2v * 1.0f * sc, a2v);
          for (int ci = 0; ci < cols.Length; ci++) { var c = cols[ci]; if (c == null || !c.enabled || c.isTrigger) continue; RaycastHit hi; if (c.Raycast(ray, out hi, 2.5f * sc) && hi.distance < best) { best = hi.distance; point = hi.point; onSurface = true; } } }
        float roll = shape.baseRollDeg;
        if (kind == \"slash\")"""),
("            go.transform.SetPositionAndRotation(point - a2v * row.depth * 0.35f * sc, rot);\n            var p = go.AddComponent<DecalProjector>();",
"            go.transform.SetPositionAndRotation(onSurface ? point : point - a2v * row.depth * 0.35f * sc, rot);\n            var p = go.AddComponent<DecalProjector>();")])
patch(C+r"\BloodBurst.cs",[("r.lengthScale = 3.5f;","r.lengthScale = 1.6f;"),("r.lengthScale = 3.2f;","r.lengthScale = 1.6f;")])

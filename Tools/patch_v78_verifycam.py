P=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\VerifyGround.cs"
b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b; s=b.decode('utf-8-sig').replace('\r\n','\n')
if 'v78 cam check' in s: print('ALREADY'); raise SystemExit
def rep(a,n):
    global s
    assert s.count(a)==1,(a[:60],s.count(a)); s=s.replace(a,n)
rep("        float ft = 0f, nextShot = D.shotEvery; int shotN = 0; int ow = 0;\n","        float ft = 0f, nextShot = D.shotEvery; int shotN = 0; int ow = 0;\n        int camFrames = 0, camOut = 0; float camDistWorst = 0f;   // v78 cam check: the game camera must keep the fighters in view (v77 shipped a dead camera, rock close-up, with the ground check PASS)\n")
rep("            if (ft >= nextShot)\n            {\n                nextShot += D.shotEvery; shotN++;","""            if (ft > 2f)
            {
                var mc = Camera.main;
                if (mc == null || fighters == null) { camFrames++; camOut++; }
                else
                {
                    Vector3 cc = Vector3.zero; int cn = 0; foreach (var ff in fighters) if (ff && ff.gameObject.activeInHierarchy) { cc += ff.transform.position; cn++; }
                    if (cn > 0) { cc = cc / cn + Vector3.up; var vp = mc.WorldToViewportPoint(cc); float cd = Vector3.Distance(mc.transform.position, cc); camFrames++; if (vp.z < 0.1f || vp.x < 0.02f || vp.x > 0.98f || vp.y < 0.02f || vp.y > 0.98f || cd > 14f) camOut++; if (cd > camDistWorst) camDistWorst = cd; }
                }
            }
            if (ft >= nextShot)
            {
                nextShot += D.shotEvery; shotN++;""")
rep("        int fails = 0; foreach (var r in rows) if (r.verdict == \"FAIL\") fails++;\n        if (fightYFail) fails++;\n","""        int fails = 0; foreach (var r in rows) if (r.verdict == "FAIL") fails++;
        if (fightYFail) fails++;
        bool camFail = camFrames > 0 && camOut > camFrames * 0.3f;
        Debug.Log("[VERIFY] CAM " + (camFail ? "FAIL" : "PASS") + " framesOut=" + camOut + "/" + camFrames + " worstDistToFighters=" + camDistWorst.ToString("F1", IC) + " m (limit 14 m, fail when out of view > 30% of frames)");
        if (camFail) fails++;
""")
s=s.replace('\n','\r\n') if crlf else s; o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o; open(P,'wb').write(o); print('patched')

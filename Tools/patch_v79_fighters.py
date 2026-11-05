P=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\VerifyGround.cs"
b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b; s=b.decode('utf-8-sig').replace('\r\n','\n')
if 'v79 fighters check' in s: print('ALREADY'); raise SystemExit
def rep(a,n):
    global s
    assert s.count(a)==1,(a[:60],s.count(a)); s=s.replace(a,n)
rep("        // ---- fight phase ----\n        FightOn();\n","""        // ---- fight phase ----
        // v79 fighters check: exactly two active fighters (player + enemy), distinct names (v79 had a stale second Joan root active)
        int activeFighters = 0; var fnames = new System.Collections.Generic.HashSet<string>(); bool dupFighter = false;
        foreach (var fdf in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (fdf != null && fdf.isActiveAndEnabled) { activeFighters++; if (!fnames.Add(fdf.name)) dupFighter = true; }
        bool fightersFail = activeFighters != 2 || dupFighter;
        Debug.Log("[VERIFY] FIGHTERS " + (fightersFail ? "FAIL" : "PASS") + " active=" + activeFighters + " names=" + string.Join(",", new System.Collections.Generic.List<string>(fnames).ToArray()));
        FightOn();
""")
rep("        if (camFail) fails++;\n","        if (camFail) fails++;\n        if (fightersFail) fails++;\n")
s=s.replace('\n','\r\n') if crlf else s; o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o; open(P,'wb').write(o); print('patched')

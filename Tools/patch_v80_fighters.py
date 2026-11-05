P=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\VerifyGround.cs"
b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b; s=b.decode('utf-8-sig').replace('\r\n','\n')
if 'v80 fighters after FightOn' in s: print('ALREADY'); raise SystemExit
a1="""        int activeFighters = 0; var fnames = new System.Collections.Generic.HashSet<string>(); bool dupFighter = false;
        foreach (var fdf in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (fdf != null && fdf.isActiveAndEnabled) { activeFighters++; if (!fnames.Add(fdf.name)) dupFighter = true; }
        bool fightersFail = activeFighters != 2 || dupFighter;
        Debug.Log("[VERIFY] FIGHTERS " + (fightersFail ? "FAIL" : "PASS") + " active=" + activeFighters + " names=" + string.Join(",", new System.Collections.Generic.List<string>(fnames).ToArray()));
        FightOn();
"""
assert s.count(a1)==1
a2="""        FightOn();
        yield return null;   // v80 fighters after FightOn: the brains / duel components are only enabled by FightOn()
        yield return null;
        int activeFighters = 0; var fnames = new System.Collections.Generic.HashSet<string>(); bool dupFighter = false;
        foreach (var fdf in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (fdf != null && fdf.gameObject.activeInHierarchy) { activeFighters++; if (!fnames.Add(fdf.name)) dupFighter = true; }
        bool fightersFail = activeFighters != 2 || dupFighter;
        Debug.Log("[VERIFY] FIGHTERS " + (fightersFail ? "FAIL" : "PASS") + " active=" + activeFighters + " names=" + string.Join(",", new System.Collections.Generic.List<string>(fnames).ToArray()));
"""
s=s.replace(a1,a2)
s=s.replace('\n','\r\n') if crlf else s; o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o; open(P,'wb').write(o); print('patched')

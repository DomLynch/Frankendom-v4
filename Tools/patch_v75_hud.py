P=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\GameHUD.cs"
b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b
s=b.decode('utf-8-sig').replace('\r\n','\n')
if 'GameLoop.Outcome != GameLoop.Result.None' in s: print('ALREADY'); raise SystemExit
def rep(a,n):
    global s
    assert s.count(a)==1,(a[:60],s.count(a)); s=s.replace(a,n)
rep('string msg = FinisherRTBusy() ? null :','string msg = (FinisherRTBusy() || GameLoop.Outcome != GameLoop.Result.None) ? null :   /* v75 (Goose): the GameLoop end screen owns YOU DIED / VICTORY / Retry */')
rep('if (pf) CombatHudBars.Draw(new Rect(30, 38, 360, 20), pf, true);','if (pf && !pf.Dead) CombatHudBars.Draw(new Rect(30, 38, 360, 20), pf, true);   // v75 (Goose): no stamina / poise strips at 0 HP')
rep('if (ef) CombatHudBars.Draw(new Rect(Screen.width - 390, 38, 360, 20), ef, false);','if (ef && !ef.Dead) CombatHudBars.Draw(new Rect(Screen.width - 390, 38, 360, 20), ef, false);')
s=s.replace('\n','\r\n') if crlf else s
o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o
open(P,'wb').write(o); print('patched')

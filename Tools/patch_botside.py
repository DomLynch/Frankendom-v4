import sys
def patch(P,reps):
    b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b
    s=b.decode('utf-8-sig').replace('\r\n','\n')
    for a,n in reps:
        if n in s: print('ALREADY',P); return
        assert s.count(a)==1,(P,a[:60],s.count(a)); s=s.replace(a,n)
    s=s.replace('\n','\r\n') if crlf else s
    o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o
    open(P,'wb').write(o); print('patched',P)
A=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\Combat"
patch(A+r"\AIBrain.cs",[("        if (wantGuard) df.guardSide = DirectionalGuard.SideIdFor(it.guardDir);",
"        if (wantGuard) df.guardSide = (df.flowBotOn && ControlsTable.SideRequired) ? DirectionalGuard.BotSide(df, o) : DirectionalGuard.SideIdFor(it.guardDir);   // v69: the balance bot reads the foe's swing with controls.json bot.sideReadChance (a human-like read), not the AI profile's null-until-noticed side\n        //")])
patch(A+r"\DirectionalGuard.cs",[("    // FlowBot: aim the guard at the incoming swing",
"""    // v69: AIBrain's guard side for a flowBotOn fighter (the balance bot): the guard side that covers the foe's current swing with chance bot.sideReadChance
    // (one deterministic roll per half second of sim time, so a side is not re-rolled every tick), else a wrong side; no swing = keep the side it holds
    public static int BotSide(DuelFighter df, DuelFighter foe)
    {
        var m = foe != null ? foe.CurrentMove : null;
        if (m == null) return df.guardSide;
        int best = BestSideFor(m);
        uint h = (uint)(Duel.TickCount / 30) * 2654435761u + (uint)df.GetInstanceID() * 40503u; h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        float roll = (h & 0xFFFFu) / 65535f;
        if (roll < ControlsTable.Get().bot.sideReadChance) return best;
        var S = ControlsTable.Get().guard.sides; int n = S.Length + 1;
        for (int k = 1; k <= n; k++) { int idx = (best + k) % n; int id = idx == 0 ? 0 : (S[idx - 1] != null ? S[idx - 1].id : 0); if (id != best) return id; }
        return best;
    }

    // FlowBot: aim the guard at the incoming swing""")])

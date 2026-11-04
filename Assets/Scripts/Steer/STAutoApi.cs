using System;

// STEER lane S4: the ONLY place STAuto touches STSteer (S1, Assets\Scripts\Steer\STTypes.cs). String-based wrapper (enum values by NAME) so the harness logic stays free of the enum types.
public static class STAutoApi
{
    public static bool Available { get { return true; } }
    public static string Missing() { return ""; }

    public static bool Active { get { return STSteer.Active; } }
    public static string Stance { get { return STSteer.Stance.ToString(); } }
    public static string[] StanceNames { get { return Enum.GetNames(typeof(STStance)); } }
    public static string[] TacticNames { get { return Enum.GetNames(typeof(STTactic)); } }

    public static void SetStance(string name) { STSteer.SetStance((STStance)Enum.Parse(typeof(STStance), name)); }
    public static bool UseTactic(string name) { return STSteer.UseTactic((STTactic)Enum.Parse(typeof(STTactic), name)); }
    public static float Cooldown01(string name) { return STSteer.Cooldown01((STTactic)Enum.Parse(typeof(STTactic), name)); }
    public static bool Enabled(string name) { return STSteer.Enabled((STTactic)Enum.Parse(typeof(STTactic), name)); }
    public static string Running { get { var r = STSteer.Running; return r.HasValue ? r.Value.ToString() : "none"; } }
    public static DuelFighter Dracula { get { return STSteer.Dracula; } }
    public static DuelFighter Pitborn { get { return STSteer.Pitborn; } }

    // the harness must see S1's Log() lines in its file: AutoLog is S1's switch (it is set from -steerauto, forced here as well)
    public static void ForceAutoLog() { STSteer.AutoLog = true; }
    public static void Mark(string cmd, string dec, string outc) { STSteer.Log(cmd, dec, outc); }

    // sink(eventName, text): feed lines, tactic start/end (the AI decision as S1 sees it), fight reset
    public static int Hook(Action<string, string> sink)
    {
        STSteer.OnFeed += s => sink("Feed", s);
        STSteer.OnTacticStart += t => sink("TacticStart", t.ToString());
        STSteer.OnTacticEnd += t => sink("TacticEnd", t.ToString());
        STSteer.OnFightReset += () => sink("FightReset", "");
        STSteer.OnTactic += (t, ok) => sink("Tactic", t + (ok ? " accepted" : " refused"));
        return 5;
    }
}

using UnityEngine;

// CHASE v1 (laneCHASE): per-creature chase driver, added by ZoneRuntime to a spawned clone only when chase.json enabled. While it OWNS the body (Chase, Pause, Home) AIBrain.Update returns at once
// (one anchored hunk), so there is no decision, no swing and no RNG draw; it sets df.moveDir / df.run (the same inputs the AI uses) on sim ticks, so the 60 Hz sim stays authoritative.
// Back inside stopGap the AI continues where it was. UNPROVEN IN UNITY.
public static class ChaseTable
{
    [System.Serializable] class File { public bool enabled; public ChaseRow[] rows; }
    static File f; static bool loaded;
    public static bool Enabled { get { Load(); return f != null && ZoneFlag.Allowed(f.enabled); } }   // ZONEFLAG v1
    static void Load()
    {
        if (loaded) return; loaded = true;
        var txt = HudCfg.ReadData("chase.json");
        if (txt != null) { try { f = JsonUtility.FromJson<File>(txt); } catch (System.Exception e) { Debug.LogWarning("[Chase] chase.json parse failed: " + e.Message); } }
        // CHASE-FIX v1 (INT107): a row without a key must not read 0 (giveUpGap 0 = gives up at once): every row starts from the "*" row and overlays only the keys it has.
        if (f != null && f.rows != null && txt != null)
        {
            ChaseRow star = null; foreach (var r in f.rows) if (r != null && r.id == "*") star = r;
            if (star != null) for (int i = 0; i < f.rows.Length; i++)
            {
                var r = f.rows[i]; if (r == null || r == star) continue; string raw = RowText(txt, r.id); if (raw == null) continue;
                try { var m = JsonUtility.FromJson<ChaseRow>(JsonUtility.ToJson(star)); JsonUtility.FromJsonOverwrite(raw, m); f.rows[i] = m; } catch (System.Exception) { }
            }
        }
    }
    static string RowText(string t, string id)   // the json object of the row with this id (string-aware brace match)
    {
        int k = t.IndexOf("\"id\": \"" + id + "\""); if (k < 0) k = t.IndexOf("\"id\":\"" + id + "\""); if (k < 0) return null; int s = t.LastIndexOf('{', k); if (s < 0) return null;
        int d = 0; bool q = false;
        for (int i = s; i < t.Length; i++) { char c = t[i]; if (q) { if (c == '\\') i++; else if (c == '"') q = false; continue; } if (c == '"') q = true; else if (c == '{') d++; else if (c == '}') { d--; if (d == 0) return t.Substring(s, i - s + 1); } }
        return null;
    }
    /// <summary>Row for a roster id, else the "*" row, else null (no chase for that creature).</summary>
    public static ChaseRow Row(string id)
    {
        Load(); if (f == null || f.rows == null) return null; ChaseRow star = null;
        foreach (var r in f.rows) { if (r == null) continue; if (r.id == id) return r; if (r.id == "*") star = r; }
        return star;
    }
}

[DefaultExecutionOrder(5)]
public sealed class ChaseAgent : MonoBehaviour
{
    public ChaseRow row; public DuelFighter df, player; public Vector3 home; public float leash; public bool foreign;   // foreign = followed through a gate: no home leash here, it walks to the arrival spot and vanishes when it gives up
    ChaseState st; int lastTick; bool runClip; Animator anim;
    public bool Owning { get { return enabled && (st.mode == ChaseMode.Chase || st.mode == ChaseMode.Pause || st.mode == ChaseMode.Home); } }
    public bool Done { get { return st.mode == ChaseMode.Done; } }
    public bool GivingUpNow { get { return st.mode == ChaseMode.Pause || st.mode == ChaseMode.Home; } }
    public static bool Owns(AIBrain b) { var a = b.GetComponent<ChaseAgent>(); return a != null && a.Owning; }
    public ChaseMode Mode { get { return st.mode; } }

    public void Init(DuelFighter self, DuelFighter target, Vector3 homePos, float leashM, ChaseRow r, bool isForeign)
    {
        df = self; player = target; home = homePos; leash = leashM; row = r; foreign = isForeign; st = new ChaseState(); lastTick = Duel.TickCount; anim = GetComponentInChildren<Animator>();
    }
    /// <summary>Back to Fight (a fresh engage).</summary>
    public void Restart() { st = new ChaseState(); lastTick = Duel.TickCount; }

    static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return (a - b).magnitude; }

    void Update()
    {
        if (row == null || df == null || player == null || df.Dead) return;
        int now = Duel.TickCount, n = now - lastTick; if (n < 0) n = 0; lastTick = now;
        if (Duel.Frozen) { if (Owning) { df.moveDir = Vector3.zero; df.run = false; } return; }
        bool free = df.state == FState.Loco || df.state == FState.Guard;
        if (n > 0 && free)
        {
            float gap = Flat(df.transform.position, player.transform.position), hd = Flat(df.transform.position, home);
            float pr = player.runSpeed * player.MoveMulNow;
            float v = ChaseCore.Step(ref st, row, n, gap, hd, foreign ? 0f : leash, pr, df.walkSpeed);
            if (st.mode == ChaseMode.Pause || st.mode == ChaseMode.Home)   // gave up: it stops facing and fighting you
            {
                if (df.opponent != null) { if (player.opponent == df) player.opponent = null; df.opponent = null; }
            }
            if (st.mode == ChaseMode.Chase || st.mode == ChaseMode.Home)
            {
                Vector3 to = (st.mode == ChaseMode.Chase ? player.transform.position : home) - df.transform.position; to.y = 0f;
                Vector3 dir = to.sqrMagnitude > 1e-6f ? to.normalized : transform.forward;
                runClip = ChaseCore.UseRun(runClip, v, row.runClipFromMps, row.walkClipBelowMps);
                float nominal = runClip ? df.runSpeed : df.walkSpeed;
                df.run = runClip; df.moveDir = dir * Mathf.Min(1f, nominal > 0f ? v / nominal : 0f);   // |moveDir| x run/walkSpeed = v in MoveTick; wound / exhaust multipliers can only slow it further
            }
            else { df.moveDir = Vector3.zero; df.run = false; }
        }
        if (Owning && anim != null && df.moveDir.sqrMagnitude > 0.0001f)   // presentation only: after DuelFighter.Update wrote anim.speed this frame
        {
            float nominal = df.run ? df.runSpeed : df.walkSpeed, v = df.moveDir.magnitude * nominal;
            anim.speed *= ChaseCore.AnimSpeed(v, nominal, row.animMin, row.animMax);
        }
    }
}

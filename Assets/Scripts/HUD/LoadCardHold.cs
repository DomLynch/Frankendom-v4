using System;
using System.IO;
using UnityEngine;

// LOADCARD v2 (FK loadcard kit, 18 Nov). Static helper for LoadingCard: honest wait, moving bar, version, sim hold.
// A STATIC class on purpose: its JSON config class is nested in a static class, so the signature guard (Tools\_build_sig.ps1) sees no serialized field.
// NO RuntimeInitializeOnLoadMethod here (a compile-only take keeps the old table and the guard refuses a grown set): LoadingCard.Boot, an existing hook, arms everything through LoadingCard.Awake.
// Data: the same loading_card.json that LoadingCard reads (keys below). Missing keys = the defaults below.
// Log lines: [LoadCard] armed / ready / gone.
public static class LoadCardHold
{
    [Serializable] class Cfg2
    {
        public bool waitForPrewarm = true;      // true: the maxSeconds give-up is replaced by hardCapSeconds (a last-resort cap). false = old behaviour
        public float hardCapSeconds = 45f;      // measured card time on C: 13.8-22 s, so 45 s is a real last resort
        public bool holdSim = true;             // Duel.Frozen while the card is up: the first fight starts only when the picture does
        public bool waitForInput = false;       // true: after the card is ready it stays until a key / click / pad button
        public float typicalSeconds = 15f;      // text + bar scale: measured card time on this build, from data
        public bool showVersion = true;         // build_info.json "version" in the corner of the card
        public int prefabsPerFrame = 0;         // >0: ZoneHitch.Warm loads that many zone prefabs per frame instead of all 16-19 in one 7-8 s frame (CODE part C); 0 = old
        public string waitHint = "press any key";
        public float calmMaxWaitS = 3f;         // v2: once prewarm + StartupWarm (+ gear, checked after) are done, the calm-frame wait is waived after this many seconds (<= 0 = old: wait for calm forever, up to the hard cap)
        public bool holdEngage = true;          // v2: ZoneRuntime does not engage an enemy while the card is up (no brain runs anyway under Duel.Frozen; this keeps GameLoop.Enemy / camera lock / HUD target bookkeeping for after the card)
        // v3 (A) first-fight beat: after the card is gone the first engage waits for the player's first move or engageDelayS (<= 0 = no beat, engage at once as in v2). HUD boss bar and LOCK marker are hidden until then.
        public float engageDelayS = 6f;         // v4: 6 s (was 3): the world is still, so a longer first look costs nothing; any input ends it at once
        public bool freezeEnemiesInBeat = true; // v4: during the beat every NON-player fighter is Duel.FrozenFor (no brain, chase, patrol, archer decides or moves; no strike resolves); Joan can walk and look
        public bool firstMoveEngages = true;
        // v3 (B) card typography through the game HUD's own faces (HudDraw / hud.json fonts: Palatino Linotype, Book Antiqua, Georgia, Times New Roman; title = small caps). hudFont false = the old plain label.
        public bool hudFont = true;
        public string titleFont = "title", subFont = "serif", versionFont = "serif";
        public float titleSmallRatio = 0.8f, titleTrack = 0.12f, subSizeMul = 1.1f;   // small-cap size / big size; tracking in em of the big size
        public float[] titleColor = new float[0], subColor = new float[0], versionColor = new float[0];   // r,g,b,a (empty = the defaults in Col())
        public int versionSize = 18;            // px at 1080p, scaled by Screen.height / 1080 (27 px at 2560x1600)
        // v5b: firstLook.mode "v4" (default, today's live behaviour: frozen beat through Duel.FrozenFor, ends on the first move or engageDelayS) or "v5" (passive-alive enemies, ends on attack / proximity / damage / timeout).
        public FirstLookCfg firstLook = new FirstLookCfg();
    }
    [Serializable] class FirstLookCfg
    {
        public string mode = "v4";
        public bool softLockHold = false;       // true: SoftLock.Broken cannot break the lock while HoldEngage() (card up + beat); false = today's behaviour
        public float timeoutS = 20f;            // v5: last-resort timeout of the passive beat (<= 0 = no beat)
        public bool passive = true;             // v5: enemies idle in place (Passive() in the AI / chase / patrol / archer gates), not Duel.FrozenFor
        public float proximityM = 7.5f;         // v5: Joan within this many metres (flat, world transforms) of a live enemy ends the beat and engages it
        public float attackEngageM = 14f;       // v5: on an attack / damage end, enemies within this range engage at once (even beyond their zone aggro)
        public bool endOnAttack = true, endOnDamage = true, firstMoveEngages = false;
        public bool diag = true;                // the "[LoadCard] first look:" line at card gone (positions + distances of every live enemy)
    }
    static Cfg2 c; static string version = ""; static bool ownFreeze, armed, ready; static float readyAt = -1f, armedAt, allDoneAt = -1f; static bool calmWaived;
    static bool v5;   // firstLook.mode == "v5" (set once in C())
    static float goneAt = -1f; static bool beatDone;   // v3: card-gone time (unscaled) and whether the first-fight beat is over

    static Cfg2 C()
    {
        if (c != null) return c;
        c = new Cfg2();
        try { var t = DataPath.ReadText("loading_card.json"); if (!string.IsNullOrEmpty(t)) { var x = JsonUtility.FromJson<Cfg2>(t); if (x != null) c = x; } if (c.firstLook == null) c.firstLook = new FirstLookCfg(); v5 = c.firstLook.mode == "v5"; } catch (Exception e) { Debug.LogWarning("[LoadCard] cfg: " + e.Message); }
        return c;
    }

    [Serializable] class Info { public string version = ""; }
    static string Version()
    {
        if (version.Length > 0 || !C().showVersion) return version;
        try
        {
            string p = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "build_info.json"));
            if (File.Exists(p)) { var i = JsonUtility.FromJson<Info>(File.ReadAllText(p)); if (i != null && !string.IsNullOrEmpty(i.version)) version = i.version; }
            if (version.Length == 0) version = "dev";
        }
        catch (Exception e) { version = "dev"; Debug.LogWarning("[LoadCard] version: " + e.Message); }
        return version;
    }

    /// <summary>Called from LoadingCard.Awake.</summary>
    public static void OnArm(float now)
    {
        try
        {
            armed = true; ready = false; readyAt = -1f; armedAt = now; allDoneAt = -1f; calmWaived = false; var k = C();
            if (k.holdSim && !Duel.Frozen) { Duel.Frozen = true; ownFreeze = true; }
            Debug.Log("[LoadCard] armed: version " + Version() + ", hard cap " + (k.waitForPrewarm ? k.hardCapSeconds.ToString("F0") + " s (no give-up at maxSeconds)" : "off (old maxSeconds give-up)") + ", sim hold " + ownFreeze + ", wait for input " + k.waitForInput + ", typical " + k.typicalSeconds.ToString("F0") + " s");
        }
        catch (Exception e) { Debug.LogWarning("[LoadCard] arm: " + e.Message); }
    }

    /// <summary>Called from LoadingCard.OnDestroy (card gone).</summary>
    public static void OnGone()
    {
        if (!armed) return; armed = false;
        if (ownFreeze) { Duel.Frozen = false; ownFreeze = false; }
        goneAt = Time.unscaledTime; hbAt = 0f; strikeSeen = 0; strikeWarned.Clear(); beatDone = !((v5 ? c.firstLook.timeoutS : C().engageDelayS) > 0f); playerHp = -1f; scanAt = -10f; evalFrame = -1; forced.Clear(); foes = new DuelFighter[0]; foeHp = new float[0]; FirstLookLog();
        Debug.Log("[LoadCard] gone: sim released, card " + (Time.realtimeSinceStartup - armedAt).ToString("F1") + " s, prewarmDone " + (Prewarm92.TotalSeconds >= 0f));
    }

    /// <summary>Seconds after which the card gives up. Old behaviour (maxSeconds) when waitForPrewarm is false.</summary>
    public static float Cap(float maxSeconds) { var k = C(); return k.waitForPrewarm ? Mathf.Max(maxSeconds, k.hardCapSeconds) : maxSeconds; }

    /// <summary>v2: replaces "calm frames AND StartupWarm done" in LoadingCard.Update. calm = the original calm test (frames under calmFrameS for calmForS); others = StartupWarm condition (prewarm is already true when this is evaluated). Same result as before while the machine is quiet; under load (frames 100+ ms forever) the calm test is waived calmMaxWaitS after the others became true, so a hard cap is never the normal exit.</summary>
    public static bool CalmOk(float now, bool calm, bool others)
    {
        if (!others) return false;
        if (allDoneAt < 0f) allDoneAt = now;
        if (calm) return true;
        float w = C().calmMaxWaitS; if (w <= 0f || now - allDoneAt < w) return false;
        if (!calmWaived) { calmWaived = true; Debug.Log("[LoadCard] calm wait waived after " + w.ToString("F1") + " s (frames never calm: last frame " + (Time.unscaledDeltaTime * 1000f).ToString("F0") + " ms)"); }
        return true;
    }

    /// <summary>v2: true while the card is up and holdEngage is on (ZoneRuntime.Poll skips Engage). LoadingCard.Cleared is true in harness runs and after the card is gone.</summary>
    public static bool HoldEngage()
    {
        if (armed && C().holdEngage && !LoadingCard.Cleared) return true;
        return BeatHold();
    }

    /// <summary>v3 (A): true from card gone until the player's first move (firstMoveEngages) or engageDelayS seconds. Callers: ZoneRuntime.Poll (no engage), GameHUD (no boss bar), FlowHud (no LOCK marker). Never true in harness runs (no card there, goneAt stays -1). Sim is NOT frozen in the beat (Joan can move); nobody is engaged, so no brain decides and no damage is dealt.</summary>
    static bool BeatHold()
    {
        if (goneAt < 0f || beatDone) return false;
        if (v5) return BeatHold5();
        float now = Time.unscaledTime, el = now - goneAt;
        if (el >= C().engageDelayS) { beatDone = true; Debug.Log("[LoadCard] first-fight beat over: " + el.ToString("F1") + " s after the card (no input)"); return false; }
        if (C().firstMoveEngages && el >= 0.3f && FirstMove()) { beatDone = true; Debug.Log("[LoadCard] first-fight beat over: first move after " + el.ToString("F1") + " s"); return false; }
        return true;
    }

    /// <summary>v4: called first thing in Duel.FrozenFor(f). True for every fighter except the player while the first-fight beat runs (freezeEnemiesInBeat). Cheap exit (two static compares) outside the beat: FrozenFor runs per fighter per tick.</summary>
    public static bool EnemyFreeze(DuelFighter f)
    {
        if (goneAt < 0f || beatDone || v5 || f == null || f == GameLoop.Player) return false;
        return C().freezeEnemiesInBeat && BeatHold();
    }
    // ---------------- v5b: passive-alive first look (only when firstLook.mode == "v5") ----------------
    /// <summary>v5: true from card gone until the beat ends (attack / proximity / damage / timeout, see FirstLookCfg). Evaluated once per frame (cached).</summary>
    static bool BeatHold5()
    {
        if (evalFrame == Time.frameCount) return evalHold;
        evalFrame = Time.frameCount; evalHold = true;
        try { evalHold = BeatEval(); } catch (Exception e) { beatDone = true; evalHold = false; Debug.LogWarning("[LoadCard] beat: " + e.Message + " (beat off)"); }
        return evalHold;
    }
    static int evalFrame = -1; static bool evalHold;
    static float scanAt = -10f; static DuelFighter[] foes = new DuelFighter[0]; static float[] foeHp = new float[0]; static float playerHp = -1f;
    static readonly System.Collections.Generic.HashSet<DuelFighter> forced = new System.Collections.Generic.HashSet<DuelFighter>(); static float forcedUntil;

    static float hbAt; static int strikeSeen; static readonly System.Collections.Generic.HashSet<DuelFighter> strikeWarned = new System.Collections.Generic.HashSet<DuelFighter>();
    /// <summary>FIRSTLOOK v6 diagnostic (firstLook.diag): once a second "[LoadCard] beat: joan hp X (start Y, delta D) +T s, enemies not striking i/n", and ONE "[LoadCard] beat WARN" per enemy the first time it is seen in a strike state (Windup / Active) during the passive beat. A clean beat shows delta 0.0 on every line and no WARN line; End() writes the closing line (hp delta + strike-state count).</summary>
    static void BeatTelemetry(DuelFighter pl, float now, float el)
    {
        int idle = 0;
        for (int i = 0; i < foes.Length; i++)
        {
            var f = foes[i]; if (f == null) continue;
            if ((f.state == FState.Windup || f.state == FState.Active) && f.CurrentMove != null) { if (strikeWarned.Add(f)) { strikeSeen++; Debug.Log("[LoadCard] beat WARN: " + f.name + " is in " + f.state + " (" + f.CurrentMove.id + ") at +" + el.ToString("F1") + " s while passive, joan hp " + pl.health.ToString("F1")); } }
            else idle++;
        }
        if (now < hbAt) return; hbAt = now + 1f;
        Debug.Log("[LoadCard] beat: joan hp " + pl.health.ToString("F1") + " (start " + playerHp.ToString("F1") + ", delta " + (pl.health - playerHp).ToString("F1") + ") +" + el.ToString("F1") + " s, enemies not striking " + idle + "/" + foes.Length);
    }

    static float Flat(Transform a, Transform b) { Vector3 d = a.position - b.position; d.y = 0f; return d.magnitude; }

    static bool BeatEval()
    {
        var k = C().firstLook; float now = Time.unscaledTime, el = now - goneAt; var pl = GameLoop.Player;
        if (el >= k.timeoutS) return End("timeout " + el.ToString("F1") + " s, no trigger", null, 0f);
        if (pl == null) return true;
        if (now >= scanAt)   // refresh the enemy list twice a second, keep the health baselines of fighters already seen
        {
            scanAt = now + 0.5f; var all = UnityEngine.Object.FindObjectsByType<DuelFighter>(FindObjectsSortMode.None); var l = new System.Collections.Generic.List<DuelFighter>(); var h = new System.Collections.Generic.List<float>();
            foreach (var f in all) { if (f == null || f == pl || f.Dead || !f.isActiveAndEnabled || !f.gameObject.activeInHierarchy) continue; int at = Array.IndexOf(foes, f); l.Add(f); h.Add(at >= 0 ? foeHp[at] : f.health); }
            foes = l.ToArray(); foeHp = h.ToArray();
        }
        if (playerHp < 0f) playerHp = pl.health;
        if (k.diag) BeatTelemetry(pl, now, el);   // FIRSTLOOK v6: [LoadCard] beat: joan hp lines + enemy-strike warning
        if (el < 0.3f) return true;   // ignore the key press that left the card
        if (k.endOnAttack && (((pl.state == FState.Windup || pl.state == FState.Active) && pl.CurrentMove != null) || Input.GetMouseButtonDown(0))) return End("Joan attacked", pl, k.attackEngageM);
        if (k.endOnDamage)
        {
            if (pl.health < playerHp - 0.01f) return End("Joan took damage", pl, k.attackEngageM);
            for (int i = 0; i < foes.Length; i++) if (foes[i] != null && foes[i].health < foeHp[i] - 0.01f) { forced.Clear(); forced.Add(foes[i]); return End("an enemy was hit (" + foes[i].name + ")", pl, k.attackEngageM, true); }
        }
        if (k.proximityM > 0f) for (int i = 0; i < foes.Length; i++) { if (foes[i] == null) continue; float d = Flat(foes[i].transform, pl.transform); if (d <= k.proximityM) return End("Joan within " + k.proximityM.ToString("F1") + " m of " + foes[i].name + " (d " + d.ToString("F1") + " m)", pl, k.proximityM); }
        if (k.firstMoveEngages && FirstMove()) return End("first move", pl, k.attackEngageM);
        return true;
    }
    /// <summary>Ends the beat, logs why, and flags the enemies within range (or the one already in the set) so ZoneRuntime.Poll engages them at once even beyond their zone aggro.</summary>
    static bool End(string why, DuelFighter pl, float range, bool keepSet = false)
    {
        beatDone = true; float now = Time.unscaledTime;
        if (pl != null && C().firstLook.diag) Debug.Log("[LoadCard] beat: joan hp " + pl.health.ToString("F1") + " (start " + playerHp.ToString("F1") + ", delta " + (pl.health - playerHp).ToString("F1") + ") at end: " + why + ", enemy strike states seen " + strikeSeen);
        if (!keepSet) forced.Clear();
        if (pl != null && range > 0f) foreach (var f in foes) { if (f != null && Flat(f.transform, pl.transform) <= range) forced.Add(f); }
        forcedUntil = now + 3f;
        Debug.Log("[LoadCard] first-fight beat over: " + why + " after " + (now - goneAt).ToString("F1") + " s, engaging " + forced.Count);
        return false;
    }
    /// <summary>v5: first line of ZoneRuntime.Engage. While the passive beat runs, any Engage is a forced one (Poll is held): SpawnForced, Surprise ambush. It ends the beat so the world resumes with it.</summary>
    public static void OnEngage()
    {
        if (!v5 || goneAt < 0f || beatDone) return;
        End("a forced engage (spawn / ambush)", null, 0f);
    }
    /// <summary>v5: ZoneRuntime.Poll engages a flagged enemy regardless of its aggro radius, for 3 s after the beat ended. Always false in mode v4 (the set stays empty).</summary>
    public static bool ForceEngage(DuelFighter f) { return f != null && forced.Count > 0 && Time.unscaledTime <= forcedUntil && forced.Contains(f); }
    /// <summary>v5: gate term for the AI brain, stance AI, chase, patrol, archer and burrow checks (next to Duel.FrozenFor): true for every non-player fighter while the passive beat runs. Always false in mode v4. Enemies stay alive: they can be hit, knocked back and play idle; they just do not decide, move or strike.</summary>
    public static bool Passive(DuelFighter f)
    {
        if (!v5 || goneAt < 0f || beatDone || f == null || f == GameLoop.Player) return false;
        return C().firstLook.passive && BeatHold();
    }
    /// <summary>firstLook.softLockHold: SoftLock.Broken cannot break the lock (and clears any earlier break) while the card is up and during the beat. false (default) = today's behaviour.</summary>
    public static bool SoftLockHeld() { return C().firstLook.softLockHold && HoldEngage(); }

    /// <summary>Diagnostic at card gone (all modes, firstLook.diag): world positions and flat / 3D distances of every live enemy, so the real distances of the real launch are in the log.</summary>
    static void FirstLookLog()
    {
        try
        {
            if (!C().firstLook.diag) return;
            var pl = GameLoop.Player; var sb = new System.Text.StringBuilder();
            sb.Append("[LoadCard] first look (mode ").Append(C().firstLook.mode).Append("): ");
            if (pl == null) { sb.Append("no player"); Debug.Log(sb.ToString()); return; }
            Vector3 jp = pl.transform.position; sb.Append("joan=(").Append(jp.x.ToString("F1")).Append(", ").Append(jp.z.ToString("F1")).Append(") enemies:");
            int n = 0;
            foreach (var f in UnityEngine.Object.FindObjectsByType<DuelFighter>(FindObjectsSortMode.None))
            {
                if (f == null || f == pl || f.Dead || !f.gameObject.activeInHierarchy) continue; Vector3 p = f.transform.position; Vector3 d = p - jp; float d3 = d.magnitude; d.y = 0f; n++;
                sb.Append(' ').Append(f.name).Append(" pos=(").Append(p.x.ToString("F1")).Append(", ").Append(p.z.ToString("F1")).Append(") d=").Append(d.magnitude.ToString("F1")).Append(" m (3D ").Append(d3.ToString("F1")).Append(")").Append(f.opponent == pl ? " opponent=Joan" : "").Append(f.isActiveAndEnabled ? "" : " [disabled]").Append(';');
            }
            if (n == 0) sb.Append(" none");
            Debug.Log(sb.ToString());
        }
        catch (Exception e) { Debug.LogWarning("[LoadCard] first look log: " + e.Message); }
    }

    static bool FirstMove()
    {
        try
        {
            if (Input.anyKeyDown || Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) return true;
            var p = GameLoop.Player; return p != null && p.moveDir.sqrMagnitude > 0.01f;
        }
        catch (Exception) { return true; }
    }

    // ---------------- v3 (B) typography ----------------
    static Color Col(float[] a, float r, float g, float b, float al) { return a != null && a.Length >= 3 ? new Color(a[0], a[1], a[2], a.Length > 3 ? a[3] : 1f) : new Color(r, g, b, al); }
    /// <summary>Title in the HUD's small caps (HudDraw.Caps, face "title"). False = not drawn here (hudFont off or any failure): LoadingCard then draws its old plain label.</summary>
    public static bool Title(string title, int size)
    {
        var k = C(); if (!k.hudFont || string.IsNullOrEmpty(title)) return false;
        try
        {
            int big = Mathf.Max(14, Mathf.RoundToInt(size * Screen.height / 1080f)), small = Mathf.Max(10, Mathf.RoundToInt(big * k.titleSmallRatio));
            float lh = big * HudCfg.Get().layout.lineH, y = Screen.height * 0.47f - lh * 0.5f;
            HudDraw.Caps(Screen.width * 0.5f, y, 0.5f, title, k.titleFont, big, small, big * k.titleTrack, Col(k.titleColor, 0.90f, 0.82f, 0.62f, 1f), true, true);
            return true;
        }
        catch (Exception) { return false; }
    }
    /// <summary>Sub text (one or more lines) in the HUD serif face, brighter. False = old label.</summary>
    public static bool SubDraw(string text, int size)
    {
        var k = C(); if (!k.hudFont || string.IsNullOrEmpty(text)) return false;
        try
        {
            int px = Mathf.Max(11, Mathf.RoundToInt(size * k.subSizeMul * Screen.height / 1080f)); float lh = px * 1.3f, y = Screen.height * 0.52f; var col = Col(k.subColor, 0.90f, 0.87f, 0.80f, 1f);
            foreach (var line in text.Split('\n')) { HudDraw.Text(new Rect(0f, y, Screen.width, lh), line, k.subFont, px, TextAnchor.UpperCenter, false, col); y += lh; }
            return true;
        }
        catch (Exception) { return false; }
    }
    static bool VersionDraw(string v)
    {
        var k = C(); if (!k.hudFont) return false;
        try
        {
            int px = Mathf.Max(14, Mathf.RoundToInt(k.versionSize * Screen.height / 1080f));
            HudDraw.Text(new Rect(0f, Screen.height - px * 2.4f, Screen.width * 0.988f, px * 1.6f), "Frankendom " + v, k.versionFont, px, TextAnchor.LowerRight, false, Col(k.versionColor, 0.92f, 0.89f, 0.82f, 1f));
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Extra condition for the card to leave: with waitForInput, a key / click / pad button after everything is ready.</summary>
    public static bool InputOk()
    {
        if (!C().waitForInput) return true;
        if (!ready) { ready = true; readyAt = Time.realtimeSinceStartup; Debug.Log("[LoadCard] ready after " + (readyAt - armedAt).ToString("F1") + " s: waiting for input"); }
        return Input.anyKeyDown;
    }

    /// <summary>Sub text with {s} (elapsed seconds) and {typ} (typical seconds) filled in; a sub without tokens is returned unchanged.</summary>
    public static string Sub(string sub, float elapsed)
    {
        if (sub == null || sub.IndexOf('{') < 0) return sub;
        if (ready && C().waitForInput) return C().waitHint;
        return sub.Replace("{s}", Mathf.FloorToInt(elapsed).ToString()).Replace("{typ}", Mathf.RoundToInt(C().typicalSeconds).ToString());
    }

    static Texture2D px; static GUIStyle sv;
    /// <summary>Thin progress bar under the sub text (time based: the load has no step count; asymptotic so it never sits at 100 % before the card leaves) and the version in the bottom-right corner. Called inside OnGUI.</summary>
    public static void Draw(float elapsed, bool leaving)
    {
        try
        {
            if (px == null) { px = new Texture2D(1, 1); px.SetPixel(0, 0, Color.white); px.Apply(); }
            float typ = Mathf.Max(1f, C().typicalSeconds), f = leaving ? 1f : Mathf.Min(0.97f, 1f - Mathf.Exp(-1.5f * elapsed / typ));
            float w = Screen.width * 0.28f, h = Mathf.Max(3f, Screen.height * 0.004f), x = (Screen.width - w) * 0.5f, y = Screen.height * 0.61f;
            var g = GUI.color; GUI.color = new Color(0.25f, 0.23f, 0.2f, g.a); GUI.DrawTexture(new Rect(x, y, w, h), px);
            GUI.color = new Color(0.85f, 0.78f, 0.62f, g.a); GUI.DrawTexture(new Rect(x, y, w * f, h), px); GUI.color = g;
            string v = Version(); if (v.Length > 0 && !VersionDraw(v))
            {
                if (sv == null) { sv = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerRight, fontSize = Mathf.Max(10, Mathf.RoundToInt(14f * Screen.height / 1080f)) }; sv.normal.textColor = new Color(0.75f, 0.71f, 0.62f, 1f); }
                GUI.Label(new Rect(0, Screen.height * 0.95f, Screen.width * 0.988f, Screen.height * 0.045f), "Frankendom " + v, sv);
            }
        }
        catch (Exception) { }
    }

    public static int PrefabsPerFrame() { return C().prefabsPerFrame; }
}

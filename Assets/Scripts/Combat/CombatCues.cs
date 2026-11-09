using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// laneTOP20A v1 (NEW file; no existing file is edited). Two cues the combat audit found missing, plus a harness-only proof log. All numbers in combat_cues.json.
// A missing file or "enabled": false = this component is not even created (byte-for-byte the old game). The proof log needs the player started with -dirguardproof (harness builds only).
//  1) WRONG SIDE: the player's guard was up and facing the attacker, but its side did not cover the swing (controls.json guard.match), so Duel.Resolve let the blow through as a clean Hit.
//     Before this the player only saw a normal hit and could not tell "I blocked the wrong side" from "my guard did nothing".
//  2) EXHAUSTED: the player's stamina hit 0 (DuelFighter.Exhausted turned true): a short text toast and a pulsing red edge for as long as it lasts. Before this the only sign was a colour pulse on a 6 px bar.
// It only READS fight state (ContactRelay.OnEvent, DuelFighter flags, DirectionalGuard.SideOk which has no side effect) and draws IMGUI. It never changes a number.
// -dirguardproof [-dirproofout:<file>]: one line per contact where the defender had a raised guard facing the attacker:
//   [DIRPROOF] tick=.. a=Pitborn d=Joan move=light_left dir=left dside=right dguard=1 ang=12.0 arcmax=60.0 covers=1 ev=Blocked human=1 swiping=1 shield=0
public class CombatCues : MonoBehaviour
{
    [Serializable] class Cfg
    {
        public bool enabled = false, wrongSide = true, exhausted = true, edge = true;
        public string wrongSideText = "WRONG SIDE", exhaustedText = "EXHAUSTED";
        public float seconds = 0.8f, sizeFrac = 0.04f, yFrac = 0.64f, edgeAlpha = 0.4f, edgeWidthFrac = 0.07f, edgeHz = 1.8f;
        public float[] wrongColor = { 0.95f, 0.72f, 0.28f, 1f }, exhaustedColor = { 0.85f, 0.2f, 0.12f, 1f };
    }
    static Cfg C; static CombatCues inst;
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;

#if FRANKEN_NO_HARNESS
    static bool ProofOn { get { return false; } }
#else
    static readonly bool proofFlag = Array.IndexOf(Environment.GetCommandLineArgs(), "-dirguardproof") >= 0;
    static bool ProofOn { get { return proofFlag; } }
    static string proofOut;
    static DuelFighter[] all = new DuelFighter[0]; static float nextScan;
    static readonly System.Collections.Generic.Dictionary<DuelFighter, bool> wasGuarding = new System.Collections.Generic.Dictionary<DuelFighter, bool>();
#endif

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { C = null; inst = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null) return;
        C = new Cfg();
        try { string t = DataPath.ReadText("combat_cues.json"); if (!string.IsNullOrEmpty(t)) { var r = JsonUtility.FromJson<Cfg>(t); if (r != null) C = r; } }
        catch (Exception e) { Debug.LogWarning("[CombatCues] combat_cues.json unreadable, cues off: " + e.Message); C = new Cfg(); }
        if (!C.enabled && !ProofOn) return;
        var go = new GameObject("CombatCues"); go.hideFlags = HideFlags.HideInHierarchy; DontDestroyOnLoad(go); inst = go.AddComponent<CombatCues>();
    }

    // ---- player snapshot (the state one frame BEFORE the contact: Duel.Resolve staggers the victim before it raises the event) ----
    DuelFighter pl; float nextFind; bool plGuard, plExh, exhPrev;
    string toast = ""; Color toastCol = Color.white; float toastUntil, exhaustedSince = -1f;
    GUIStyle st; Texture2D grad;

    void OnEnable() { ContactRelay.OnEvent += OnEvent; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; }

    static Color Col(float[] a, float alpha) { return a != null && a.Length >= 3 ? new Color(a[0], a[1], a[2], alpha) : new Color(1f, 1f, 1f, alpha); }
    void Toast(string text, float[] col) { toast = text; toastCol = Col(col, 1f); toastUntil = Time.unscaledTime + Mathf.Max(0.1f, C.seconds); }

    void LateUpdate()
    {
        if (pl == null || pl.Dead)
        {
            pl = null;
            if (Time.unscaledTime >= nextFind)
            {
                nextFind = Time.unscaledTime + 0.5f;
                foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsSortMode.None)) if (f != null && f.IsPlayer) { pl = f; break; }
            }
        }
        if (pl != null)
        {
            bool ex = pl.Exhausted && !pl.Dead;
            if (C.enabled && C.exhausted && ex && !exhPrev) Toast(C.exhaustedText, C.exhaustedColor);
            if (ex && !exhPrev) exhaustedSince = Time.unscaledTime; else if (!ex) exhaustedSince = -1f;
            exhPrev = ex; plExh = ex;
            plGuard = pl.IsGuarding;
        }
#if !FRANKEN_NO_HARNESS
        if (ProofOn)
        {
            if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 1f; all = FindObjectsByType<DuelFighter>(FindObjectsSortMode.None); }
            for (int i = 0; i < all.Length; i++) { var f = all[i]; if (f != null) wasGuarding[f] = f.IsGuarding; }
        }
#endif
    }

    void OnEvent(CombatEvent e)
    {
        if (e.actor == null || e.target == null) return;
        var m = e.actor.CurrentMove; if (m == null) return;
        if (Duel.RangedNow) return;   // an arrow is being judged: its move is not the shooter's CurrentMove
        bool wasG = e.target == pl ? plGuard : false;
#if !FRANKEN_NO_HARNESS
        bool pg; if (ProofOn && wasGuarding.TryGetValue(e.target, out pg)) wasG = pg;
#endif
        if (!wasG) return;
        if (e.type != "Hit" && e.type != "Blocked" && e.type != "Parried" && e.type != "GuardBroken") return;
        Vector3 ad = e.target.transform.position - e.actor.transform.position; ad.y = 0f;
        float ang = Vector3.Angle(e.target.transform.forward, -ad);
        float arcMax = e.actor.Rule("guardArc", Mathf.PI / 3f) * Mathf.Rad2Deg * StanceLayer.GuardArcMul(e.target);   // the same cone Duel.Resolve uses
        bool heavy = m.charges || m.damage >= e.actor.HeavyDamage;
        bool covers = DirectionalGuard.SideOk(e.actor, e.target, m, heavy);
        if (C.enabled && C.wrongSide && e.type == "Hit" && e.target == pl && ang <= arcMax && !covers) Toast(C.wrongSideText, C.wrongColor);
#if !FRANKEN_NO_HARNESS
        if (ProofOn && ang <= arcMax + 15f)
        {
            if (proofOut == null) proofOut = Array.Find(Environment.GetCommandLineArgs(), x => x.StartsWith("-dirproofout:", StringComparison.Ordinal)) ?? "";
            var sb = new StringBuilder(220);
            sb.Append("[DIRPROOF] tick=").Append(Duel.TickCount).Append(" a=").Append(e.actor.name).Append(" d=").Append(e.target.name)
              .Append(" move=").Append(m.id).Append(" dir=").Append(m.direction).Append(" dside=").Append(DirectionalGuard.NameOf(e.target.guardSide)).Append(" dguard=1")
              .Append(" ang=").Append(ang.ToString("F1", IC)).Append(" arcmax=").Append(arcMax.ToString("F1", IC)).Append(" covers=").Append(covers ? 1 : 0).Append(" ev=").Append(e.type)
              .Append(" human=").Append(e.target.FlowHuman ? 1 : 0).Append(" swiping=").Append(DirectionalInput.IsSwiping(e.target) ? 1 : 0)
              .Append(" shield=").Append(ShieldHooks.Covers(e.target, m, heavy) ? 1 : 0);
            string line = sb.ToString(); Debug.Log(line);
            if (proofOut.Length > 13) { try { File.AppendAllText(proofOut.Substring(13), line + "\n"); } catch (Exception) { } }
        }
#endif
    }

    void OnGUI()
    {
        if (!C.enabled || Event.current.type != EventType.Repaint) return;
        float h = Screen.height, w = Screen.width;
        if (C.edge && plExh && exhaustedSince >= 0f)
        {
            if (grad == null) { grad = new Texture2D(64, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave }; for (int i = 0; i < 64; i++) { float a = 1f - i / 63f; grad.SetPixel(i, 0, new Color(1f, 1f, 1f, a * a)); } grad.Apply(); }
            float pulse = 0.55f + 0.45f * Mathf.Sin((Time.unscaledTime - exhaustedSince) * Mathf.PI * 2f * C.edgeHz);
            var o = GUI.color; GUI.color = Col(C.exhaustedColor, C.edgeAlpha * pulse); float bw = h * C.edgeWidthFrac;
            GUI.DrawTexture(new Rect(0f, 0f, bw, h), grad);
            var m = GUI.matrix; GUIUtility.RotateAroundPivot(180f, new Vector2(w * 0.5f, h * 0.5f)); GUI.DrawTexture(new Rect(0f, 0f, bw, h), grad); GUI.matrix = m;
            GUIUtility.RotateAroundPivot(90f, new Vector2(w * 0.5f, h * 0.5f)); float s = (w - h) * 0.5f; GUI.DrawTexture(new Rect(s, -s, bw, w), grad); GUI.matrix = m;
            GUIUtility.RotateAroundPivot(-90f, new Vector2(w * 0.5f, h * 0.5f)); GUI.DrawTexture(new Rect(s, -s, bw, w), grad); GUI.matrix = m;
            GUI.color = o;
        }
        if (Time.unscaledTime < toastUntil)
        {
            int size = Mathf.Max(12, Mathf.RoundToInt(h * C.sizeFrac));
            if (st == null) st = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            st.fontSize = size;
            float a = Mathf.Clamp01((toastUntil - Time.unscaledTime) / Mathf.Max(0.1f, C.seconds * 0.5f));
            var r = new Rect(0f, h * C.yFrac, w, size * 1.6f);
            st.normal.textColor = new Color(0f, 0f, 0f, 0.85f * a); GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), toast, st);
            st.normal.textColor = new Color(toastCol.r, toastCol.g, toastCol.b, a); GUI.Label(r, toast, st);
        }
    }
}

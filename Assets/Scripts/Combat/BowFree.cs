using System.Collections.Generic;
using UnityEngine;

// FREEAIM v1 (laneLG, file-only patch, NOT applied). Dom: before combat the player must be able to shoot from a distance with a crosshair, at a target that is not engaged yet.
// Data: bow.json aim.free (BowCfg.Free). Off (enabled false, or no bow up) = every call returns neutral on its first line: today, bit for bit.
//  Wants    free aim is on for this archer: the aim key is held, or (whenNoFoe) the player has no live duel foe (out of combat).
//  Resolve  the camera ray (screen centre) from the archer's plane forward: the first world hit bounds it; the nearest hostile fighter whose chest point lies within max(body radius, along x tan(softSnapDeg)) of the ray
//           and in front of that hit is the target (the sim then lands the arrow on it through Duel.Resolve exactly like an auto-aimed one: guard, roll, posture, hit reaction). No fighter = a point shot: BowShot flies to the
//           hit point (or maxRangeM), judges nothing and sticks in what it meets. The arrow speed for a free shot is aim.free.speedCmS.
//  Camera   HoldsCamera frees the camera from the lock while the key is held; CamApply eases an over-the-shoulder offset and the aim lens in / out (static weight, no scene field).
//  HUD      DrawReticle: a small dot + four ticks that close with the draw (Windup progress), white while the ray is on a fighter.
public static class BowFree
{
    static float w;                 // 0..1 camera weight
    static readonly bool QaHold = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-bow:aimhold") >= 0;
    static float nextCamLog, nextRetLog;   // proof log, once per second, only with aim.free.log
    static float winMax;            // longest WindupLeft seen in this draw (the draw progress denominator)
    static KeyCode key; static string keyName = null;

    static BowCfg.Free F { get { return BowCfg.Get().aim.free; } }
    static bool On { get { var f = F; return f != null && f.enabled && BowCfg.Master && BowHooks.AnyBowUp; } }

    static bool KeyHeld()
    {
        if (QaHold) return true;   // QA only: -bow:aimhold on the player command line stands in for the held aim key (the harness cannot press LeftAlt)
        var f = F; if (f == null || string.IsNullOrEmpty(f.aimHoldKey)) return false;
        if (keyName != f.aimHoldKey) { keyName = f.aimHoldKey; KeyCode k; key = System.Enum.TryParse(keyName, true, out k) ? k : KeyCode.None; }
        return key != KeyCode.None && Input.GetKey(key);
    }

    public static bool Holding { get { return On && KeyHeld(); } }
    /// <summary>CameraRig: the aim key is held with the bow up: the camera leaves the lock-on pose.</summary>
    public static bool HoldsCamera { get { if (!On) return false; var p = BowHooks.HudFighter(); return p != null && BowHooks.Active(p) && KeyHeld(); } }

    public static bool Wants(DuelFighter a)
    {
        if (!On || a == null || !a.IsPlayer || !BowHooks.Active(a)) return false;
        if (KeyHeld()) return true;
        return F.whenNoFoe && (a.opponent == null || a.opponent.Dead);
    }
    /// <summary>BowAim.Tick: turn to the crosshair heading also in Loco while the key is held.</summary>
    public static bool FaceHeld(DuelFighter f) { return On && F.faceWhileHolding && f != null && f.IsPlayer && BowHooks.Active(f) && KeyHeld(); }

    public static Vector3 Heading(DuelFighter f)
    {
        var c = Camera.main; if (c == null) return Vector3.zero;
        Vector3 h = c.transform.forward; h.y = 0f; return h.sqrMagnitude > 1e-4f ? h.normalized : Vector3.zero;
    }

    static float UpMax(BowCfg.Free f) { return Mathf.Max(0f, f.reticleUpMaxFrac); }   // DATALAYER 18 Nov: bow.json aim.free.reticleUpMaxFrac (default 0.4)
    /// <summary>RETICLEUP v1: the aim ray direction: through the screen point reticleUpFrac of the height above the centre (0 = the camera forward, exactly as before).</summary>
    public static Vector3 RayDir(Camera cam)
    {
        var f = F; float up = f != null ? Mathf.Clamp(f.reticleUpFrac, -UpMax(f), UpMax(f)) : 0f;
        if (Mathf.Abs(up) < 1e-4f) return cam.transform.forward;
        return cam.ViewportPointToRay(new Vector3(0.5f, 0.5f + up, 0f)).direction;
    }

    /// <summary>The target of the camera ray: t = a hostile fighter (point = its aim point) or null (point = where the ray ends). False = no camera.</summary>
    public static bool Resolve(DuelFighter a, out DuelFighter t, out Vector3 point)
    {
        t = null; point = Vector3.zero; var cam = Camera.main; if (cam == null || a == null) return false;
        var Fr = F; var A = BowCfg.Get().arrow;
        Vector3 dir = RayDir(cam), o = cam.transform.position;   // RETICLEUP v1: the ray goes through the (raised) reticle, not the screen centre
        float along0 = Vector3.Dot(a.transform.position + Vector3.up * (A.releaseHeightM * a.scale) - o, dir);
        o += dir * Mathf.Max(0f, along0);                                  // the ray starts at the archer's plane: a wall behind her never blocks it
        float max = Mathf.Max(Fr.minRangeM, Fr.maxRangeM), wd = max; DM03.Log("aim.free.minRangeM", 1, Fr.minRangeM); Vector3 wp = o + dir * max;
        var hits = Physics.RaycastAll(o, dir, max, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            var c = hits[i].collider; if (c == null || c.transform.IsChildOf(a.transform) || c.GetComponentInParent<DuelFighter>() != null) continue;
            if (hits[i].distance < wd) { wd = hits[i].distance; wp = hits[i].point; }
        }
        var all = BowHooks.Fighters; float tan = Mathf.Tan(Mathf.Deg2Rad * Mathf.Max(0f, Fr.softSnapDeg)), best = float.MaxValue;
        if (all != null)
            for (int i = 0; i < all.Count; i++)
            {
                var f = all[i]; if (f == null || f == a || f.Dead || f.IsPlayer == a.IsPlayer || !f.isActiveAndEnabled) continue;
                Vector3 chest = f.transform.position + Vector3.up * (A.aimHeightM * f.scale), v = chest - o; float al = Vector3.Dot(v, dir);
                if (al < 0.5f || al > wd + f.radius * 2f) continue;
                float perp = (v - dir * al).magnitude, tol = Mathf.Max(f.radius * Mathf.Max(1f, f.scale), al * tan);
                if (perp <= tol && al < best) { best = al; t = f; point = chest; }
            }
        if (t == null) point = wp;
        return true;
    }

    // ---- camera (CameraRig.LateUpdate, after the pose and lens) ----
    public static void CamApply(Transform camT, Camera cam, float dt)
    {
        bool hold = HoldsCamera;
        if (!hold && w <= 0f) return;
        var Fr = F; w = Mathf.MoveTowards(w, hold ? 1f : 0f, dt / Mathf.Max(0.02f, hold ? Fr.easeInS : Fr.easeOutS));
        float k = w * w * (3f - 2f * w);
        var so = Fr.shoulderOffset; if (so != null && so.Length >= 3) camT.position += camT.right * (so[0] * k) + camT.up * (so[1] * k) + camT.forward * (so[2] * k);
        if (Fr.aimFov > 1f) cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, Fr.aimFov, k);
        if (Fr.log && Time.unscaledTime >= nextCamLog) { nextCamLog = Time.unscaledTime + 1f; Debug.Log("[bow] free cam: weight " + w.ToString("F2") + " (k " + k.ToString("F2") + ") hold " + hold + " fov " + cam.fieldOfView.ToString("F1") + " (aim " + Fr.aimFov.ToString("F0") + ") shoulder " + (so != null && so.Length >= 3 ? (so[0] * k).ToString("F2") + "/" + (so[1] * k).ToString("F2") + "/" + (so[2] * k).ToString("F2") : "none") + " camPos " + camT.position.ToString("F2")); }
    }

    // ---- HUD (BowHud.OnGUI) ----
    public static void DrawReticle(DuelFighter f, bool bow)
    {
        if (!bow || !Wants(f)) { if (f == null || f.state == FState.Loco) winMax = 0f; return; }
        var Fr = F; float prog = 0f;
        if (f.state == FState.Windup) { int wl = f.WindupLeft; if (wl > winMax) winMax = wl; prog = winMax > 0f ? 1f - Mathf.Clamp01(wl / winMax) : 0f; }
        else if (f.state != FState.Loco) prog = 1f; else winMax = 0f;
        DuelFighter t; Vector3 p; bool on = Resolve(f, out t, out p) && t != null;
        Color c = BowCfg.Col(on ? Fr.colourOnTarget : Fr.colour, Color.white);
        float cx = Screen.width * 0.5f, cy = Screen.height * (0.5f - Mathf.Clamp(Fr.reticleUpFrac, -UpMax(Fr), UpMax(Fr))), g = Mathf.Lerp(Fr.gapPx, Fr.gapPx * Fr.tightenTo, prog), L = Fr.tickLenPx, th = Fr.tickThickPx, d = Fr.dotPx;
        if (Fr.log && Time.unscaledTime >= nextRetLog) { nextRetLog = Time.unscaledTime + 1f; Debug.Log("[bow] free reticle: centre " + cx.ToString("F0") + "," + cy.ToString("F0") + " px (screen " + Screen.width + "x" + Screen.height + ") colour " + (on ? Fr.colourOnTarget : Fr.colour) + " on-target " + on + (on ? " target " + t.name : "") + " draw " + prog.ToString("F2") + " gap " + g.ToString("F1") + "px up " + Fr.reticleUpFrac.ToString("F2") + (on ? "" : " ray-end " + p.ToString("F1"))); }
        HudDraw.Fill(new Rect(cx - d * 0.5f, cy - d * 0.5f, d, d), c);
        HudDraw.Fill(new Rect(cx - g - L, cy - th * 0.5f, L, th), c); HudDraw.Fill(new Rect(cx + g, cy - th * 0.5f, L, th), c);
        HudDraw.Fill(new Rect(cx - th * 0.5f, cy - g - L, th, L), c); HudDraw.Fill(new Rect(cx - th * 0.5f, cy + g, th, L), c);
    }
}

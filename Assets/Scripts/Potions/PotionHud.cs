using System;
using UnityEngine;

// POTIONS v1: the potion count on the HUD. Own OnGUI host (like BowHud), so GameHUD / hud.json are untouched. One chip, bottom left (consumables.json hud):
//   [key]   Healing draught   x3          a thin bar under it fills while that potion is on cooldown
//   the last use / refusal shows as a short line above the chip (hud.toastSeconds)
// Hidden while a finisher plays, in menus, and when no player exists. Sizes are px at the HUD reference height and scale with HudCfg.Scale() like the market panel.
public sealed class PotionHud : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try { if (!PotionCfg.On || !PotionCfg.Get().hud.enabled || GameLoop.HarnessOn || FindFirstObjectByType<PotionHud>() != null) return; var go = new GameObject("PotionHud"); DontDestroyOnLoad(go); go.AddComponent<PotionHud>(); }
        catch (Exception e) { Debug.LogWarning("[Potions] hud boot failed: " + e.Message); }
    }

    static string KeyLabel()
    {
        var k = PotionCfg.UseKeys(); return k.Length > 0 ? k[0].ToString() : "";
    }

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        var C = PotionCfg.Get(); if (!C.enabled || !C.hud.enabled) return;
        var pl = GameLoop.Player; if (pl == null || pl.Dead || !pl.gameObject.activeInHierarchy) return;
        if (RTFinisher.Busy || LootScreen.Open || MarketUI.Open) return;
        var H = C.hud; var T = C.text; var K = HudCfg.Get().colors;
        float s = HudCfg.Scale(); var m = GUI.matrix; GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f)); float vh = Screen.height / s;
        Color ink = HudCfg.Col(K.ink), dim = HudCfg.Col(K.dim), gold = HudCfg.Col(K.gold);
        HudDraw.Alpha = 1f;
        string id = PotionUse.EnsureSelected(); float y = vh - H.bottomPx;
        string line; Color col;
        if (id.Length > 0) { var p = PotionCfg.Find(id); line = string.Format(T.chip, KeyLabel(), p.name.Length > 0 ? p.name : id, PotionStore.Qty(id)); col = ink; }
        else { line = string.Format(T.chipNone, KeyLabel()); col = dim; }
        HudDraw.Text(new Rect(H.leftPx, y, H.widthPx, H.nameSize * 1.5f), line, "serif", H.nameSize, TextAnchor.UpperLeft, true, col);
        if (id.Length > 0)
        {
            float cd = PotionUse.CooldownLeft01(id), bw = H.widthPx * 0.6f, by = y + H.nameSize * 1.5f + 2f;
            HudDraw.Fill(new Rect(H.leftPx, by, bw, H.barH), HudCfg.Col(K.frameDim));
            if (cd > 0f) HudDraw.Fill(new Rect(H.leftPx, by, bw * (1f - cd), H.barH), dim); else HudDraw.Fill(new Rect(H.leftPx, by, bw, H.barH), gold);
            if (PotionUse.KindsCarried() > 1 && PotionCfg.CycleKeys().Length > 0)
                HudDraw.Text(new Rect(H.leftPx, by + H.barH + 2f, H.widthPx, H.hintSize * 1.5f), string.Format(T.cycleHint, PotionCfg.CycleKeys()[0].ToString()), "serif", H.hintSize, TextAnchor.UpperLeft, false, dim);
        }
        float age = Time.unscaledTime - PotionUse.ToastAt;
        if (PotionUse.ToastText.Length > 0 && age >= 0f && age <= H.toastSeconds)
        {
            HudDraw.Alpha = Mathf.Clamp01(Mathf.Min(age / 0.1f, (H.toastSeconds - age) / 0.4f));
            HudDraw.Text(new Rect(H.leftPx, y - H.toastSize * 1.5f - H.toastGapPx, H.widthPx, H.toastSize * 1.5f), PotionUse.ToastText, "serif", H.toastSize, TextAnchor.UpperLeft, true, gold);
            HudDraw.Alpha = 1f;
        }
        GUI.matrix = m;
    }
}

using UnityEngine;

// Minimal lock-on marker + riposte hint (lane V64FLOW). Text and a small square dot only, no art. Data: flow.json lockOn.hud and riposte.hint.
// Created at runtime (no scene edit) when flow.json is enabled; draws only while a PlayerBrain is enabled (a human plays) or FlowTable.ForceHud (proof harness).
public class FlowHud : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!FlowTable.Get().enabled) return;
        if (FindFirstObjectByType<FlowHud>() != null) return;
        var go = new GameObject("FlowHud"); DontDestroyOnLoad(go); go.AddComponent<FlowHud>();
    }

    PlayerBrain pb; DuelFighter df; CameraRig rig; Camera cam; float nextFind; GUIStyle small, toast, hint; string ripTxt, ripK, ripFor;   // UIDATA_A v1: ripTxt = cached "<hintText>  [<key>]" (was a concat per frame)

    void Find()
    {
        /* UIDATA_A v1 */ nextFind = Time.unscaledTime + HudLayout.Get().flow.findEvery;
        if (pb == null) pb = FindFirstObjectByType<PlayerBrain>();
        if (pb != null) { df = pb.GetComponent<DuelFighter>(); rig = pb.rig != null ? pb.rig : FindFirstObjectByType<CameraRig>(); if (rig != null) cam = rig.GetComponent<Camera>(); }
    }
    static Color C(float[] a, float alpha = -1f) { if (a == null || a.Length < 3) return Color.white; return new Color(a[0], a[1], a[2], alpha >= 0f ? alpha : (a.Length > 3 ? a[3] : 1f)); }
    GUIStyle Style(int size, TextAnchor anchor, bool bold) { var s = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = anchor, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal }; s.normal.textColor = Color.white; return s; }
    static void Shadowed(Rect r, string txt, GUIStyle st, Color col)
    {
        var o = new Rect(r.x + HudLayout.Get().flow.shadowOffset, r.y + HudLayout.Get().flow.shadowOffset, r.width, r.height); st.normal.textColor = new Color(0f, 0f, 0f, col.a * HudLayout.Get().flow.shadowAlpha); GUI.Label(o, txt, st);
        st.normal.textColor = col; GUI.Label(r, txt, st);
    }

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        var d = FlowTable.Get(); if (!d.enabled) return;
        if (Time.unscaledTime >= nextFind || pb == null) Find();
        if (pb == null || df == null || df.Dead || (!pb.enabled && !FlowTable.ForceHud)) return;
        var h = d.lockOn.hud; Color col = C(h.color);
        if (small == null) { small = Style(h.fontSize, TextAnchor.UpperCenter, true); toast = Style(h.toastFontSize, TextAnchor.MiddleCenter, true); hint = Style(d.riposte.fontSize, TextAnchor.MiddleCenter, true); }

        // lock marker: a small dot on the target's chest while the lock is on and the target is in range
        if (d.lockOn.enabled && h.enabled && rig != null && rig.lockEnabled && df.opponent != null && !df.opponent.Dead && cam != null)
        {
            Vector3 dv = df.opponent.transform.position - df.transform.position; dv.y = 0f;
            if (dv.magnitude <= d.lockOn.range)
            {
                Vector3 sp = cam.WorldToScreenPoint(df.opponent.transform.position + Vector3.up * (h.yOffset * FighterScale.Of(df.opponent)));
                if (sp.z > HudLayout.Get().flow.nearZ)
                {
                    float x = sp.x, y = Screen.height - sp.y, px = h.dotPx;
                    if (h.dot)
                    {
                        GUI.color = new Color(0f, 0f, 0f, col.a * HudLayout.Get().flow.dotShadowAlpha); GUI.DrawTexture(new Rect(x - px * 0.5f - HudLayout.Get().flow.dotOutline, y - px * 0.5f - HudLayout.Get().flow.dotOutline, px + HudLayout.Get().flow.dotOutlineSize, px + HudLayout.Get().flow.dotOutlineSize), Texture2D.whiteTexture);
                        GUI.color = col; GUI.DrawTexture(new Rect(x - px * 0.5f, y - px * 0.5f, px, px), Texture2D.whiteTexture); GUI.color = Color.white;
                    }
                    if (!string.IsNullOrEmpty(h.text)) Shadowed(new Rect(x - HudLayout.Get().flow.labelDx, y + px * 0.5f + HudLayout.Get().flow.labelDy, HudLayout.Get().flow.labelW, HudLayout.Get().flow.labelH), h.text, small, new Color(col.r, col.g, col.b, col.a * HudLayout.Get().flow.labelAlpha));
                }
            }
        }
        // toggle toast
        if (d.lockOn.enabled && h.toast && Time.unscaledTime - FlowTable.lastToggleTime < h.toastSeconds)
        {
            float a = Mathf.Clamp01(1f - (Time.unscaledTime - FlowTable.lastToggleTime) / h.toastSeconds);
            Shadowed(new Rect(Screen.width * 0.5f - HudLayout.Get().flow.toastDx, Screen.height * h.toastY - HudLayout.Get().flow.toastDy, HudLayout.Get().flow.toastW, HudLayout.Get().flow.toastH), FlowTable.lastToggleOn ? h.onText : h.offText, toast, new Color(col.r, col.g, col.b, Mathf.Min(1f, a * HudLayout.Get().flow.toastAlphaMul)));
        }
        // riposte window hint
        if (d.riposte.enabled && d.riposte.hint && df.RiposteLeft > 0)
        {
            string k = FlowTable.FirstKeyLabel(d.riposte.keys, "B");
            if (ripTxt == null || !string.Equals(ripK, k) || !string.Equals(ripFor, d.riposte.hintText)) { ripK = k; ripFor = d.riposte.hintText; ripTxt = d.riposte.hintText + HudLayout.Get().flow.hintOpen + k + HudLayout.Get().flow.hintClose; }
            Shadowed(new Rect(Screen.width * 0.5f - HudLayout.Get().flow.hintDx, Screen.height * HudLayout.Get().flow.hintY - HudLayout.Get().flow.hintDy, HudLayout.Get().flow.hintW, HudLayout.Get().flow.hintH), ripTxt, hint, HudLayout.Col(HudLayout.Get().flow.hintColor));
        }
    }
}

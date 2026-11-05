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

    PlayerBrain pb; DuelFighter df; CameraRig rig; Camera cam; float nextFind; GUIStyle small, toast, hint;

    void Find()
    {
        nextFind = Time.unscaledTime + 0.5f;
        if (pb == null) pb = FindFirstObjectByType<PlayerBrain>();
        if (pb != null) { df = pb.GetComponent<DuelFighter>(); rig = pb.rig != null ? pb.rig : FindFirstObjectByType<CameraRig>(); if (rig != null) cam = rig.GetComponent<Camera>(); }
    }
    static Color C(float[] a, float alpha = -1f) { if (a == null || a.Length < 3) return Color.white; return new Color(a[0], a[1], a[2], alpha >= 0f ? alpha : (a.Length > 3 ? a[3] : 1f)); }
    GUIStyle Style(int size, TextAnchor anchor, bool bold) { var s = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = anchor, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal }; s.normal.textColor = Color.white; return s; }
    static void Shadowed(Rect r, string txt, GUIStyle st, Color col)
    {
        var o = new Rect(r.x + 1, r.y + 1, r.width, r.height); st.normal.textColor = new Color(0f, 0f, 0f, col.a * 0.85f); GUI.Label(o, txt, st);
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
                if (sp.z > 0.1f)
                {
                    float x = sp.x, y = Screen.height - sp.y, px = h.dotPx;
                    if (h.dot)
                    {
                        GUI.color = new Color(0f, 0f, 0f, col.a * 0.8f); GUI.DrawTexture(new Rect(x - px * 0.5f - 1.5f, y - px * 0.5f - 1.5f, px + 3f, px + 3f), Texture2D.whiteTexture);
                        GUI.color = col; GUI.DrawTexture(new Rect(x - px * 0.5f, y - px * 0.5f, px, px), Texture2D.whiteTexture); GUI.color = Color.white;
                    }
                    if (!string.IsNullOrEmpty(h.text)) Shadowed(new Rect(x - 40f, y + px * 0.5f + 3f, 80f, 18f), h.text, small, new Color(col.r, col.g, col.b, col.a * 0.85f));
                }
            }
        }
        // toggle toast
        if (d.lockOn.enabled && h.toast && Time.unscaledTime - FlowTable.lastToggleTime < h.toastSeconds)
        {
            float a = Mathf.Clamp01(1f - (Time.unscaledTime - FlowTable.lastToggleTime) / h.toastSeconds);
            Shadowed(new Rect(Screen.width * 0.5f - 120f, Screen.height * h.toastY - 12f, 240f, 24f), FlowTable.lastToggleOn ? h.onText : h.offText, toast, new Color(col.r, col.g, col.b, Mathf.Min(1f, a * 1.6f)));
        }
        // riposte window hint
        if (d.riposte.enabled && d.riposte.hint && df.RiposteLeft > 0)
        {
            string k = FlowTable.FirstKeyLabel(d.riposte.keys, "B");
            Shadowed(new Rect(Screen.width * 0.5f - 160f, Screen.height * 0.78f - 14f, 320f, 28f), d.riposte.hintText + "  [" + k + "]", hint, new Color(0.98f, 0.85f, 0.45f, 0.95f));
        }
    }
}

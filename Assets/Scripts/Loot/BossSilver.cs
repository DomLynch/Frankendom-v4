using System;
using System.IO;
using UnityEngine;

// BOSSSILVER v1 (unity-dev): the player's kill of a boss pays silver once. boss_silver.json (Assets/Data, copy in StreamingAssets, read first):
//   { "enabled": true, "rows": [ { "id": "Pitborn", "silver": 16 }, ... ], "text": {"toast": "+{0} silver"}, "toastSeconds": 3, "toastSize": 24, "toastBottom": 190, "color": [0.93,0.88,0.78] }
// A missing file / enabled false = no boss pays anything (today). The payment is LootSave.CommitGrant: one atomic save with a request id (a kill pays once; the id is unique per kill).
// The market must be on for coins to exist (MarketCfg.enabled); otherwise nothing is paid.
public static class BossSilver
{
    [Serializable] public class Row { public string id = ""; public int silver = 0; }
    [Serializable] public class TextC { public string toast = "+{0} silver"; }
    [Serializable] public class Cfg { public bool enabled = false; public Row[] rows = new Row[0]; public TextC text = new TextC(); public float toastSeconds = 3f; public int toastSize = 24; public float toastBottom = 190f; public float[] color = new float[0]; }

    static Cfg c; static readonly System.Collections.Generic.HashSet<DuelFighter> paid = new System.Collections.Generic.HashSet<DuelFighter>();
    public static string LastToast = ""; static float toastT0 = -99f;
    public static Cfg Get()
    {
        if (c != null) return c;
        c = new Cfg();
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, "boss_silver.json"); if (!File.Exists(p)) p = Path.Combine(Application.dataPath, "Data/boss_silver.json");
            if (File.Exists(p)) { var k = JsonUtility.FromJson<Cfg>(File.ReadAllText(p).TrimStart((char)0xFEFF)); if (k != null) c = k; }
        }
        catch (Exception e) { Debug.LogWarning("[BossSilver] read failed, off: " + e.Message); c = new Cfg(); }
        if (c.rows == null) c.rows = new Row[0]; if (c.text == null) c.text = new TextC();
        return c;
    }
    public static int SilverFor(string id) { var k = Get(); if (string.IsNullOrEmpty(id)) return 0; foreach (var r in k.rows) if (r != null && r.id == id) return Mathf.Max(0, r.silver); return 0; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { c = null; paid.Clear(); LastToast = ""; toastT0 = -99f; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        ContactRelay.OnKilled -= OnKilled;   // no domain reload in the editor: never double-subscribe
        if (!Get().enabled) return;
        ContactRelay.OnKilled += OnKilled;
        var go = new GameObject("BossSilverToast"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<BossSilverToast>();
    }

    static void OnKilled(DuelFighter victim, MoveDef m)
    {
        if (victim == null || !Get().enabled || !MarketCfg.Get().enabled) return;
        var killer = victim.opponent; if (killer == null || killer == victim) return;
        if (!paid.Add(victim)) return;   // one kill, one payment (the finisher path and Die() can both fire)
        string id = victim.CharacterId; int amount = SilverFor(id); if (amount <= 0) return;
        string req = "boss-" + id + "-" + Duel.TickCount + "-" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(victim);
        int cap = MarketCfg.Get().currency.cap;
        if (LootSave.CommitGrant(req, amount, MarketCfg.Get().rules.keepRequests, cap)) { LastToast = string.Format(Get().text.toast, amount); toastT0 = Time.unscaledTime; Debug.Log("[BossSilver] " + id + " paid " + amount + " silver (wallet " + LootSave.Coins + ")"); }
    }

    internal static bool ToastActive(out string text, out float alpha)
    {
        text = LastToast; alpha = 0f; if (text.Length == 0) return false;
        float t = Time.unscaledTime - toastT0, d = Mathf.Max(0.5f, Get().toastSeconds); if (t < 0f || t > d) return false;
        alpha = Mathf.Clamp01(Mathf.Min(t / 0.25f, (d - t) / 0.5f)); return true;
    }
}

/// <summary>Draws the "+N silver" toast. Colours / size / position are boss_silver.json.</summary>
public sealed class BossSilverToast : MonoBehaviour
{
    void OnGUI()
    {
        string text; float a; if (!BossSilver.ToastActive(out text, out a)) return;
        var k = BossSilver.Get(); GUI.depth = -9000;
        var col = HudCfg.Col(k.color, a);
        HudDraw.Text(new Rect(0f, Screen.height - k.toastBottom, Screen.width, k.toastSize * 1.6f), text, "serif", k.toastSize, TextAnchor.UpperCenter, false, col);
    }
}

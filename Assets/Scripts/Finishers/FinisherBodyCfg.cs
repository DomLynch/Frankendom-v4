using System;
using UnityEngine;

// HCKIT4 v95 (HC3 K11 / PERF2 "FinisherBody region heights -> per-character roster"): the fallback bounds of a fighter whose skinned meshes are not available, the cap of a stray huge bound, the scale floor
// and the renderer refresh. They were literals in FinisherBody.cs (0.9 / 0.8,1.8,0.8 / 3.2,3.4,3.2 / 0.5 / 0.25). Now one row per roster id in finishers.json "bodyBounds.rows"
// (exact id first through RosterResolver, then the "*" row, then the built-in numbers below = today's values). A new character is one row; a missing key changes nothing.
public static class FinisherBodyCfg
{
    [Serializable]
    public class Row
    {
        public string character = "*";
        public float centerYM = 0.9f, scaleFloor = 0.5f, refreshS = 0.25f;
        public float[] sizeM = { 0.8f, 1.8f, 0.8f }, maxSizeM = { 3.2f, 3.4f, 3.2f };
    }
    [Serializable] class Block { public Row[] rows = new Row[0]; }
    [Serializable] class Root { public Block bodyBounds; }

    public static readonly Row Default = new Row();
    static Row[] rows; static bool loaded;

    public static Vector3 V(float[] a) { return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.one; }

    static void Load()
    {
        loaded = true; rows = new Row[0];
        try
        {
            string s = DataPath.ReadText("finishers.json");
            if (string.IsNullOrEmpty(s)) return;
            var r = JsonUtility.FromJson<Root>(s);
            if (r != null && r.bodyBounds != null && r.bodyBounds.rows != null) rows = r.bodyBounds.rows;
        }
        catch (Exception ex) { Debug.LogWarning("FinisherBodyCfg: finishers.json read failed: " + ex.Message); }
    }

    /// <summary>The row for a fighter root (exact roster id, else "*", else the built-in defaults).</summary>
    public static Row For(Transform root)
    {
        if (!loaded) Load();
        if (root == null) return Default;
        var r = RosterResolver.Pick(rows, x => x.character, root.name);
        if (r == null) return Default;
        if (r.sizeM == null || r.sizeM.Length < 3) r.sizeM = Default.sizeM;
        if (r.maxSizeM == null || r.maxSizeM.Length < 3) r.maxSizeM = Default.maxSizeM;
        if (r.scaleFloor <= 0f) r.scaleFloor = Default.scaleFloor;
        if (r.refreshS <= 0f) r.refreshS = Default.refreshS;
        return r;
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// LODREPAIR v1 (Lead 19 Nov, LodAudit finding: 41-95 v10sky_* massif/hill groups per zone had a LODGroup with NO renderer in any level, so the LOD0, LOD1 and LOD2 children drew at once).
// Rebuilds the LODs of every LODGroup whose levels hold no renderer: children named LOD0 / LOD1 / LOD2 become levels 0 / 1 / 2. Groups that already have renderers are never touched.
// Last level height 0 = never culled (distant massifs must not vanish). Flag -nolodrepair switches it off (before/after pairs). Started from ZoneGroundMat.Boot (an existing boot path: a new RuntimeInitializeOnLoadMethod would not run in a compile-only take).
// Log: [LodRepair] rebuilt N empty LODGroups (A active), S already fine or no LOD children.
public sealed class LodRepair : MonoBehaviour
{
    static LodRepair inst;
    static readonly float[] Heights = { 0.25f, 0.08f, 0f };

    public static void Boot()
    {
        if (inst != null) return;
        foreach (var a in Environment.GetCommandLineArgs()) if (a == "-nolodrepair") return;
        var go = new GameObject("LodRepair"); DontDestroyOnLoad(go); inst = go.AddComponent<LodRepair>();
    }

    void Start()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) => StartCoroutine(Run(2f));
        StartCoroutine(Run(2f));
        StartCoroutine(Run(25f));   // zone roots that switch on later
    }

    IEnumerator Run(float delayS)
    {
        float t = Time.unscaledTime + delayS; while (Time.unscaledTime < t) yield return null;
        try { Repair(); } catch (Exception e) { Debug.LogWarning("[LodRepair] failed: " + e.Message); }
    }

    static bool HasRenderers(LODGroup g)
    {
        foreach (var l in g.GetLODs()) if (l.renderers != null) foreach (var r in l.renderers) if (r != null) return true;
        return false;
    }

    static void Repair()
    {
        int fixedN = 0, fixedActive = 0, skipped = 0;
        foreach (var g in FindObjectsByType<LODGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (g == null) continue;
            if (HasRenderers(g)) { skipped++; continue; }
            var lod = new Renderer[3][]; int n = 0;
            for (int i = 0; i < g.transform.childCount; i++)
            {
                var ch = g.transform.GetChild(i); var nm = ch.name;
                if (nm.Length == 4 && nm.StartsWith("LOD", StringComparison.Ordinal) && nm[3] >= '0' && nm[3] <= '2')
                {
                    var rs = ch.GetComponentsInChildren<Renderer>(true); if (rs.Length == 0) continue;
                    lod[nm[3] - '0'] = rs; n++;
                }
            }
            if (n == 0) { skipped++; continue; }
            var list = new List<LOD>(); int slot = 0;
            for (int i = 0; i < 3; i++)
            {
                if (lod[i] == null) continue;
                float h = Heights[Mathf.Min(slot, Heights.Length - 1)];
                if (slot == n - 1) h = 0f;
                if (list.Count > 0 && h >= list[list.Count - 1].screenRelativeTransitionHeight) h = list[list.Count - 1].screenRelativeTransitionHeight * 0.5f;
                list.Add(new LOD(h, lod[i])); slot++;
            }
            g.SetLODs(list.ToArray()); g.fadeMode = LODFadeMode.None; g.RecalculateBounds(); fixedN++; if (g.gameObject.activeInHierarchy) fixedActive++;
        }
        Debug.Log("[LodRepair] rebuilt " + fixedN + " empty LODGroups (" + fixedActive + " active), " + skipped + " already fine or no LOD children");
    }
}
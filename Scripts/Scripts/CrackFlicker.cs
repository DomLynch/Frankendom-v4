using System.IO;
using UnityEngine;

// Slow, small flicker of the moon-crack emission (Goose QC: dark and quiet beats loud). Data: Assets/Data/crack_flicker.json (StreamingAssets in the player).
// Multiplies the base _EmissiveColor of the materials named in 'materials' by 1 + amplitude * (sin of two slow frequencies); hzA/hzB in Hz. Also applies QualitySettings.lodBias from 'lodBias' (> 0).
public class CrackFlicker : MonoBehaviour
{
    [System.Serializable] class Row { public bool enabled = true; public float hzA = 0.27f, hzB = 0.41f, amplitude = 0.1f, lodBias = 2f; public string[] materials = { "M_WorldTerrain" }; }
    public static float BaseLodBias;   // TIERS v96: the data lodBias (approved look); QualityTiers.ApplyLod multiplies it by the tier's lodBiasMul after every tier change
    Row row; Material[] mats; Color[] baseCol; static readonly int Em = Shader.PropertyToID("_EmissiveColor");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        Row r = new Row();
        foreach (var p in new[] { Application.streamingAssetsPath + "/crack_flicker.json", Application.dataPath + "/Data/crack_flicker.json" })
        {
            if (!File.Exists(p)) continue;
            try { r = JsonUtility.FromJson<Row>(File.ReadAllText(p)); } catch { }
            break;
        }
        if (r.lodBias > 0f) { BaseLodBias = r.lodBias; QualitySettings.lodBias = r.lodBias * QualityTiers.LodBiasMul; }   // TIERS v96: x1 on Ultra
        if (!r.enabled) return;
        var go = new GameObject("CrackFlicker"); DontDestroyOnLoad(go); var cf = go.AddComponent<CrackFlicker>(); cf.row = r;
    }

    // PERF3 v100 D1 / D1b: D1 = write only when the common multiplier k moved (perf_fixes.json D1.epsilonEmissiveRel); D1b = per-renderer MaterialPropertyBlock instead of the shared Material.SetColor
    // (note: a renderer with a property block is not SRP-Batcher compatible; the terrain has few renderers, but A/B it: see PERFBENCH_AB_PLAN.md).
    bool guard, useMpb; float lastK = float.NaN, epsK; Renderer[] tRend; int[] tIdx, tMat; MaterialPropertyBlock mpb;

    void Start()
    {
        var list = new System.Collections.Generic.List<Material>();
        foreach (var rr in FindObjectsByType<Renderer>(FindObjectsSortMode.None)) foreach (var m in rr.sharedMaterials) if (m != null && !list.Contains(m) && m.HasProperty(Em)) foreach (var n in row.materials) if (m.name.Contains(n)) list.Add(m);
        mats = list.ToArray(); baseCol = new Color[mats.Length]; for (int i = 0; i < mats.Length; i++) baseCol[i] = mats[i].GetColor(Em);
        guard = PerfFixes.On("D1"); epsK = FlickerGuard.EpsEmissive; useMpb = PerfFixes.On("D1b");
        if (useMpb)
        {
            var rl = new System.Collections.Generic.List<Renderer>(); var il = new System.Collections.Generic.List<int>(); var ml = new System.Collections.Generic.List<int>();
            foreach (var rr in FindObjectsByType<Renderer>(FindObjectsSortMode.None)) { var sm = rr.sharedMaterials; for (int j = 0; j < sm.Length; j++) { int mi = System.Array.IndexOf(mats, sm[j]); if (mi >= 0) { rl.Add(rr); il.Add(j); ml.Add(mi); } } }
            tRend = rl.ToArray(); tIdx = il.ToArray(); tMat = ml.ToArray(); mpb = new MaterialPropertyBlock();
        }
    }

    void Update()
    {
        if (mats == null) return;
        float t = Time.unscaledTime, k = 1f + row.amplitude * 0.5f * (Mathf.Sin(t * row.hzA * 6.2832f) + Mathf.Sin(t * row.hzB * 6.2832f + 1.3f));
        if (guard && !float.IsNaN(lastK) && !FlickerGuard.Moved(lastK, k, 1f, epsK)) return;   // PERF3 v100 D1: write only on change
        lastK = k;
        if (useMpb)   // PERF3 v100 D1b
        {
            for (int i = 0; i < tRend.Length; i++) { if (tRend[i] == null) continue; mpb.Clear(); mpb.SetColor(Em, baseCol[tMat[i]] * k); tRend[i].SetPropertyBlock(mpb, tIdx[i]); }
            return;
        }
        for (int i = 0; i < mats.Length; i++) if (mats[i] != null) mats[i].SetColor(Em, baseCol[i] * k);
    }
}

using System.IO;
using UnityEngine;

// Slow, small flicker of the moon-crack emission (Goose QC: dark and quiet beats loud). Data: Assets/Data/crack_flicker.json (StreamingAssets in the player).
// Multiplies the base _EmissiveColor of the materials named in 'materials' by 1 + amplitude * (sin of two slow frequencies); hzA/hzB in Hz. Also applies QualitySettings.lodBias from 'lodBias' (> 0).
public class CrackFlicker : MonoBehaviour
{
    [System.Serializable] class Row { public bool enabled = true; public float hzA = 0.27f, hzB = 0.41f, amplitude = 0.1f, lodBias = 2f; public string[] materials = { "M_WorldTerrain" }; }
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
        if (r.lodBias > 0f) QualitySettings.lodBias = r.lodBias;
        if (!r.enabled) return;
        var go = new GameObject("CrackFlicker"); DontDestroyOnLoad(go); var cf = go.AddComponent<CrackFlicker>(); cf.row = r;
    }

    void Start()
    {
        var list = new System.Collections.Generic.List<Material>();
        foreach (var rr in FindObjectsByType<Renderer>(FindObjectsSortMode.None)) foreach (var m in rr.sharedMaterials) if (m != null && !list.Contains(m) && m.HasProperty(Em)) foreach (var n in row.materials) if (m.name.Contains(n)) list.Add(m);
        mats = list.ToArray(); baseCol = new Color[mats.Length]; for (int i = 0; i < mats.Length; i++) baseCol[i] = mats[i].GetColor(Em);
    }

    void Update()
    {
        if (mats == null) return;
        float t = Time.unscaledTime, k = 1f + row.amplitude * 0.5f * (Mathf.Sin(t * row.hzA * 6.2832f) + Mathf.Sin(t * row.hzB * 6.2832f + 1.3f));
        for (int i = 0; i < mats.Length; i++) if (mats[i] != null) mats[i].SetColor(Em, baseCol[i] * k);
    }
}

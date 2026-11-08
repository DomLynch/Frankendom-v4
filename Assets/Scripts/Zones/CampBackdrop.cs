using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// TRADEFIN v1: the camp's 2D backdrop cards drew black. Their material (emissive-only, base colour black) was made from the glb with _EmissiveColor = (1,1,1), which HDRP reads as 1 nit; at the project's fixed
// exposure (look.json ev 9.4) 1 nit is about 0.001 of white. This sets the emission from data (camp_backdrop.json emissiveNits) on PRIVATE COPIES of the materials of the rows under rootName
// (the shared .mat assets are never written, so a re-run of the glb material conversion cannot undo it). Dark: only with the zones on (ZoneSave.Active), only when camp_backdrop.json enabled. No fog or light is touched.
[Serializable] public class CampBackdropCard { public string match = ""; public float gain = 1f; }   // CAMPFIX v1: per-card emission gain (the a|b seam of the entry view stepped 1.2 to 1.7x)
[Serializable] public class CampBackdropCap { public bool enabled = false; public float halfWidthM = 31.84f, fromYM = 99.9f, heightM = 240f; }   // CAMPFIX v1: the black cap above each card (card local metres)
[Serializable] public class CampBackdropData { public bool enabled = true, log = true; public string rootName = "HubZone"; public string[] rootNames = new string[0]; public string[] match = new string[] { "M_Camp_backdrop" }; public float emissiveNits = 400f; public int waitFrames = 20; public CampBackdropCard[] cards = new CampBackdropCard[0]; public CampBackdropCap cap = new CampBackdropCap(); }

public sealed class CampBackdrop : MonoBehaviour
{
    static CampBackdrop inst; CampBackdropData D; readonly Dictionary<Material, Material> copies = new Dictionary<Material, Material>();
    static readonly int Em = Shader.PropertyToID("_EmissiveColor");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null || !ZoneSave.Active) return;
        var d = new CampBackdropData(); string txt = DataPath.ReadText("camp_backdrop.json");
        if (txt != null) { try { var k = JsonUtility.FromJson<CampBackdropData>(txt); if (k != null) d = k; } catch (Exception e) { Debug.LogWarning("[CampBackdrop] camp_backdrop.json unreadable, backdrop stays as built: " + e.Message); return; } }
        if (!d.enabled || d.match == null || d.match.Length == 0) return;
        var go = new GameObject("CampBackdrop"); DontDestroyOnLoad(go); inst = go.AddComponent<CampBackdrop>(); inst.D = d;
    }
    void OnEnable() { SceneManager.sceneLoaded += OnLoaded; }
    void OnDisable() { SceneManager.sceneLoaded -= OnLoaded; }
    void Start() { StartCoroutine(Run()); }
    void OnLoaded(Scene s, LoadSceneMode m) { if (m == LoadSceneMode.Single) { copies.Clear(); StartCoroutine(Run()); } }

    IEnumerator Run()
    {
        for (int i = 0; i < Mathf.Max(1, D.waitFrames); i++) yield return null;
        var roots = new List<GameObject>();   // MARKET2 v1: rootName first (as before), then every rootNames[] entry that exists
        var main = GameObject.Find(D.rootName); if (main != null) roots.Add(main); else if (D.log) Debug.LogWarning("[CampBackdrop] no object '" + D.rootName + "' in the scene (camp not built?)");
        if (D.rootNames != null) foreach (var nm in D.rootNames) { if (string.IsNullOrEmpty(nm) || nm == D.rootName) continue; var g2 = GameObject.Find(nm); if (g2 != null) roots.Add(g2); else if (D.log) Debug.LogWarning("[CampBackdrop] no object '" + nm + "' in the scene (zone not built?)"); }
        if (roots.Count == 0) yield break;
        float n = Mathf.Max(0f, D.emissiveNits); int rends = 0, mats = 0; var capRoots = new Dictionary<Transform, Material>();
        foreach (var root in roots) foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            var sm = r.sharedMaterials; bool ch = false;
            for (int i = 0; i < sm.Length; i++)
            {
                var m = sm[i]; if (m == null || !Matches(m.name) || !m.HasProperty(Em)) continue;
                Material c; if (!copies.TryGetValue(m, out c) || c == null) { c = new Material(m); c.name = m.name + " (CampBackdrop)"; float gk = GainFor(m.name) /* CAMPFIX v1 */; c.SetColor(Em, new Color(n * gk, n * gk, n * gk, 1f)); copies[m] = c; mats++; }
                sm[i] = c; ch = true; if (D.cap != null && D.cap.enabled) { var lg = r.GetComponentInParent<LODGroup>(); var rt = lg != null ? lg.transform : r.transform; if (!capRoots.ContainsKey(rt)) capRoots[rt] = c; }
            }
            if (ch) { r.sharedMaterials = sm; rends++; }
        }
        MakeCaps(capRoots);
        if (D.log) Debug.Log("[CampBackdrop] emission " + n.ToString("F0") + " nits on " + rends + " renderers, " + mats + " material copies (root " + D.rootName + (roots.Count > 1 ? " +" + (roots.Count - 1) + " more" : "") + ")");
    }
    // CAMPFIX v1: the card top edge showed as a hard line over the HDRI sky. The top 12 % of each card texture is faded to black (the glbs), and this puts one plain black quad above each card (same plane, child of the card row,
    // so it wakes / retires with its ring) so the HDRI eclipse glow and stars above the card are covered. Material = a copy of the card's own copy with the emission black (same shader, never pink). No light, no shadow.
    void MakeCaps(Dictionary<Transform, Material> roots)
    {
        if (D.cap == null || !D.cap.enabled || roots.Count == 0) return; int made = 0;
        float w = Mathf.Max(1f, D.cap.halfWidthM), y0 = Mathf.Max(0f, D.cap.fromYM), y1 = y0 + Mathf.Max(1f, D.cap.heightM);
        foreach (var kv in roots)
        {
            var rt = kv.Key; if (rt == null || kv.Value == null || rt.Find("CampBackdropCap") != null) continue;
            var m = new Material(kv.Value); m.name = "CampBackdropCap"; m.SetColor(Em, Color.black); if (m.HasProperty("_EmissiveColorMap")) m.SetTexture("_EmissiveColorMap", null);
            var mesh = new Mesh { name = "CampBackdropCap" };
            mesh.vertices = new[] { new Vector3(-w, y0, 0f), new Vector3(w, y0, 0f), new Vector3(w, y1, 0f), new Vector3(-w, y1, 0f) };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward }; mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };   // both windings: visible from whichever side the card's normal ended up
            mesh.RecalculateBounds();
            var go = new GameObject("CampBackdropCap"); go.transform.SetParent(rt, false); go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = m; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false; made++;
        }
        if (D.log) Debug.Log("[CampBackdrop] " + made + " black cap(s) above the cards (" + D.cap.heightM.ToString("F0") + " m)");
    }
    float GainFor(string name) { if (D.cards != null) foreach (var k in D.cards) if (k != null && !string.IsNullOrEmpty(k.match) && name.Contains(k.match)) return Mathf.Max(0f, k.gain); return 1f; }   // CAMPFIX v1
    bool Matches(string name) { if (name.Contains("(CampBackdrop)")) return false; foreach (var s in D.match) if (!string.IsNullOrEmpty(s) && name.Contains(s)) return true; return false; }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// VENDORREVEAL v1 (laneVENDORREVEAL, Dom's V21 idea): a camp vendor's face goes see-through for a brief glimpse and a skull shows behind it when the player is close
// AND (an eclipse flare is on OR the camera is not looking straight at the face). Vendor stays fully functional, non-hostile, silent.
// DARK by default: vendor_reveal.json absent, "enabled": false or -vendorreveal:0 = this class builds NOTHING (no object, no allocation, no per-frame work).
// Switch: vendor_reveal.json "enabled", or same-exe A/B -vendorreveal:0|1 (harness builds only, FRANKEN_NO_HARNESS ignores args); 2 = proof (distance only, no eclipse/peripheral test), 3 = proof hold (peak while in range).
// How: the vendor GLB is the _reveal.glb variant (Body mesh = 2 submeshes, 2 identical materials, made offline by make_split_glb.py), so the face is its own submesh with its own material.
// Idle cost: one distance test per vendor every pollSeconds, nothing else. During a glimpse: the face submesh material is swapped for a fade copy (+0 draw calls, the submesh already draws) and a small skull
// (primitive parts or a GLB) is switched on under the head bone. Every number comes from vendor_reveal.json (data-engine rule).
[Serializable] public class VrPart { public string shape = "sphere"; public float[] pos = new float[3], scale = new float[3], color = new float[3]; public float smoothness = 0.3f; }
[Serializable] public class VrSkull { public string mode = "proxy"; public string glb = ""; public float[] glbScale = new float[3]; public VrPart[] parts = new VrPart[0]; public string shader = "HDRP/Lit", colorProp = "_BaseColor", smoothProp = "_Smoothness"; public string[] shaderFallbacks = new string[0]; }
[Serializable] public class VrFade
{
    public string mode = "transparent";                       // "transparent" (HDRP Lit alpha blend, soft) | "clip" (alpha clip, hard edge at clipCutoff, robust fallback)
    public int renderQueue = 3000; public float clipCutoff = 0.5f; public float[] alphaBlend = new float[0]; public string[] blendProps = new string[0];   // alphaBlend[i] is written to blendProps[i] (SrcBlend, DstBlend, AlphaSrcBlend, AlphaDstBlend as UnityEngine.Rendering.BlendMode ints)
    public string[] disablePasses = new string[0]; public string[] keywordsOn = new string[0]; public string[] keywordsOff = new string[0];
    public string colorProp = "_BaseColor", surfaceProp = "_SurfaceType", zWriteProp = "_ZWrite", cutoffProp = "_AlphaCutoff", cutoffEnableProp = "_AlphaCutoffEnable", blendModeProp = "_BlendMode", renderTypeTag = "RenderType", renderTypeValue = "Transparent";
}
[Serializable] public class VrRow
{
    public string npcId = ""; public bool enabled = true; public string headBone = "mixamorig:Head"; public int faceSubmesh = 1;
    public float triggerM = 3f, peripheralDeg = 25f, maxPeripheralDeg = 75f, minFacing = 0.2f, revealSeconds = 1.5f, easeInFrac = 0.3f, easeOutFrac = 0.4f, cooldownSeconds = 45f, firstDelaySeconds = 5f, skullYawDeg = 0f, skullScale = 1f;
    public bool useEclipse = true, usePeripheral = true;
    public float[] faceCenterLocal = new float[3], faceForwardLocal = new float[3], skullSeatLocal = new float[3];
}
[Serializable] public class VrData
{
    public bool enabled = false, log = true; public float pollSeconds = 0.2f, rescanSeconds = 1f; public string distanceFrom = "player";   // "player" | "camera"
    public string eclipseType = "EclipseAlive", eclipseProp = "FlareActive"; public float minAmount = 0.001f;
    public VrFade fade = new VrFade(); public VrSkull skull = new VrSkull(); public VrRow[] vendors = new VrRow[0];
}

public sealed class VendorReveal : MonoBehaviour
{
    sealed class V
    {
        public VrRow row; public GameObject npc; public Transform head; public SkinnedMeshRenderer smr; public Material[] baseMats; public Material fadeMat; public GameObject skull;
        public bool live; public float t0, lastEnd, nextOk; public bool swapped;
    }
    const float SmoothA = 3f, SmoothB = 2f;
    const int ProofCamMode = 3;   // -vendorreveal:3 = stills proof (ZoneShot places only a camera, so distance is measured from the camera)
    const string ArgOn = "-vendorreveal:", ArgOffSuffix = ":0";
    static VrData D; static VendorReveal inst; static int mode = -1;
    readonly List<V> vs = new List<V>(); float nextPoll, nextScan; PropertyInfo eclProp; bool eclTried; Shader skullShader;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; D = null; mode = -1; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null) return;
        int force = -1;
#if !FRANKEN_NO_HARNESS
        foreach (var a in Environment.GetCommandLineArgs())
            if (a.StartsWith(ArgOn, StringComparison.OrdinalIgnoreCase)) { int v; force = int.TryParse(a.Substring(ArgOn.Length), out v) ? v : 1; }
#endif
        if (force == 0) return;
        string txt = DataPath.ReadText("vendor_reveal.json"); if (txt == null) return;
        VrData d; try { d = JsonUtility.FromJson<VrData>(txt); } catch (Exception e) { Debug.LogWarning("[VendorReveal] vendor_reveal.json unreadable, faces stay solid: " + e.Message); return; }
        if (d == null || (!d.enabled && force < 1) || d.vendors == null || d.vendors.Length == 0) return;
        D = d; mode = force; var go = new GameObject("VendorReveal"); DontDestroyOnLoad(go); inst = go.AddComponent<VendorReveal>();
        if (D.log) Debug.Log("[VendorReveal] on: " + D.vendors.Length + " vendor rows, fade " + D.fade.mode + ", skull " + D.skull.mode + ", mode arg " + force);
    }

    static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (SmoothA - SmoothB * t); }
    static Vector3 V3(float[] a) { return a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero; }
    static Color Col(float[] a) { return a != null && a.Length >= 3 ? new Color(a[0], a[1], a[2], 1f) : Color.white; }
    static Transform FindDeep(Transform t, string n) { if (t.name == n) return t; for (int i = 0; i < t.childCount; i++) { var r = FindDeep(t.GetChild(i), n); if (r != null) return r; } return null; }

    bool Eclipse()
    {
        if (!eclTried) { eclTried = true; var ty = Type.GetType(D.eclipseType); if (ty != null) eclProp = ty.GetProperty(D.eclipseProp, BindingFlags.Public | BindingFlags.Static); if (eclProp == null && D.log) Debug.Log("[VendorReveal] eclipse hook not present (" + D.eclipseType + "." + D.eclipseProp + "): the eclipse trigger stays off, peripheral still works"); }
        if (eclProp == null) return false; try { return (bool)eclProp.GetValue(null, null); } catch (Exception) { return false; }
    }

    void Bind(V v)
    {
        var go = HubNpcs.TryGet(v.row.npcId);
        if (go == v.npc) return;
        Release(v); v.npc = go; if (go == null) return;
        v.head = FindDeep(go.transform, v.row.headBone); v.smr = go.GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (v.head == null || v.smr == null || v.smr.sharedMaterials.Length <= v.row.faceSubmesh) { if (D.log) Debug.LogWarning("[VendorReveal] " + v.row.npcId + ": head bone / face submesh missing (is the vendor GLB the _reveal.glb split?), no reveal for this vendor"); v.npc = null; v.head = null; v.smr = null; return; }
        v.baseMats = v.smr.sharedMaterials; v.nextOk = Time.time + v.row.firstDelaySeconds;
    }

    void Release(V v) { if (v.fadeMat != null) Destroy(v.fadeMat); v.fadeMat = null; if (v.skull != null) Destroy(v.skull); v.skull = null; v.live = false; v.swapped = false; v.head = null; v.smr = null; v.baseMats = null; }

    Material MakeFade(Material src)
    {
        var F = D.fade; var m = new Material(src); m.name = src.name + "_vrfade";
        if (F.mode == "clip")
        {
            if (m.HasProperty(F.cutoffEnableProp)) m.SetFloat(F.cutoffEnableProp, 1f); if (m.HasProperty(F.cutoffProp)) m.SetFloat(F.cutoffProp, F.clipCutoff);
        }
        else
        {
            if (m.HasProperty(F.surfaceProp)) m.SetFloat(F.surfaceProp, 1f); if (m.HasProperty(F.blendModeProp)) m.SetFloat(F.blendModeProp, 0f); if (m.HasProperty(F.zWriteProp)) m.SetFloat(F.zWriteProp, 0f);
            if (F.alphaBlend != null) for (int i = 0; i < F.blendProps.Length && i < F.alphaBlend.Length; i++) if (m.HasProperty(F.blendProps[i])) m.SetFloat(F.blendProps[i], F.alphaBlend[i]);
            m.renderQueue = F.renderQueue; m.SetOverrideTag(F.renderTypeTag, F.renderTypeValue);
        }
        foreach (var k in F.keywordsOn) m.EnableKeyword(k); foreach (var k in F.keywordsOff) m.DisableKeyword(k);
        if (F.mode != "clip") foreach (var p in F.disablePasses) m.SetShaderPassEnabled(p, false);
        return m;
    }

    void SetFace(V v, float amount)
    {
        var F = D.fade; var c = v.fadeMat.GetColor(F.colorProp); float keep = 1f - amount;
        c.a = F.mode == "clip" ? (keep > F.clipCutoff ? 1f : 0f) : keep;   // clip: alpha below clipCutoff is discarded, so the face is gone once keep crosses it (hard edge, no blending)
        v.fadeMat.SetColor(F.colorProp, c);
    }

    void Swap(V v, bool fade)
    {
        if (v.swapped == fade || v.smr == null) return;
        var arr = v.smr.sharedMaterials; arr[v.row.faceSubmesh] = fade ? v.fadeMat : v.baseMats[v.row.faceSubmesh]; v.smr.sharedMaterials = arr; v.swapped = fade;
    }

    Shader SkullShader() { if (skullShader != null) return skullShader; var S = D.skull; skullShader = Shader.Find(S.shader); for (int i = 0; skullShader == null && i < S.shaderFallbacks.Length; i++) skullShader = Shader.Find(S.shaderFallbacks[i]); return skullShader; }

    GameObject BuildSkull(V v)
    {
        var root = new GameObject("vendor_reveal_skull"); root.transform.SetParent(v.head, false);
        root.transform.localPosition = V3(v.row.skullSeatLocal); root.transform.localRotation = Quaternion.Euler(0f, v.row.skullYawDeg, 0f); root.transform.localScale = Vector3.one * v.row.skullScale;
        var sh = SkullShader();
        foreach (var p in D.skull.parts)
        {
            var pt = p.shape == "cube" ? PrimitiveType.Cube : p.shape == "capsule" ? PrimitiveType.Capsule : PrimitiveType.Sphere;
            var g = GameObject.CreatePrimitive(pt); var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);
            g.transform.SetParent(root.transform, false); g.transform.localPosition = V3(p.pos); g.transform.localScale = V3(p.scale);
            var r = g.GetComponent<Renderer>(); r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            if (sh != null) { var m = new Material(sh); if (m.HasProperty(D.skull.colorProp)) m.SetColor(D.skull.colorProp, Col(p.color)); if (m.HasProperty(D.skull.smoothProp)) m.SetFloat(D.skull.smoothProp, p.smoothness); r.sharedMaterial = m; }
        }
        root.SetActive(false); return root;
    }

    void Update()
    {
        float now = Time.time;
        if (now >= nextScan) { nextScan = now + D.rescanSeconds; if (vs.Count == 0) foreach (var r in D.vendors) if (r != null && r.enabled && !string.IsNullOrEmpty(r.npcId)) vs.Add(new V { row = r }); foreach (var v in vs) Bind(v); }
        bool polled = now >= nextPoll; if (polled) nextPoll = now + D.pollSeconds;
        var cam = Camera.main; if (cam == null) return;
        Vector3 pp = cam.transform.position; if (mode != ProofCamMode && D.distanceFrom == "player" && GameLoop.Player != null) pp = GameLoop.Player.transform.position;
        for (int i = 0; i < vs.Count; i++)
        {
            var v = vs[i]; if (v.npc == null || v.head == null) continue;
            var R = v.row;
            if (!v.live)
            {
                if (!polled || now < v.nextOk || !v.npc.activeInHierarchy) continue;
                Vector3 fc = v.head.TransformPoint(V3(R.faceCenterLocal)); if ((fc - pp).sqrMagnitude > R.triggerM * R.triggerM) continue;
                bool ok;
                if (mode >= 2) ok = true;
                else
                {
                    Vector3 to = fc - cam.transform.position; float ang = Vector3.Angle(cam.transform.forward, to);
                    Vector3 fw = v.head.TransformDirection(V3(R.faceForwardLocal)).normalized; bool facing = Vector3.Dot(fw, -to.normalized) >= R.minFacing;
                    bool periph = R.usePeripheral && ang >= R.peripheralDeg && ang <= R.maxPeripheralDeg; bool ecl = R.useEclipse && Eclipse();
                    ok = facing && (periph || ecl);
                }
                if (!ok) continue;
                if (v.fadeMat == null) v.fadeMat = MakeFade(v.baseMats[R.faceSubmesh]); if (v.skull == null) v.skull = BuildSkull(v);
                v.live = true; v.t0 = now; if (D.log) Debug.Log("[VendorReveal] glimpse " + R.npcId + " at " + Mathf.Sqrt((fc - pp).sqrMagnitude).ToString("0.0") + " m");
            }
            if (v.live)
            {
                float p = Mathf.Max(R.revealSeconds, Mathf.Epsilon); p = (now - v.t0) / p;
                float a = Smooth(p / Mathf.Max(R.easeInFrac, Mathf.Epsilon)) * (1f - Smooth((p - (1f - R.easeOutFrac)) / Mathf.Max(R.easeOutFrac, Mathf.Epsilon)));
                if (mode == 3 && p >= R.easeInFrac) { Vector3 fc2 = v.head.TransformPoint(V3(R.faceCenterLocal)); if ((fc2 - pp).sqrMagnitude <= R.triggerM * R.triggerM) { a = 1f; v.t0 = now - R.easeInFrac * R.revealSeconds; } }
                if (p >= 1f || !v.npc.activeInHierarchy) { Swap(v, false); if (v.skull != null) v.skull.SetActive(false); v.live = false; v.lastEnd = now; v.nextOk = now + R.cooldownSeconds; continue; }
                bool vis = a > D.minAmount; Swap(v, vis); if (vis) SetFace(v, a); if (v.skull != null && v.skull.activeSelf != vis) v.skull.SetActive(vis);
            }
        }
    }

    void OnDestroy() { foreach (var v in vs) Release(v); if (inst == this) inst = null; }
}

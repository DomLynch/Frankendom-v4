using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// laneV14 v1 (STAGED for Assets/Scripts/Zones/ZoneCharSep.cs; NEVER COMPILED, written CPU-only: unity-dev2 must compile it and fix typos / HDRP 6.6 light API names).
// What it is: per-zone character separation from DATA (character_sep.json, StreamingAssets first via DataPath, same rule as ZoneLook). It adds NO pass: it re-colours and re-scales the existing
// characters-only lights by name (Rim_Player, Fill_Player, Rim_Enemy, Rim_Character) per zone, blended by the player's world z with the zone rows of look_zones.json (same ids, same blendM),
// and can add ONE shadowed spot (characters light layers only, off the lane).
// Why: V2 showed skin/hair strengths change nothing; the characters are the same hue as the zone grade (CPU measurement, laneV14\proof: boss/background sector dE 4.7-6.4 vs Joan 14-23).
// A complementary-colour rim is a hue AND a luminance edge.
// Cost: zero extra lights for the rim path (same spots, other colour/intensity); the optional Sep_Shadow is one shadowed spot (fps arm V14SHADOW).
// Switches: json enabled / shadowLight.enabled; command line -charsep:off | -charsep:scale=1.5 | -charsep:noshadow. Everything off = nothing touched (lights keep their authored values).
[Serializable] public class CharSepShadow { public bool colorFromRim = true; public float lux = 900f, sideM = 8f, heightM = 4.5f, backM = 3f, spotAngle = 70f, range = 24f; public int shadowRes = 512; }
[Serializable] public class CharSepRow { public string id = ""; public float[] rimColor, enemyRimColor, fillColor, heroRimColor; public float playerRimMul = 1f, enemyRimMul = 1f, fillMul = 1f, heroRimMul = 1f; public CharSepShadow shadow = new CharSepShadow(); }
[Serializable] public class CharSepShadowSw { public bool enabled; public string name = "Sep_Shadow"; }
[Serializable] public class CharSepData { public bool enabled, log = true; public float pollSeconds = 0.1f, smoothSeconds = 1.5f, rimSat = 0.62f; public CharSepShadowSw shadowLight = new CharSepShadowSw(); public CharSepRow[] rows; }

public sealed class ZoneCharSep : MonoBehaviour
{
    class L { public Light l; public HDAdditionalLightData hd; public float baseInt; }
    static ZoneCharSep inst;
    CharSepData D; ZoneLookData Z; float scale = 1f; bool allowShadow = true;
    readonly L rimP = new L(), fillP = new L(), rimE = new L(), hero = new L();
    Color curRim, curEnemy, curFill, curHero; float mP, mE, mF, mH; bool init; float nextFind, nextLog;
    Light shadowL; HDAdditionalLightData shadowHd;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null || !ZoneSave.Active) return;
        float sc = 1f; bool noShadow = false, off = false;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (!a.StartsWith("-charsep:", StringComparison.OrdinalIgnoreCase)) continue; var v = a.Substring(9).Trim();
            if (v == "off" || v == "0") off = true; else if (v == "noshadow") noShadow = true;
            else if (v.StartsWith("scale=")) float.TryParse(v.Substring(6), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out sc);
        }
        if (off) return;
        string txt = DataPath.ReadText("character_sep.json"), zt = DataPath.ReadText("look_zones.json"); if (txt == null || zt == null) return;
        CharSepData d; ZoneLookData z;
        try { d = JsonUtility.FromJson<CharSepData>(txt); z = JsonUtility.FromJson<ZoneLookData>(zt); } catch (Exception e) { Debug.LogWarning("[ZoneCharSep] json unreadable: " + e.Message); return; }
        if (d == null || z == null || !d.enabled || d.rows == null || z.rows == null || z.rows.Length == 0) return;
        var go = new GameObject("ZoneCharSep"); DontDestroyOnLoad(go); inst = go.AddComponent<ZoneCharSep>(); inst.D = d; inst.Z = z; inst.scale = sc; inst.allowShadow = !noShadow;
    }

    static Color C(float[] a) { return a != null && a.Length >= 3 ? new Color(a[0], a[1], a[2], 1f) : Color.white; }
    static void Grab(L r, string name, Light[] all)
    {
        r.l = null; r.hd = null;
        foreach (var x in all) if (x != null && x.name == name) { r.l = x; break; }
        if (r.l == null) return;
        r.hd = r.l.GetComponent<HDAdditionalLightData>(); r.baseInt = r.hd != null ? r.hd.intensity : r.l.intensity;
    }
    void FindLights()
    {
        var all = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Grab(rimP, "Rim_Player", all); Grab(fillP, "Fill_Player", all); Grab(rimE, "Rim_Enemy", all); Grab(hero, "Rim_Character", all);
    }
    int RowIdx(float z) { int n = Z.rows.Length, i = 0; for (; i < n - 1; i++) if (z < Z.rows[i].zTo) break; return i; }
    CharSepRow ByID(string id) { foreach (var r in D.rows) if (r != null && r.id == id) return r; return null; }

    // colour only through Light.color with colour temperature off (HDRP multiplies the two otherwise); intensity as authored x mul
    static void Apply(L r, Color col, float mul)
    {
        if (r.l == null) return;
        r.l.useColorTemperature = false; r.l.color = col;
        if (r.hd != null) r.hd.intensity = r.baseInt * mul; else r.l.intensity = r.baseInt * mul;
    }

    void Update()
    {
        var p = GameLoop.Player; bool live = p != null && ZoneRuntime.Owns && ZoneSpan.Live;
        if (!live) return;
        if (Time.unscaledTime >= nextFind && (rimP.l == null || rimE.l == null || hero.l == null)) { nextFind = Time.unscaledTime + 5f; FindLights(); }
        float z = p.transform.position.z, dt = Mathf.Max(0.0001f, Time.unscaledDeltaTime), k = 1f - Mathf.Exp(-dt / Mathf.Max(0.05f, D.smoothSeconds));
        int i = RowIdx(z); float half = Z.blendM * 0.5f, t = 0f; CharSepRow a = ByID(Z.rows[i].id), b = a;
        if (i < Z.rows.Length - 1 && z > Z.rows[i].zTo - half) { b = ByID(Z.rows[i + 1].id); t = Mathf.SmoothStep(0f, 1f, (z - (Z.rows[i].zTo - half)) / Mathf.Max(0.01f, Z.blendM)); }
        else if (i > 0 && z < Z.rows[i].zFrom + half) { a = ByID(Z.rows[i - 1].id); b = ByID(Z.rows[i].id); t = Mathf.SmoothStep(0f, 1f, (z - (Z.rows[i].zFrom - half)) / Mathf.Max(0.01f, Z.blendM)); }
        if (a == null || b == null) return;
        Color tr = Color.Lerp(C(a.rimColor), C(b.rimColor), t), te = Color.Lerp(C(a.enemyRimColor), C(b.enemyRimColor), t), tf = Color.Lerp(C(a.fillColor), C(b.fillColor), t), th = Color.Lerp(C(a.heroRimColor), C(b.heroRimColor), t);
        // scale only the strength above 1: scale=0 -> authored intensity, 1 -> the data, 1.5 -> half again
        float tp = 1f + (Mathf.Lerp(a.playerRimMul, b.playerRimMul, t) - 1f) * scale, tE = 1f + (Mathf.Lerp(a.enemyRimMul, b.enemyRimMul, t) - 1f) * scale;
        float tF = 1f + (Mathf.Lerp(a.fillMul, b.fillMul, t) - 1f) * scale, tH = 1f + (Mathf.Lerp(a.heroRimMul, b.heroRimMul, t) - 1f) * scale;
        if (!init) { curRim = tr; curEnemy = te; curFill = tf; curHero = th; mP = tp; mE = tE; mF = tF; mH = tH; init = true; }
        else { curRim = Color.Lerp(curRim, tr, k); curEnemy = Color.Lerp(curEnemy, te, k); curFill = Color.Lerp(curFill, tf, k); curHero = Color.Lerp(curHero, th, k); mP = Mathf.Lerp(mP, tp, k); mE = Mathf.Lerp(mE, tE, k); mF = Mathf.Lerp(mF, tF, k); mH = Mathf.Lerp(mH, tH, k); }
        Apply(rimP, curRim, mP); Apply(rimE, curEnemy, mE); Apply(fillP, curFill, mF); Apply(hero, curHero, mH);
        if (D.shadowLight.enabled && allowShadow) ShadowSpot(p.transform, a.shadow, curRim); else if (shadowL != null) shadowL.enabled = false;
        if (D.log && Time.unscaledTime >= nextLog)
        {
            nextLog = Time.unscaledTime + 10f;
            Debug.Log("[ZoneCharSep] z=" + z.ToString("F1") + " zone=" + Z.rows[i].id + " rim=" + curRim + " x" + mP.ToString("F2") + " enemy x" + mE.ToString("F2") + " fill x" + mF.ToString("F2") + " hero x" + mH.ToString("F2") + " shadow=" + (shadowL != null && shadowL.enabled));
        }
    }

    // the one optional shadowed light: off the lane to the side and a little behind, aimed at the player-to-enemy midpoint, characters only (light layers copied from the rim lights)
    void ShadowSpot(Transform player, CharSepShadow s, Color rim)
    {
        if (shadowL == null)
        {
            var g = new GameObject(D.shadowLight.name); g.transform.SetParent(transform, false); shadowL = g.AddComponent<Light>(); shadowL.type = LightType.Spot;
            shadowHd = g.AddComponent<HDAdditionalLightData>(); shadowL.shadows = LightShadows.Soft; shadowHd.SetShadowResolution(s.shadowRes); shadowHd.volumetricDimmer = 0f; shadowL.useColorTemperature = false;
            if (rimP.hd != null) shadowHd.lightlayersMask = rimE.hd != null ? (rimP.hd.lightlayersMask | rimE.hd.lightlayersMask) : rimP.hd.lightlayersMask;   // characters only
        }
        Transform foe = null; var eg = GameObject.Find(Roster.Get(RimRig.ResolveId("enemy")).objectName); if (eg != null) foe = eg.transform;
        Vector3 mid = (foe != null ? (player.position + foe.position) * 0.5f : player.position) + Vector3.up * 1.3f;
        shadowL.transform.position = mid + new Vector3(s.sideM, s.heightM, -s.backM);
        shadowL.transform.rotation = Quaternion.LookRotation((mid - shadowL.transform.position).normalized, Vector3.up);
        shadowL.spotAngle = s.spotAngle; shadowL.range = s.range; shadowL.color = s.colorFromRim ? rim : Color.white; shadowL.enabled = true;
        shadowHd.intensity = s.lux * (s.range * 0.5f) * (s.range * 0.5f);   // lux at half range expressed in candela (spot unit), the way HdrpFixes sizes the rim spots
    }
}

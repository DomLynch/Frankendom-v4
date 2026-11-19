using System;
using UnityEngine;

// TAKE C6: rim_rig.json (StreamingAssets, then Assets/Data). keyFollow=false (default) = the old placement exactly. true = the rim sits on the key light side of the character (never from behind the camera, no ring that turns with the camera).
public static class RimRigCfg
{
    [Serializable] public class Row { public bool keyFollow = false, log = true; public string keyLight = "Key_ColdSun"; public float minAwayDeg = 100f, followRate = 6f, runMul = 1f, aboveMul = 1f; }
    static Row cfg;
    public static Row Get()
    {
        if (cfg != null) return cfg; cfg = new Row();
        foreach (var p in new[] { Application.streamingAssetsPath + "/rim_rig.json", Application.dataPath + "/Data/rim_rig.json" })
        {
            if (!System.IO.File.Exists(p)) continue;
            try { var r = JsonUtility.FromJson<Row>(DataPath.ReadFile(p)); if (r != null) { cfg = r; if (string.IsNullOrEmpty(cfg.keyLight)) cfg.keyLight = "Key_ColdSun"; } } catch (Exception e) { Debug.LogWarning("[RimRig] " + p + ": " + e.Message); }
            break;
        }
        Debug.Log("[Take] C6 rim_rig.json read: keyFollow=" + cfg.keyFollow + " keyLight=" + cfg.keyLight + " minAwayDeg=" + cfg.minAwayDeg.ToString("F0"));
        return cfg;
    }
}

// Character rim light that stays behind and above a roster character as seen from the main camera (lane LOOK, fix 2).
// Built by HdrpFixes from hdrp_fixes.json "rimRigs" (who = "player" | "enemy" | a roster id; no character names in code).
// The light only reaches the character's renderers through its light layer (HdrpFixes sets the renderer bits), shadows off.
public class RimRig : MonoBehaviour
{
    public string who = "player";
    public float behind = 2.5f, above = 2.6f, side = 0.6f, aimHeight = 1.4f;
    Transform target; Camera cam; float nextFind; Light key; float nextKey; Vector3 smPos; bool smInit, keyLogged;

    /// <summary>"player" / "enemy" -> the roster id; anything else is taken as a roster id.</summary>
    public static string ResolveId(string w)
    {
        if (string.IsNullOrEmpty(w) || w == "player") return Roster.PlayerId;
        if (w == "enemy") return Roster.EnemyId;
        return w;
    }

    void LateUpdate()
    {
        if (target == null || cam == null)
        {
            if (Time.unscaledTime < nextFind) return; nextFind = Time.unscaledTime + 1f;
            if (target == null) { var g = GameObject.Find(Roster.Get(ResolveId(who)).objectName); if (g != null) target = g.transform; }
            if (cam == null) cam = Camera.main;
            if (target == null || cam == null) return;
        }
        Vector3 fwd = cam.transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) { fwd = target.forward; fwd.y = 0f; }
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        Vector3 aim = target.position + Vector3.up * aimHeight;
        Vector3 pos = target.position + fwd * behind + Vector3.up * above + right * side;   // far side of the character from the camera, raised
        var RC = RimRigCfg.Get();
        if (RC.keyFollow)   // TAKE C6: the rim comes from the key light (Key_ColdSun, driven by ZoneLook): on its side of the character, raised by the same height; never within minAwayDeg of the camera side (that would be a front light, not a rim)
        {
            if (key == null && Time.unscaledTime >= nextKey) { nextKey = Time.unscaledTime + 1f; var g = GameObject.Find(RC.keyLight); if (g != null) key = g.GetComponent<Light>(); }
            if (key != null && key.enabled && key.gameObject.activeInHierarchy && key.intensity > 0f)
            {
                Vector3 toSun = -key.transform.forward; toSun.y = 0f;
                if (toSun.sqrMagnitude > 1e-4f)
                {
                    toSun.Normalize(); Vector3 camSide = -fwd;   // from the character toward the camera
                    float ang = Vector3.Angle(toSun, camSide);
                    if (ang < RC.minAwayDeg) { float sg = Vector3.Cross(camSide, toSun).y >= 0f ? 1f : -1f; toSun = Quaternion.AngleAxis(sg * RC.minAwayDeg, Vector3.up) * camSide; }
                    float run = Mathf.Sqrt(behind * behind + side * side) * RC.runMul;
                    Vector3 want = target.position + toSun * run + Vector3.up * above * RC.aboveMul;
                    if (!smInit) { smPos = want; smInit = true; } else smPos = Vector3.Lerp(smPos, want, 1f - Mathf.Exp(-Mathf.Max(0.01f, RC.followRate) * Time.unscaledDeltaTime));
                    pos = smPos;
                    if (RC.log && !keyLogged) { keyLogged = true; Debug.Log("[Take] C6 rim " + who + " follows " + key.name + ": sun-side " + toSun.ToString("F2") + " angle to camera side " + ang.ToString("F0") + " deg (min " + RC.minAwayDeg.ToString("F0") + ")"); }
                }
            }
        }
        transform.position = pos;
        Vector3 d = aim - pos; if (d.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// DiagFootGround (laneMOUNTFLOAT, UNPROVEN, staged outside Assets). Flag: -footgrounddiag[:startDelayS][:maxHorses]  (no flag = nothing runs).
// For the ridden mount and the nearest ambient horses, once per second for 10 s, logs per hoof: hoof marker world Y (HB_F_*), ground Y under it (nearest non-fighter collider ray)
// and the terrain sampler Y, plus the lowest vertex of the baked skinned mesh (true visible sole). gap = hoofY - groundY: + floats, - sinks.
// Output: foot_ground_diag.json next to Player.log (rewritten each second) and one [FootDiag] line per subject per second in the log.
// Ambient horses only exist in a harness run when the build is started with -ambienthorses (AmbientHorses.Boot returns early otherwise).
public class DiagFootGround : MonoBehaviour
{
    [Serializable] public class Hoof { public string leg; public float hoofY, groundY, terrainY, gap; public bool hit; }
    [Serializable] public class Row { public int t; public float speed, rootY, bodyLocalY, gapMin, gapMean, gapMax, meshMinY, meshGap; public string gait; public bool ikActive, ikDriven; public Hoof[] hooves; }
    [Serializable] public class Series { public string name, kind, rig; public float scaleY; public List<Row> rows = new List<Row>(); }
    [Serializable] public class Report { public string build, note; public float sinkM_ambient, mountSinkM, groundRayUpM; public List<Series> subjects = new List<Series>(); }
    public class Subject { public string name, kind, gait = ""; public Transform root, body; public Transform[] U = new Transform[4], F = new Transform[4]; public SkinnedMeshRenderer smr; public float speed; public bool ik; public int mask = -1; }

    static readonly string[] Legs = { "FL", "FR", "HL", "HR" };
    static readonly RaycastHit[] buf = new RaycastHit[24];
    const int TickMax = 900; float startDelay = 5f, t0 = -1f; int maxHorses = 12, tick; float nextTick; Report rep = new Report(); readonly Dictionary<string, Series> map = new Dictionary<string, Series>(); string path;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (!a.StartsWith("-footgrounddiag", StringComparison.OrdinalIgnoreCase)) continue;
            var g = new GameObject("DiagFootGround"); DontDestroyOnLoad(g); var d = g.AddComponent<DiagFootGround>(); var p = a.Split(new[] { (char)58 });
            if (p.Length > 1) float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d.startDelay);
            if (p.Length > 2) int.TryParse(p[2], out d.maxHorses);
            return;
        }
    }

    void Start()
    {
        string dir; try { dir = Path.GetDirectoryName(Application.consoleLogPath); } catch (Exception) { dir = null; }
        if (string.IsNullOrEmpty(dir)) dir = Application.persistentDataPath; path = Path.Combine(dir, "foot_ground_diag.json");
        var ah = AmbientHorses.Get(); var hb = AmbientHorses.HbGet();
        rep.build = Application.version; rep.sinkM_ambient = ah.sinkM; rep.groundRayUpM = ah.groundRayUpM; rep.mountSinkM = hb != null ? hb.skit.mountSinkM : -1f;
        rep.note = "gap = hoofMarkerY - groundY (+ floats, - sinks). meshGap = lowest baked mesh vertex Y - min groundY under the 4 hooves.";
        Debug.Log("[FootDiag] armed, delay " + startDelay + " s, writes " + path);
    }

    void Update()
    {
        if (tick >= TickMax) return; if (Time.unscaledTime < startDelay) return;
        var list = new List<Subject>(); AddMount(list); AmbientHorses.DiagFG_List(list);
        if (list.Count == 0) { if (Time.unscaledTime > startDelay + 60f) { Debug.Log("[FootDiag] no mount or horse with an HB_ rig after 60 s"); tick = TickMax; } return; }
        if (t0 < 0f) { t0 = Time.unscaledTime; nextTick = t0; }
        if (Time.unscaledTime < nextTick) return; nextTick += 1f;
        var cam = Camera.main; list.Sort((a, b) => Order(a, cam).CompareTo(Order(b, cam))); int nh = 0;
        foreach (var s in list) { if (s.kind == "ambient" && nh++ >= maxHorses) continue; try { Sample(s); } catch (Exception e) { Debug.LogWarning("[FootDiag] " + s.name + " " + e.Message); } }
        tick++; try { File.WriteAllText(path, JsonUtility.ToJson(rep, true)); } catch (Exception e) { Debug.LogWarning("[FootDiag] write failed " + e.Message); }
        if (tick >= TickMax) Debug.Log("[FootDiag] done, " + rep.subjects.Count + " subjects -> " + path);
    }
    static float Order(Subject s, Camera c) { if (s.kind == "mount") return -1f; return c != null && s.root != null ? Vector3.Distance(c.transform.position, s.root.position) : 0f; }

    static Transform Find(Transform r, string n) { if (r == null) return null; foreach (var t in r.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t; return null; }
    static void AddMount(List<Subject> o)
    {
        var mr = MountRider.Inst; if (mr == null || !MountRider.Mounted) return; var T = typeof(MountRider); const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var go = T.GetField("horse", F).GetValue(mr) as GameObject; var hb = T.GetField("hBody", F).GetValue(mr) as Transform; if (go == null || hb == null || !go.activeInHierarchy) return;
        var s = new Subject { name = "MOUNT " + go.name, kind = "mount", root = go.transform, body = hb, speed = (float)T.GetField("speedNow", F).GetValue(mr) };
        var hbody = Find(hb, "HB_Body"); if (hbody == null) Debug.Log("[FootDiag] mount has no HB_ rig (legacy bob only)");
        for (int i = 0; i < 4; i++) { s.U[i] = Find(hb, "HB_U_" + Legs[i]); s.F[i] = Find(hb, "HB_F_" + Legs[i]); }
        if (s.F[0] == null || s.F[1] == null || s.F[2] == null || s.F[3] == null) { Debug.Log("[FootDiag] mount rig bones missing, subject skipped"); return; }
        var c = AmbientHorses.HbGet(); s.ik = hbody != null && c != null && c.enabled && c.gaits != null && c.gaits.Length > 0; s.smr = hb.GetComponentInChildren<SkinnedMeshRenderer>(true); o.Add(s);
    }

    float GroundY(Vector3 p, Subject s, out bool ok)
    {
        ok = false; float best = float.MaxValue, y = 0f; int n = Physics.RaycastNonAlloc(new Vector3(p.x, Mathf.Max(p.y, s.root.position.y) + 2f, p.z), Vector3.down, buf, 10f, s.mask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++) { var c = buf[i].collider; if (c == null || c.transform.IsChildOf(s.root) || c.GetComponentInParent<DuelFighter>() != null) continue; if (c.GetComponentInParent<MountRider>() != null) continue; if (buf[i].distance < best) { best = buf[i].distance; y = buf[i].point.y; ok = true; } }
        return y;
    }

    void Sample(Subject s)
    {
        Series ser; if (!map.TryGetValue(s.name, out ser)) { ser = new Series { name = s.name, kind = s.kind, rig = s.smr != null ? (s.smr.transform.parent != null ? s.smr.transform.parent.name : s.smr.name) : "?", scaleY = s.body != null ? s.body.lossyScale.y : 0f }; map[s.name] = ser; rep.subjects.Add(ser); }
        var row = new Row { t = tick, speed = s.speed, rootY = s.root.position.y, bodyLocalY = s.body != null ? s.body.localPosition.y : 0f, gait = s.gait, ikActive = s.ik, hooves = new Hoof[4] }; float mn = 1e9f, mx = -1e9f, sum = 0f, gmin = 1e9f; int cnt = 0; var ter = Terrain.activeTerrain;
        for (int i = 0; i < 4; i++)
        {
            var hp = s.F[i].position; bool ok; float gy = GroundY(hp, s, out ok); float ty = float.NaN; if (ter != null) ty = ter.SampleHeight(hp) + ter.transform.position.y;
            var h = new Hoof { leg = Legs[i], hoofY = hp.y, groundY = gy, terrainY = ty, hit = ok, gap = ok ? hp.y - gy : float.NaN }; row.hooves[i] = h;
            if (ok) { mn = Mathf.Min(mn, h.gap); mx = Mathf.Max(mx, h.gap); sum += h.gap; cnt++; gmin = Mathf.Min(gmin, gy); }
            if (s.U[i] != null && Quaternion.Angle(s.U[i].localRotation, Quaternion.identity) > 0.05f) row.ikDriven = true;
        }
        row.gapMin = cnt > 0 ? mn : float.NaN; row.gapMax = cnt > 0 ? mx : float.NaN; row.gapMean = cnt > 0 ? sum / cnt : float.NaN;
        if (s.smr != null && cnt > 0) { var m = new Mesh(); s.smr.BakeMesh(m, true); var vs = m.vertices; var M = Matrix4x4.TRS(s.smr.transform.position, s.smr.transform.rotation, Vector3.one); float lo = 1e9f; for (int v = 0; v < vs.Length; v++) { float y = M.MultiplyPoint3x4(vs[v]).y; if (y < lo) lo = y; } Destroy(m); row.meshMinY = lo; row.meshGap = lo - gmin; }
        ser.rows.Add(row);
        Debug.Log("[FootDiag] " + s.name + " t=" + tick + " clk=" + Time.unscaledTime.ToString("F0") + " v=" + s.speed.ToString("F2") + " gait=" + s.gait + " ik=" + s.ik + "/" + row.ikDriven + " gap min/mean/max " + row.gapMin.ToString("F3") + "/" + row.gapMean.ToString("F3") + "/" + row.gapMax.ToString("F3") + " meshGap " + row.meshGap.ToString("F3") + " rootY " + row.rootY.ToString("F2"));
    }
}

// Adds the ambient horse list through the private Horse / HbHorse types (same assembly, no existing file edited).
public sealed partial class AmbientHorses
{
    internal static void DiagFG_List(List<DiagFootGround.Subject> o)
    {
        var C = HBC; var cam = Camera.main; float farM = C != null ? C.move.animFarM : 70f;
        foreach (var kv in hbAll)
        {
            var h = kv.Key; var b = kv.Value; if (h == null || h.go == null || !h.go.activeInHierarchy || h.body == null || b.rigs.Count == 0) continue; var rg = b.rigs[b.rigs.Count - 1]; if (rg.F[0] == null) continue;
            bool far = cam != null && Vector3.Distance(cam.transform.position, h.pos) > farM;
            var s = new DiagFootGround.Subject { name = h.go.name, kind = "ambient", root = h.go.transform, body = h.body, speed = b.v, gait = b.gaitName + "/" + b.st, ik = !far && C != null && C.enabled, smr = rg.smr, mask = K != null ? K.groundMask : -1 };
            for (int i = 0; i < 4; i++) { s.U[i] = rg.U[i]; s.F[i] = rg.F[i]; }
            o.Add(s);
        }
    }
}

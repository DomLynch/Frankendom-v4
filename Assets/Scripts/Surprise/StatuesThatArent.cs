using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// laneS5 v1 "STATUES THAT AREN'T" (surprise mechanic, ships DARK: surprise_statues.json enabled=false). NEVER COMPILED IN UNITY; Roslyn-checked on a scratch copy only.
// What: the stone lion at the camp (props_layout_hub.json row z3h_hub_lion_statue, the PropsLayout wrapper is named after the row id) changes pose / position ONLY while the player's camera cannot see it,
// a little more each visit: data list of steps (head turns toward the camp, paws shift, last step = the plinth is empty). v1 has no attack and no roaming: the unease is the feature.
// A visit = the player walks into the camp circle (enterRadiusM around campCenter) after having been outside leaveRadiusM for leaveSeconds. Visit 1 shows the base pose; every advanceEveryVisits-th visit after that arms ONE step.
// The armed step is applied only when the statue (bounds grown by frustumMarginM, so a glance at the edge of the screen counts as seen) has been outside the camera frustum for unseenSeconds, the camera is at least
// minDistanceM away, and the shared SurpriseGate lets this surprise through (one surprise per zone visit). The step index is saved in <SavePaths.Dir>\<saveFile> (the sandbox dir under -savePath; never the real
// save while RunMode.RealSaveBlocked), and the saved pose is re-applied the moment the camp props exist, before the first frame the camp is drawn.
// Everything numeric is in surprise_statues.json (this file only holds the algorithm). Harness args (compiled out with FRANKEN_NO_HARNESS): -s5:1 / -s5:0 force on / off, -s5step:N shows step N at once (no gate, no save),
// -s5sweep:<seconds> cycles 0..N for a proof clip (no gate, no save).
[Serializable] public class S5Step { public string note = ""; public float towardFrac, yawDeg, pitchDeg, rollDeg, towardM; public float[] offsetM = new float[0]; public bool hide; }
[Serializable] public class S5Target { public string id = ""; public float[] lookAt = new float[0]; public float forwardYawOffsetDeg, advanceEveryVisits = 1f; public S5Step[] steps = new S5Step[0]; }
[Serializable] public class S5Plinth { public bool enabled; public float radiusFrac = 0.5f, heightFrac = 0.12f; public string name = "S5_PlinthStub"; }
[Serializable] public class S5Visibility { public float frustumMarginM = 3f, unseenSeconds = 1.5f, minDistanceM = 6f; }
[Serializable] public class S5Jitter { public float yawDeg, offsetM; }
[Serializable] public class S5Data
{
    public bool enabled, log = true, useGate = true, resetOnVisit; public string zone = "hub_camp", gateKey = "s5_statue", saveFile = "surprise_statues_save.json";
    public int seed; public float pollSeconds = 0.25f, enterRadiusM = 30f, leaveRadiusM = 45f, leaveSeconds = 8f, findRadiusM = 60f;
    public float[] campCenter = new float[0]; public S5Visibility visibility = new S5Visibility(); public S5Jitter jitter = new S5Jitter(); public S5Plinth plinth = new S5Plinth(); public S5Target[] targets = new S5Target[0];
}
[Serializable] public class S5TargetRec { public string id = ""; public int step; }
[Serializable] public class S5SaveRec { public int version = 1, seed, visits; public S5TargetRec[] targets = new S5TargetRec[0]; }

public sealed class StatuesThatArent : MonoBehaviour
{
    sealed class Tgt
    {
        public S5Target row; public GameObject go; public Renderer[] rends; public Vector3 basePos; public Quaternion baseRot; public Bounds baseBounds; public int step, shown = -1; public bool armed; public float unseenSince = -1f; public GameObject proxy;
    }
    static readonly int SaveVersion = 1, LookAtMin = 2, VecN = 3, MaxLogs = 24; const float BothSides = 2f, Half = 0.5f; static readonly string NoteApplied = "applied step ";
    static StatuesThatArent inst; S5Data D; S5SaveRec rec = new S5SaveRec(); readonly List<Tgt> tg = new List<Tgt>(); bool inCamp, forcedMode, dirty; float awayT, next, sweepT; int forcedStep = -1, logged; float sweepS;
    readonly Plane[] planes = new Plane[6];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { inst = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (inst != null) return;
        string txt = DataPath.ReadText("surprise_statues.json"); if (txt == null) return;
        S5Data d; try { d = JsonUtility.FromJson<S5Data>(txt); } catch (Exception e) { Debug.LogWarning("[S5] surprise_statues.json unreadable, no statue steps: " + e.Message); return; }
        if (d == null) return;
        bool on = d.enabled; int fs = -1; float sw = 0f; bool harnessArg = false;
#if !FRANKEN_NO_HARNESS
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a.StartsWith("-s5:", StringComparison.OrdinalIgnoreCase)) { harnessArg = true; on = a.EndsWith(":1", StringComparison.Ordinal); }
            else if (a.StartsWith("-s5step:", StringComparison.OrdinalIgnoreCase)) { int v; if (int.TryParse(a.Substring(a.IndexOf(':') + 1), out v)) { fs = v; harnessArg = true; on = true; } }
            else if (a.StartsWith("-s5sweep:", StringComparison.OrdinalIgnoreCase)) { float v; if (float.TryParse(a.Substring(a.IndexOf(':') + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) { sw = v; harnessArg = true; on = true; } }
        }
#endif
        if (!on || d.targets == null || d.targets.Length == 0) return;
        if (MenuCfg.Harness && !harnessArg) return;   // proof / bot runs never carry the surprise unless they ask for it
        var go = new GameObject("StatuesThatArent"); DontDestroyOnLoad(go); inst = go.AddComponent<StatuesThatArent>(); inst.D = d; inst.forcedStep = fs; inst.sweepS = sw; inst.forcedMode = fs >= 0 || sw > 0f;
    }

    string SavePath { get { return Path.Combine(SavePaths.Dir, D.saveFile); } }
    bool CanSave { get { return !forcedMode && !RunMode.RealSaveBlocked; } }

    void Awake() { SaveSlot.OnErase += OnNewGame; }
    void OnDestroy() { SaveSlot.OnErase -= OnNewGame; if (inst == this) inst = null; }
    void OnNewGame() { rec = new S5SaveRec(); rec.seed = NewSeed(); foreach (var t in tg) { t.step = 0; t.armed = false; } inCamp = false; dirty = true; Flush(); Pose(null); }

    int NewSeed() { return D.seed != 0 ? D.seed : Environment.TickCount; }

    void Start()
    {
        if (!forcedMode) Load();
        if (rec.seed == 0) rec.seed = NewSeed();
    }

    void Load()
    {
        try
        {
            string p = SavePath; if (!File.Exists(p)) return;
            var r = JsonUtility.FromJson<S5SaveRec>(File.ReadAllText(p).TrimStart((char)0xFEFF)); if (r == null || r.version > SaveVersion) return;
            if (r.targets == null) r.targets = new S5TargetRec[0]; rec = r;
        }
        catch (Exception e) { Debug.LogWarning("[S5] statue record unreadable, starting fresh: " + e.Message); rec = new S5SaveRec(); }
    }
    void Flush()
    {
        if (!dirty || !CanSave) return;
        try { var l = new List<S5TargetRec>(); foreach (var t in tg) l.Add(new S5TargetRec { id = t.row.id, step = t.step }); foreach (var o in rec.targets) if (o != null && !tg.Exists(t => t.row.id == o.id)) l.Add(o); rec.targets = l.ToArray(); rec.version = SaveVersion; SafeFile.AtomicWrite(SavePath, JsonUtility.ToJson(rec, true)); dirty = false; }
        catch (Exception e) { Debug.LogWarning("[S5] statue record write failed: " + e.Message); }
    }
    int SavedStep(string id) { if (rec.targets != null) foreach (var o in rec.targets) if (o != null && o.id == id) return o.step; return 0; }

    void Log(string s) { if (D.log && logged++ < MaxLogs) Debug.Log("[S5] " + s); }

    void Update()
    {
        if (Time.unscaledTime < next) return; next = Time.unscaledTime + Mathf.Max(Time.unscaledDeltaTime, D.pollSeconds);
        var pl = GameLoop.Player; if (pl == null) return;
        Vector2 c = D.campCenter != null && D.campCenter.Length >= LookAtMin ? new Vector2(D.campCenter[0], D.campCenter[1]) : Vector2.zero;
        var pp = pl.transform.position; float d = Vector2.Distance(new Vector2(pp.x, pp.z), c);
        if (d <= D.findRadiusM) Bind();
        if (forcedMode) { Forced(); return; }
        if (!inCamp) { if (d <= D.enterRadiusM && tg.Count > 0) EnterCamp(); }
        else
        {
            if (d >= D.leaveRadiusM) { awayT += Mathf.Max(Time.unscaledDeltaTime, D.pollSeconds); if (awayT >= D.leaveSeconds) { inCamp = false; awayT = 0f; Log("left the camp"); } }
            else awayT = 0f;
            foreach (var t in tg) TryAdvance(t);
        }
    }

    void EnterCamp()
    {
        inCamp = true; awayT = 0f; rec.visits++; dirty = true;
        if (D.resetOnVisit) SurpriseGate.Reset(D.zone);
        foreach (var t in tg)
        {
            int n = t.row.steps != null ? t.row.steps.Length : 0; float every = Mathf.Max(1f, t.row.advanceEveryVisits);
            int want = Mathf.Min(n, Mathf.FloorToInt((rec.visits - 1) / every)); t.armed = want > t.step; t.unseenSince = -1f;
        }
        Log("visit " + rec.visits);
        Flush();
    }

    void Bind()
    {
        foreach (var r in D.targets)
        {
            if (r == null || string.IsNullOrEmpty(r.id) || tg.Exists(q => q.row == r && q.go != null)) continue;
            var go = GameObject.Find(r.id); if (go == null) continue;
            tg.RemoveAll(q => q.row == r);
            var t = new Tgt { row = r, go = go, rends = go.GetComponentsInChildren<Renderer>(true), basePos = go.transform.position, baseRot = go.transform.rotation, step = Mathf.Clamp(forcedStep >= 0 ? forcedStep : SavedStep(r.id), 0, r.steps != null ? r.steps.Length : 0) };
            t.baseBounds = Union(t.rends); tg.Add(t); Pose(t); Log("bound " + r.id + " at step " + t.step);
        }
    }

    static Bounds Union(Renderer[] rs) { Bounds b = new Bounds(); bool any = false; foreach (var r in rs) { if (r == null) continue; if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); } return b; }

    // visible = inside the camera frustum with the bounds grown by frustumMarginM (also true when there is no camera yet, so nothing changes during a load)
    bool Seen(Tgt t, out float dist)
    {
        dist = 0f; var cam = Camera.main; if (cam == null) return true;
        var b = t.baseBounds; dist = Vector3.Distance(cam.transform.position, b.center); b.Expand(D.visibility.frustumMarginM * BothSides);
        GeometryUtility.CalculateFrustumPlanes(cam, planes); return GeometryUtility.TestPlanesAABB(planes, b);
    }

    void TryAdvance(Tgt t)
    {
        if (!t.armed || t.go == null) return;
        float dist; bool seen = Seen(t, out dist);
        if (seen || dist < D.visibility.minDistanceM) { t.unseenSince = -1f; return; }
        if (t.unseenSince < 0f) t.unseenSince = Time.unscaledTime;
        if (Time.unscaledTime - t.unseenSince < D.visibility.unseenSeconds) return;
        if (D.useGate && !SurpriseGate.TryClaim(D.zone, D.gateKey)) return;
        t.step++; t.armed = false; dirty = true; Pose(t); Log(NoteApplied + t.step + " of " + t.row.id + " (unseen " + (Time.unscaledTime - t.unseenSince).ToString("F1") + " s)"); Flush();
    }

    void Forced()
    {
        foreach (var t in tg)
        {
            int n = t.row.steps != null ? t.row.steps.Length : 0;
            int want = forcedStep >= 0 ? Mathf.Clamp(forcedStep, 0, n) : (sweepS > 0f ? Mathf.FloorToInt(sweepT / sweepS) % (n + 1) : t.step);
            if (forcedStep < 0) sweepT += Mathf.Max(Time.unscaledDeltaTime, D.pollSeconds);
            if (want != t.shown) { t.step = want; Pose(t); }
        }
    }

    static float Signed(System.Random r) { return (float)r.NextDouble() * BothSides - 1f; }

    void Pose(Tgt only)
    {
        foreach (var t in tg) { if (only != null && t != only) continue; if (t.go == null) continue; Apply(t); }
    }

    void Apply(Tgt t)
    {
        var steps = t.row.steps; bool hide = false; Vector3 pos = t.basePos; Quaternion rot = t.baseRot;
        if (t.step > 0 && steps != null && t.step <= steps.Length)
        {
            var s = steps[t.step - 1]; hide = s.hide;
            Vector3 toward = Vector3.forward; float yawTo = t.baseRot.eulerAngles.y;
            if (t.row.lookAt != null && t.row.lookAt.Length >= LookAtMin) { var dv = new Vector3(t.row.lookAt[0] - t.basePos.x, 0f, t.row.lookAt[1] - t.basePos.z); if (dv.sqrMagnitude > Mathf.Epsilon) { toward = dv.normalized; yawTo = Mathf.Atan2(dv.x, dv.z) * Mathf.Rad2Deg + t.row.forwardYawOffsetDeg; } }
            var rnd = new System.Random(rec.seed * 31 + Animator.StringToHash(t.row.id) + t.step);
            float jy = Signed(rnd) * D.jitter.yawDeg, jx = Signed(rnd) * D.jitter.offsetM, jz = Signed(rnd) * D.jitter.offsetM;
            var e = t.baseRot.eulerAngles; float yaw = Mathf.LerpAngle(e.y, yawTo, s.towardFrac) + s.yawDeg + jy;
            rot = Quaternion.Euler(e.x + s.pitchDeg, yaw, e.z + s.rollDeg);
            pos = t.basePos + toward * s.towardM + new Vector3(jx, 0f, jz);
            if (s.offsetM != null && s.offsetM.Length >= VecN) pos += new Vector3(s.offsetM[0], s.offsetM[1], s.offsetM[2]);
        }
        t.go.transform.SetPositionAndRotation(pos, rot); t.shown = t.step;
        foreach (var r in t.rends) if (r != null) r.enabled = !hide;
        if (hide) MakePlinth(t); else if (t.proxy != null) t.proxy.SetActive(false);
    }

    // the prop GLB is one mesh (lion + base), so "the plinth is empty" = the statue renderers off and a low stone slab stand-in on the footprint (existing statue material, built-in cylinder mesh, no collider added)
    void MakePlinth(Tgt t)
    {
        if (!D.plinth.enabled) return;
        if (t.proxy != null) { t.proxy.SetActive(true); return; }
        var b = t.baseBounds; float h = b.size.y * D.plinth.heightFrac, rad = Mathf.Max(b.extents.x, b.extents.z) * D.plinth.radiusFrac;
        var g = GameObject.CreatePrimitive(PrimitiveType.Cylinder); g.name = D.plinth.name; var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);
        g.transform.position = new Vector3(b.center.x, b.min.y + h * Half, b.center.z); g.transform.localScale = new Vector3(rad * BothSides, h * Half, rad * BothSides);
        var mr = g.GetComponent<MeshRenderer>(); Renderer src = null; foreach (var r in t.rends) if (r != null) { src = r; break; } if (mr != null && src != null) mr.sharedMaterial = src.sharedMaterial;
        t.proxy = g;
    }
}

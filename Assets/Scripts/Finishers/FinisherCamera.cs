using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// The finisher shot player (was CameraRig). It owns the main camera ONLY while a finisher scene runs: RTFinisherHost sets CameraRig.Yield = true first, then Begin().
//  - Cut(shotId, killer, victim): blend (unscaled time) from the current pose to a live orbit pose around the pair. Slow-mo never slows the camera.
//  - Shots come from the entry's shotDefs (finishers.json); an unknown id uses a built-in orbit pose.
//  - Camera collision + floor from camera_collision.json (the same file CameraRig.cs reads).
[DefaultExecutionOrder(1100)]   // after CameraRig (1000)
public class FinisherCamera : MonoBehaviour
{
    public static FinisherCamera Instance { get; private set; }
    public bool Active { get; private set; }

    Camera cam; float oldFov;
    FinisherShot shot; Transform actor, target; float sideUsed = 1f, sideFlip = 1f;
    Vector3 fromPos; Quaternion fromRot; float fromFov; float blendT = 1f, blendDur = 0f;
    float shakeAmp, shakeLeft; float shotStart; GameObject noBlurGo;
    readonly FinisherBody tb = new FinisherBody(), ab = new FinisherBody();   // V61B: live body bounds of the victim (target) and the killer (actor): the shots frame the BODY, not the root transform
    float flipLock;

    public static FinisherCamera Ensure()
    {
        if (Instance != null) return Instance;
        var c = Camera.main; if (c == null) c = FindFirstObjectByType<Camera>();
        if (c == null) return null;
        var r = c.GetComponent<FinisherCamera>(); if (r == null) r = c.gameObject.AddComponent<FinisherCamera>();
        r.enabled = false;
        return r;
    }
    void Awake() { Instance = this; cam = GetComponent<Camera>(); }
    void OnDestroy() { if (Instance == this) Instance = null; FinisherTime.Restore(); SetNoBlur(false); LookEnd(); }

    public void Begin(Transform killerT, Transform victimT)
    {
        if (cam == null) cam = GetComponent<Camera>();
        if (cam == null) return;
        if (!Active) oldFov = cam.fieldOfView;
        actor = killerT; target = victimT; tb.Set(victimT); ab.Set(killerT);
        Active = true; enabled = true;
        SetNoBlur(true); LookBegin();
        fromPos = transform.position; fromRot = transform.rotation; fromFov = cam.fieldOfView;
        shot = null;
    }

    public void End()
    {
        if (!Active) return;
        Active = false; enabled = false; FinisherTime.Restore(); SetNoBlur(false); LookEnd(); shot = null;
        if (cam) cam.fieldOfView = oldFov;
    }

    public void Cut(string shotId, Transform actorT, Transform targetT, float blendOverride = -1f, FinisherShot[] localDefs = null)
    {
        FinisherShot s = null;
        if (localDefs != null) foreach (var d in localDefs) if (d != null && d.id == shotId) { s = d; break; }
        if (s == null) s = Fallback();
        Cut(s, actorT, targetT, blendOverride);
    }

    public void Cut(FinisherShot s, Transform actorT, Transform targetT, float blendOverride = -1f)
    {
        if (!Active || cam == null || s == null) return;
        fromPos = transform.position; fromRot = transform.rotation; fromFov = cam.fieldOfView;   // blend from where the camera is right now
        shot = s; shotStart = Time.unscaledTime; actor = actorT ? actorT : actor; target = targetT ? targetT : target; tb.Set(target); ab.Set(actor); flipLock = 0f;
        sideUsed = s.side == 0f ? (sideFlip = -sideFlip) : Mathf.Sign(s.side);
        blendDur = blendOverride >= 0f ? blendOverride : s.blend; blendT = blendDur <= 0f ? 1f : 0f;
        if (s.shake > 0f) { shakeAmp = s.shake; shakeLeft = 0.25f; }
    }

    public void Shake(float amp, float seconds = 0.25f) { shakeAmp = amp; shakeLeft = seconds; }

    static FinisherShot Fallback()
    {
        return new FinisherShot { id = "fallback", focus = "mid", angleDeg = 25f, side = 1f, dist = 6f, height = 2.4f, lookHeight = 1.2f, shoulder = 0.8f, lookAhead = 0.2f, fov = 38f, blend = 0.3f };
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    void Pose(FinisherShot s, out Vector3 pos, out Quaternion rot, out float fov, out Vector3 lookPt, out Vector3 focusPt)
    {
        Vector3 ap = actor.position, tp = target.position;
        bool body = s.track != "root" && tb.Valid; Bounds tbb = tb.Bounds;
        if (body) tp = new Vector3(tbb.center.x, tp.y, tbb.center.z);   // the real horizontal centre of the victim's body (a fallen body is not where the root is)
        Vector3 axis = Flat(tp - ap); if (axis.sqrMagnitude < 1e-4f) axis = Flat(actor.forward); if (axis.sqrMagnitude < 1e-4f) axis = Vector3.forward; axis.Normalize();
        Vector3 f = s.focus == "actor" ? ap : s.focus == "target" ? tp : (ap + tp) * 0.5f;
        // direction from the focus to the camera: angle 0 = straight behind the actor (-axis), turning toward the side by angle
        float a = s.angleDeg * Mathf.Deg2Rad * sideUsed;
        Vector3 perp = Vector3.Cross(Vector3.up, axis);
        Vector3 dir = -axis * Mathf.Cos(a) + perp * Mathf.Sin(a);
        float dist = s.dist;
        float tanV = Mathf.Tan(Mathf.Clamp(s.fov, 10f, 90f) * 0.5f * Mathf.Deg2Rad), asp = cam != null && cam.aspect > 0.1f ? cam.aspect : 1.7778f, tanH = tanV * asp;
        if (s.fit)
        {
            // distance from the real separation: the camera plane sees the pair's axis foreshortened by |sin(a)|, plus the half bodies each side; also the body height
            float sep = Flat(tp - ap).magnitude, ca = Mathf.Abs(Mathf.Cos(a)), sa = Mathf.Abs(Mathf.Sin(a));
            bool pairFit = s.fitTarget != "target";
            float span = pairFit ? sep * sa + 2f * s.bodyHalfWidth : 2f * s.bodyHalfWidth;
            float depth = pairFit ? sep * ca * 0.5f : 0f;
            float need = Mathf.Max(span * 0.5f / tanH, s.bodyHeight * 0.5f / tanV) * s.fitMargin + depth;
            dist = Mathf.Clamp(need, s.minDist, Mathf.Max(s.minDist, s.maxDist));
        }
        float lookY = f.y + s.lookHeight;
        if (body && s.focus == "target")
        {
            // V61B: the look-at stays INSIDE the victim's real body (prone: low; standing: head / chest) and frameBody pushes the camera back until the body (or its top frameTop metres) fits
            float yHi = tbb.max.y, yLo = s.frameTop > 0f ? Mathf.Max(tbb.min.y, yHi - s.frameTop) : tbb.min.y;
            lookY = Mathf.Clamp(lookY, yLo + s.lookMin, Mathf.Max(yLo + s.lookMin, yHi - 0.05f));
            if (s.frameBody)
            {
                float halfV = Mathf.Max(lookY - yLo, yHi - lookY) + 0.08f, halfH = Mathf.Max(tbb.extents.x, tbb.extents.z) + 0.08f;
                float need = Mathf.Max(halfV / tanV, halfH / tanH) * Mathf.Max(1f, s.frameMargin);
                dist = Mathf.Min(Mathf.Max(dist, need), Mathf.Max(dist, s.frameMaxDist));
            }
        }
        if (s.pushIn > 0f && s.pushSeconds > 0f) dist -= s.pushIn * Mathf.Clamp01((Time.unscaledTime - shotStart) / s.pushSeconds);
        pos = new Vector3(f.x, f.y, f.z) + dir * dist + perp * s.shoulder * sideUsed;
        pos.y = f.y + s.height;
        Vector3 look = Vector3.Lerp(f, tp, Mathf.Clamp01(s.lookAhead)); look.y = lookY;
        rot = Quaternion.LookRotation(look - pos, Vector3.up);
        fov = s.fov; lookPt = look; focusPt = new Vector3(f.x, Mathf.Max(f.y + 0.8f, lookY), f.z);
    }

    void LateUpdate()
    {
        if (!Active || cam == null) return;
        if (shot == null || actor == null || target == null) return;
        float dt = Time.unscaledDeltaTime;
        tb.Update(dt, shot.bodyFollow); ab.Update(dt, shot.bodyFollow);
        Pose(shot, out Vector3 p, out Quaternion r, out float fv, out Vector3 lookPt, out Vector3 focusPt);
        // V61B: never inside the killer's or the victim's own body (that was the black frame): swing to the other side once, then re-pose
        if (Time.unscaledTime >= flipLock && Inside(p))
        {
            sideUsed = -sideUsed; flipLock = Time.unscaledTime + 0.6f;
            Pose(shot, out p, out r, out fv, out lookPt, out focusPt);
        }
        if (blendT < 1f)
        {
            blendT = blendDur <= 0f ? 1f : Mathf.Min(1f, blendT + dt / blendDur);
            float k = blendT * blendT * (3f - 2f * blendT);
            p = Vector3.Lerp(fromPos, p, k); r = Quaternion.Slerp(fromRot, r, k); fv = Mathf.Lerp(fromFov, fv, k);
        }
        if (shakeLeft > 0f)
        {
            shakeLeft -= dt; float w = Mathf.Clamp01(shakeLeft / 0.25f);
            p += new Vector3(Mathf.Sin(Time.unscaledTime * 90f), Mathf.Cos(Time.unscaledTime * 77f), Mathf.Sin(Time.unscaledTime * 63f)) * shakeAmp * w;
        }
        // never inside geometry, never below the floor
        Vector3 safe = Constrain(p, focusPt);
        if ((safe - p).sqrMagnitude > 1e-4f) { p = safe; Vector3 ld = lookPt - p; if (ld.sqrMagnitude > 1e-4f) r = Quaternion.LookRotation(ld, Vector3.up); }
        transform.SetPositionAndRotation(p, r); cam.fieldOfView = fv; LookEnforce();
    }

    // ---- V61C: the finisher shots keep the GAMEPLAY camera's look (data: finishers.json rt.look; FinisherLook.cs) ----
    LookSnap lookSnap; GameObject lookAnchor; public static int LookFixes;
    void LookBegin()
    {
        try
        {
            var L = FinisherConfig.Get().rt.look; if (L == null || cam == null) return;
            if (L.copyCameraData) lookSnap = FinisherLook.Capture(cam);
            if (L.freezeVolumeAnchor)
            {
                var hd = cam.GetComponent<HDAdditionalCameraData>();
                if (hd != null)
                {
                    if (lookSnap == null) lookSnap = FinisherLook.Capture(cam);
                    if (lookAnchor == null) lookAnchor = new GameObject("FinisherVolumeAnchor");
                    lookAnchor.transform.SetPositionAndRotation(transform.position, transform.rotation);   // the gameplay camera pose: the Volumes blend exactly as in gameplay
                    hd.volumeAnchorOverride = lookAnchor.transform;
                }
            }
        }
        catch (System.Exception e) { Debug.LogWarning("[FinisherCamera] look begin: " + e.Message); }
    }
    void LookEnforce()
    {
        if (lookSnap == null) return;
        try { var L = FinisherConfig.Get().rt.look; if (L != null && L.enforceEachFrame) LookFixes += FinisherLook.Apply(lookSnap); } catch { }
    }
    void LookEnd()
    {
        try
        {
            if (lookSnap != null) { FinisherLook.Apply(lookSnap); if (lookSnap.hd != null) lookSnap.hd.volumeAnchorOverride = lookSnap.volumeAnchor; }
            if (lookAnchor != null) { Destroy(lookAnchor); lookAnchor = null; }
            lookSnap = null;
        }
        catch (System.Exception e) { Debug.LogWarning("[FinisherCamera] look end: " + e.Message); }
    }

    void Update() { FinisherTime.Tick(); }

    bool Inside(Vector3 p)
    {
        if (tb.Valid) { var bb = tb.Bounds; bb.Expand(0.5f); if (bb.Contains(p)) return true; }
        if (ab.Valid) { var bb = ab.Bounds; bb.Expand(0.5f); if (bb.Contains(p)) return true; }
        return false;
    }

    // Motion blur off while a scene runs: a global Volume with a very high priority overriding MotionBlur intensity to 0 wins over every scene / default profile (HDRP).
    void SetNoBlur(bool on)
    {
        try
        {
            if (on)
            {
                if (noBlurGo != null || !FinisherConfig.Get().rt.disableMotionBlur) return;
                var go = new GameObject("FinisherNoMotionBlur"); var v = go.AddComponent<Volume>();
                v.isGlobal = true; v.priority = 10000f; v.weight = 1f;
                var prof = ScriptableObject.CreateInstance<VolumeProfile>();
                var mb = prof.Add<MotionBlur>(true); mb.intensity.Override(0f); mb.active = true; FinisherLook.DarkenExposure(prof);
                v.sharedProfile = prof; noBlurGo = go;
            }
            else if (noBlurGo != null)
            {
                var v = noBlurGo.GetComponent<Volume>(); if (v != null && v.sharedProfile != null) Destroy(v.sharedProfile);
                Destroy(noBlurGo); noBlurGo = null;
            }
        }
        catch (System.Exception e) { Debug.LogWarning("[FinisherCamera] motion blur override: " + e.Message); }
    }

    // ---- camera collision + floor (data: camera_collision.json, the same file CameraRig.cs reads) ----
    [System.Serializable] class ColRow { public bool enabled = true; public float radius = 0.35f, minDistFromTarget = 1.2f, minGroundY = 0f, minHeightAboveGround = 0.6f; public int[] ignoreLayers = { 8, 9 }; }
    ColRow col; Vector3 lastGood; bool haveGood;
    static bool Finite(Vector3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }
    void LoadCol()
    {
        col = new ColRow();
        foreach (var path in new[] { Application.streamingAssetsPath + "/camera_collision.json", Application.dataPath + "/Data/camera_collision.json" })
        {
            try { if (!System.IO.File.Exists(path)) continue; var c = JsonUtility.FromJson<ColRow>(System.IO.File.ReadAllText(path)); if (c != null) col = c; break; } catch { }
        }
    }
    Vector3 Constrain(Vector3 p, Vector3 from)
    {
        if (col == null) LoadCol();
        if (!Finite(p)) return haveGood ? lastGood : transform.position;
        if (col.enabled)
        {
            Vector3 d = p - from; float len = d.magnitude;
            if (len > 0.01f)
            {
                int mask = ~0; if (col.ignoreLayers != null) foreach (var l in col.ignoreLayers) mask &= ~(1 << l);
                if (Physics.SphereCast(from, col.radius, d / len, out var hit, len, mask, QueryTriggerInteraction.Ignore))
                    p = from + d / len * Mathf.Max(col.minDistFromTarget, hit.distance);
            }
        }
        float floorY = col.minGroundY + col.minHeightAboveGround; if (p.y < floorY) p.y = floorY;
        lastGood = p; haveGood = true;
        return p;
    }
}

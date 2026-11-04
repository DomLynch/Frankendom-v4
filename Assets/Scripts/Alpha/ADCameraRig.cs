using System.Collections.Generic;
using UnityEngine;

// Alpha step 1, lane C: the cinematic duel camera. No free/tactical camera. It replaces CameraRig.cs only while the alpha duel is active
// (Begin disables CameraRig, End restores it, the FOV and the cursor). All poses come from ADShot rows in duel_rules.json "camera".
//  - Shot(id, actor, target): blend (unscaled time) from the current pose to a live orbit pose around the pair. Slow-mo never slows the camera.
//  - Plan(): the over-the-shoulder planning view behind Dracula looking at the Pitborn.
//  - ADTime: Time.timeScale dips (crit, parry, finisher) with ease-in/out on unscaled time; always restored.
[DefaultExecutionOrder(1100)]   // after CameraRig (1000)
public class ADCameraRig : MonoBehaviour
{
    public static ADCameraRig Instance { get; private set; }
    public bool Active { get; private set; }

    Camera cam; CameraRig oldRig; bool oldRigEnabled; float oldFov; CursorLockMode oldLock; bool oldCursorVisible;
    Transform dracula, pitborn;
    // current shot
    ADShot shot; Transform actor, target; float sideUsed = 1f, sideFlip = 1f;
    // blend: from-pose snapshot -> live shot pose
    Vector3 fromPos; Quaternion fromRot; float fromFov; float blendT = 1f, blendDur = 0f;
    // shake
    float shakeAmp, shakeLeft;

    public static ADCameraRig Ensure()
    {
        if (Instance != null) return Instance;
        var c = Camera.main; if (c == null) c = FindFirstObjectByType<Camera>();
        if (c == null) return null;
        var r = c.GetComponent<ADCameraRig>(); if (r == null) r = c.gameObject.AddComponent<ADCameraRig>();
        r.enabled = false;
        return r;
    }
    void Awake() { Instance = this; cam = GetComponent<Camera>(); }
    void OnDestroy() { if (Instance == this) Instance = null; ADTime.Restore(); }

    // fighters: Dracula (the player) and the Pitborn. The plan view sits behind Dracula.
    public void Begin(Transform draculaT, Transform pitbornT)
    {
        if (cam == null) cam = GetComponent<Camera>();
        if (cam == null) return;   // a rig on a non-camera object does nothing
        dracula = draculaT; pitborn = pitbornT;
        if (!Active)
        {
            oldRig = FindFirstObjectByType<CameraRig>();
            if (oldRig != null) { oldRigEnabled = oldRig.enabled; oldRig.enabled = false; }
            oldFov = cam.fieldOfView; oldLock = Cursor.lockState; oldCursorVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;   // the alpha UI is clicked
        }
        Active = true; enabled = true;
        fromPos = transform.position; fromRot = transform.rotation; fromFov = cam.fieldOfView;
        Cut(ADCConfig.Camera.planShot, dracula, pitborn, 0f);
    }

    public void End()
    {
        if (!Active) return;
        Active = false; enabled = false; ADTime.Restore();
        if (cam) cam.fieldOfView = oldFov;
        if (oldRig) oldRig.enabled = oldRigEnabled;
        Cursor.lockState = oldLock; Cursor.visible = oldCursorVisible;
    }

    public void Plan() { Cut(ADCConfig.Camera.planShot, dracula, pitborn, -1f); }

    // blendOverride < 0: use the shot's own blend; 0 = hard cut
    public void Cut(string shotId, Transform actorT, Transform targetT, float blendOverride = -1f, ADShot[] localDefs = null)
    {
        var s = ADCConfig.Shot(shotId, localDefs);
        if (s == null) { s = Fallback(); }
        Cut(s, actorT, targetT, blendOverride);
    }

    public void Cut(ADShot s, Transform actorT, Transform targetT, float blendOverride = -1f)
    {
        if (!Active || cam == null || s == null) return;
        // snapshot where the camera is right now, blend from there
        fromPos = transform.position; fromRot = transform.rotation; fromFov = cam.fieldOfView;
        shot = s; actor = actorT ? actorT : dracula; target = targetT ? targetT : pitborn;
        sideUsed = s.side == 0f ? (sideFlip = -sideFlip) : Mathf.Sign(s.side);
        blendDur = blendOverride >= 0f ? blendOverride : s.blend; blendT = blendDur <= 0f ? 1f : 0f;
        if (s.shake > 0f) { shakeAmp = s.shake; shakeLeft = 0.25f; }
    }

    public void Shake(float amp, float seconds = 0.25f) { shakeAmp = amp; shakeLeft = seconds; }

    static ADShot Fallback()
    {
        return new ADShot { id = "fallback", focus = "mid", angleDeg = 25f, side = 1f, dist = 6f, height = 2.4f, lookHeight = 1.2f, shoulder = 0.8f, lookAhead = 0.2f, fov = 38f, blend = 0.3f };
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    void Pose(ADShot s, out Vector3 pos, out Quaternion rot, out float fov, out Vector3 lookPt, out Vector3 focusPt)
    {
        Vector3 ap = actor.position, tp = target.position;
        Vector3 axis = Flat(tp - ap); if (axis.sqrMagnitude < 1e-4f) axis = Flat(actor.forward); if (axis.sqrMagnitude < 1e-4f) axis = Vector3.forward; axis.Normalize();
        Vector3 f = s.focus == "actor" ? ap : s.focus == "target" ? tp : (ap + tp) * 0.5f;
        // direction from the focus to the camera: angle 0 = straight behind the actor (-axis), turning toward the side by angle
        float a = s.angleDeg * Mathf.Deg2Rad * sideUsed;
        Vector3 perp = Vector3.Cross(Vector3.up, axis);
        Vector3 dir = -axis * Mathf.Cos(a) + perp * Mathf.Sin(a);
        pos = new Vector3(f.x, f.y, f.z) + dir * s.dist + perp * s.shoulder * sideUsed;
        pos.y = f.y + s.height;
        Vector3 look = Vector3.Lerp(f, tp, Mathf.Clamp01(s.lookAhead)); look.y = f.y + s.lookHeight;
        rot = Quaternion.LookRotation(look - pos, Vector3.up);
        fov = s.fov; lookPt = look; focusPt = new Vector3(f.x, f.y + Mathf.Max(0.8f, s.lookHeight), f.z);
    }

    void LateUpdate()
    {
        if (!Active || cam == null) return;
        if (shot == null || actor == null || target == null) return;
        float dt = Time.unscaledDeltaTime;
        Pose(shot, out Vector3 p, out Quaternion r, out float fv, out Vector3 lookPt, out Vector3 focusPt);
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
        // never inside geometry, never below the floor (same approach as CameraRig.cs: spherecast from the look target to the camera, camera_collision.json)
        Vector3 safe = Constrain(p, focusPt);
        if ((safe - p).sqrMagnitude > 1e-4f) { p = safe; Vector3 ld = lookPt - p; if (ld.sqrMagnitude > 1e-4f) r = Quaternion.LookRotation(ld, Vector3.up); }
        transform.SetPositionAndRotation(p, r); cam.fieldOfView = fv;
    }

    void Update() { ADTime.Tick(); }

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

// Time.timeScale dips on unscaled time. Several requests merge: the lowest scale wins while any is running.
public static class ADTime
{
    class Dip { public float scale, dur, easeIn, easeOut, age; }
    static readonly List<Dip> dips = new List<Dip>();
    static float baseScale = 1f, baseFixed = 0.02f; static bool captured;
    static int lastFrame = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { dips.Clear(); captured = false; lastFrame = -1; }

    public static void SlowMo(float scale, float durationUnscaled, float easeIn = 0.05f, float easeOut = 0.25f)
    {
        if (!captured) { captured = true; baseScale = Time.timeScale <= 0f ? 1f : Time.timeScale; baseFixed = Time.fixedDeltaTime; }
        dips.Add(new Dip { scale = Mathf.Clamp(scale, 0.02f, 1f), dur = Mathf.Max(0.01f, durationUnscaled), easeIn = Mathf.Max(0.001f, easeIn), easeOut = Mathf.Max(0.001f, easeOut) });
    }
    public static bool Slowed { get { return dips.Count > 0; } }

    // call once per frame (ADCameraRig.Update and the playback coroutines both may call it; it runs once per frame)
    public static void Tick()
    {
        if (lastFrame == Time.frameCount) return; lastFrame = Time.frameCount;
        if (dips.Count == 0) return;
        float dt = Time.unscaledDeltaTime, lowest = 1f;
        for (int i = dips.Count - 1; i >= 0; i--)
        {
            var d = dips[i]; d.age += dt;
            if (d.age >= d.dur + d.easeOut) { dips.RemoveAt(i); continue; }
            float w = d.age < d.easeIn ? d.age / d.easeIn : (d.age <= d.dur ? 1f : 1f - (d.age - d.dur) / d.easeOut);
            lowest = Mathf.Min(lowest, Mathf.Lerp(1f, d.scale, Mathf.Clamp01(w)));
        }
        if (dips.Count == 0) { Restore(); return; }
        Time.timeScale = baseScale * lowest; Time.fixedDeltaTime = baseFixed * Time.timeScale;
    }

    public static void Restore()
    {
        dips.Clear();
        if (captured) { Time.timeScale = baseScale; Time.fixedDeltaTime = baseFixed; captured = false; }
    }
}

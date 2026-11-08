using System;
using UnityEngine;

// V61B: fight-end camera. After a plain death (and when a finisher scene hands the camera back) the lock-on CameraRig used to fall into its free-camera pose,
// which looked over the PLAYER's shoulder and lost the body. This keeps the look-at on the dead fighter's body (victim = the roster enemy after a win, the player after a loss).
// CameraRig.LateUpdate calls Shape() right after it computed wantPos / wantLook and uses Weight / Fov when it sets the lens. Data: camera_death.json (StreamingAssets, then Assets/Data).
// No new UI. enabled=false = the old behaviour exactly.
public static class CameraDeath
{
    [Serializable]
    class Row
    {
        public bool enabled = true;
        public float delaySeconds = 0.25f;    // after the death before the camera starts to move
        public float blendSeconds = 0.9f;     // 0 -> 1 weight ramp (the rig's own eased pose chases the target)
        public float dist = 4.4f;             // metres from the body (grows so the whole body fits, see margin)
        public float height = 1.7f;           // camera height above the body's lowest point, x victim scale
        public float lookHeight = 0.7f;       // look-at height above the body's lowest point, x victim scale (clamped into the body bounds)
        public float sideDeg = 30f;           // yaw offset of the camera around the body, from the direction the camera already had
        public float fov = 34f;
        public float margin = 1.3f;           // >1 = air around the body
        public float maxDist = 8f;
        public float followRate = 6f;         // smoothing of the body bounds
        public bool keepFightCam = false;     // DEATHCAM v1: true = the death camera does nothing, the lock camera of the fight carries on (weight 0)
        public float minDistFightFrac = 0f;   // DEATHCAM v1: never closer to the body than this x the fight camera's horizontal distance when the fighter died (0 = off)
        public float minHeightFightFrac = 0f; // DEATHCAM v1: never lower than this x the fight camera's height above the body's floor (0 = off)
        public float padM = 0.1f, fallbackAspect = 1.7778f; /* DATALAYER b7 */ // air above / beside the body in the fit; aspect used when there is no main camera
    }
    static Row cfg; static readonly FinisherBody body = new FinisherBody();
    static float age, fightDist, fightH; static bool dirSet; static Vector3 dir = Vector3.back;
    static Health pHp; static Transform pHpFor;
    public static float Weight { get; private set; }
    public static float Fov { get { return Cfg().fov; } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; age = 0f; dirSet = false; Weight = 0f; pHp = null; pHpFor = null; }

    static Row Cfg()
    {
        if (cfg != null) return cfg;
        cfg = new Row();
        foreach (var p in new[] { Application.streamingAssetsPath + "/camera_death.json", Application.dataPath + "/Data/camera_death.json" })
        {
            try { if (!System.IO.File.Exists(p)) continue; var r = JsonUtility.FromJson<Row>(System.IO.File.ReadAllText(p).TrimStart((char)0xFEFF)); if (r != null) cfg = r; break; } catch { }
        }
        return cfg;
    }

    static void Clear() { age = 0f; dirSet = false; Weight = 0f; body.Set(null); }

    public static void Shape(Transform player, Transform enemy, Health enemyHp, ref Vector3 wantPos, ref Vector3 wantLook, Vector3 camNow)
    {
        var c = Cfg();
        if (!c.enabled || c.keepFightCam /* DEATHCAM v1 */ || player == null || RTFinisher.Busy) { if (Weight != 0f) Clear(); return; }
        if (pHpFor != player) { pHpFor = player; pHp = player.GetComponent<Health>(); if (pHp == null) pHp = player.GetComponentInParent<Health>(); }
        bool pDead = pHp != null && pHp.Dead, eDead = enemyHp != null && enemyHp.Dead;
        Transform victim = pDead ? player : (eDead && enemy != null ? enemy : null);
        if (victim == null) { if (Weight != 0f || age != 0f) Clear(); return; }

        float dt = Time.unscaledDeltaTime; age += dt;
        body.Set(victim); body.Update(dt, c.followRate);
        if (!body.Valid) return;
        Bounds bb = body.Bounds; float sc = Mathf.Max(0.5f, victim.lossyScale.y);
        Vector3 center = new Vector3(bb.center.x, bb.min.y, bb.center.z);
        if (!dirSet)
        {
            Vector3 d0 = camNow - center; d0.y = 0f; if (d0.sqrMagnitude < 0.25f) d0 = -victim.forward; d0.y = 0f;
            dir = Quaternion.Euler(0f, c.sideDeg, 0f) * d0.normalized; dirSet = true;
            { Vector3 fd = camNow - center; fd.y = 0f; fightDist = fd.magnitude; fightH = camNow.y - bb.min.y; /* DEATHCAM v1: the fight camera at the moment of death */ }
        }
        float w = c.blendSeconds <= 0f ? 1f : Mathf.Clamp01((age - c.delaySeconds) / c.blendSeconds); w = w * w * (3f - 2f * w);
        Weight = w;

        float lookY = Mathf.Clamp(bb.min.y + c.lookHeight * sc, bb.min.y + c.padM, Mathf.Max(bb.min.y + c.padM, bb.max.y)); /* DATALAYER b7 */
        var cam = Camera.main; float asp = cam != null && cam.aspect > 0.1f ? cam.aspect : c.fallbackAspect; /* DATALAYER b7 */
        float tanV = Mathf.Tan(Mathf.Clamp(c.fov, 10f, 90f) * 0.5f * Mathf.Deg2Rad), tanH = tanV * asp;
        float halfV = Mathf.Max(lookY - bb.min.y, bb.max.y - lookY) + c.padM, halfH = Mathf.Max(bb.extents.x, bb.extents.z) + c.padM; /* DATALAYER b7 */
        float need = Mathf.Max(halfV / tanV, halfH / tanH) * c.margin;
        float dist = Mathf.Min(Mathf.Max(c.dist, need), Mathf.Max(c.dist, c.maxDist));
        if (c.minDistFightFrac > 0f) dist = Mathf.Max(dist, fightDist * c.minDistFightFrac);   // DEATHCAM v1: no zoom in past the fight camera
        Vector3 pos = center + dir * dist; pos.y = Mathf.Max(bb.min.y + c.height * sc, bb.min.y + fightH * c.minHeightFightFrac);   // DEATHCAM v1
        Vector3 look = new Vector3(center.x, lookY, center.z);
        wantPos = Vector3.Lerp(wantPos, pos, w); wantLook = Vector3.Lerp(wantLook, look, w);
    }
}

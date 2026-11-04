using UnityEngine;

// Lock-on camera ported 1:1 from Frankendom V3 (src/fight/camera.ts cameraPose + createCameraRig.update): a closed-form pose, no collision push, no auto-bias, no solver.
//  gap d = horizontal player->enemy distance (floor 0.8); yaw = angle of the enemy->player line (camera behind the player, facing the enemy), eased with blend = 1 - exp(-8 dt)
//  back = max(4.2, 0.62 d + 2.8) * 1.3 behind the player; height = max(3.2, 1.3 d) * 0.67 * 1.3 above the player's feet (so it works on stairs)
//  shoulder +1.0 m to the camera's right; a tall foe (0.39 taller than a man) adds back += 0.39*2.4, height += 0.39*1.6 (both x1.3)
//  look-at = midpoint of both fighters at +0.8 m above their ground; position and look lerp with the same blend (snap on the first frame)
//  lens: vertical FOV 51 deg / (zoom 1.1 x landscape boost 1.2) = 38.6; contact kick + roll tumble live in CameraImpact.cs (data: Assets/Data/camera_impact.json)
[DefaultExecutionOrder(1000)]
public class CameraRig : MonoBehaviour
{
    public Transform target, enemy;
    public Health enemyHp;
    public float distance = 3.3f, height = 1.6f, shoulder = 0.5f, yaw = 0f, pitch = 2f, sensitivity = 2.2f;   // free-camera values (lock-on off)
    public bool lockEnabled = true;
    public float framingOk = 1f;   // test-only (read by diagnostics)
    public float lockRange = 12f;
    float lockW; Vector3 camPos, lookAt; bool snapped; float lockYaw;
    [System.Serializable] class FreeRow { public float yawLimitDeg = 55f, pitchMinDeg = -5f, pitchMaxDeg = 45f; }
    FreeRow free = new FreeRow(); bool freeLoaded;
    void LoadFree() { freeLoaded = true; foreach (var p in new[] { Application.streamingAssetsPath + "/camera_free.json", Application.dataPath + "/Data/camera_free.json" }) { if (!System.IO.File.Exists(p)) continue; try { free = JsonUtility.FromJson<FreeRow>(System.IO.File.ReadAllText(p)); } catch { } break; } }
    Camera cam; CameraImpact impact;
    public void Recenter() { if (target) yaw = target.eulerAngles.y; }
    void Awake() { cam = GetComponent<Camera>(); impact = GetComponent<CameraImpact>(); if (!impact) impact = gameObject.AddComponent<CameraImpact>(); foreach (var a in System.Environment.GetCommandLineArgs()) if (a == "-autoplay") lockRange = 80f; }
    void Start() { if (Application.isPlaying && !Application.isBatchMode) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; } }

    // Hook for the presentation layer: CameraImpact calls this when a special landed (a Hit by a skill_ move; never Blocked/GuardBroken). Hit-stop timing lives in PresentationClock, not here.
    public void OnSpecialLanded() { }

    // test-only (AutoShot): fraction of feet/waist/head points of a fighter inside the safe area
    public float VisibleFraction(Transform t)
    {
        float s = Mathf.Max(1f, t.lossyScale.y); int ok = 0;
        foreach (float h in new[] { 0.1f, 0.9f, 1.7f })
        {
            Vector3 v = cam.WorldToViewportPoint(t.position + Vector3.up * h * s);
            if (v.z > 0.1f && v.x > 0.05f && v.x < 0.95f && v.y > 0.03f && v.y < 0.97f) ok++;
        }
        return ok / 3f;
    }

    void LateUpdate()
    {
        if (!target) return;
        float dt = Time.deltaTime;
        bool lockOn = lockEnabled && enemy && enemyHp && !enemyHp.Dead && Vector3.Distance(target.position, enemy.position) < lockRange;
        lockW = Mathf.MoveTowards(lockW, lockOn ? 1f : 0f, dt * 3f);

        // free camera (lock-on off): mouse yaw/pitch over the right shoulder
        if (!freeLoaded) LoadFree();
        yaw += Input.GetAxis("Mouse X") * sensitivity; yaw = Mathf.Clamp(Mathf.DeltaAngle(0f, yaw), -free.yawLimitDeg, free.yawLimitDeg);
        pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * sensitivity, free.pitchMinDeg, free.pitchMaxDeg);
        var rot = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 focus = target.position + Vector3.up * height + rot * Vector3.right * shoulder;
        Vector3 freePos = focus - rot * Vector3.forward * distance, freeLook = focus;

        if (enemy)
        {
            Vector3 P = target.position, E = enemy.position;
            Vector3 flat = E - P; flat.y = 0f; float d = Mathf.Max(0.8f, flat.magnitude);
            float tgtYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;          // camera behind the player, facing the enemy
            float blend = 1f - Mathf.Exp(-8f * dt);
            if (!snapped) lockYaw = tgtYaw; else lockYaw += Mathf.DeltaAngle(lockYaw, tgtYaw) * blend;
            float foe = Mathf.Max(0f, (enemy.lossyScale.y * 2.1f - 1.8f) / 1.8f);   // how much taller than a man (Pitborn 2.5 m = 0.39)
            float back = (Mathf.Max(4.2f, 0.62f * d + 2.8f) + foe * 2.4f) * 1.3f;
            float h = (Mathf.Max(3.2f, 1.3f * d) * 0.67f + foe * 1.6f) * 1.3f;
            Quaternion yr = Quaternion.Euler(0f, lockYaw, 0f);
            Vector3 fwd = yr * Vector3.forward, right = yr * Vector3.right;
            Vector3 lockPos = new Vector3(P.x, P.y + h, P.z) - fwd * back + right * 1.0f;
            Vector3 mid = (P + E) * 0.5f;
            Vector3 lockLook = new Vector3(mid.x, mid.y + 0.8f, mid.z) + fwd * 0.3f;   // nudged toward the foe so he sits more central
            lockPos.x = Mathf.Clamp(lockPos.x, -11.5f, 11.5f);
            Vector3 wantPos = Vector3.Lerp(freePos, lockPos, lockW), wantLook = Vector3.Lerp(freeLook, lockLook, lockW);
            if (!snapped) { camPos = wantPos; lookAt = wantLook; snapped = true; }
            else { camPos = Vector3.Lerp(camPos, wantPos, blend); lookAt = Vector3.Lerp(lookAt, wantLook, blend); }
            if (lockW > 0.5f) yaw = lockYaw;   // keep the free camera continuous when the lock breaks
        }
        else { camPos = freePos; lookAt = freeLook; }

        transform.position = camPos;
        transform.rotation = Quaternion.LookRotation(lookAt - transform.position, Vector3.up);
        cam.fieldOfView = Mathf.Lerp(50f, 38.6f, lockW);
        if (impact) impact.Apply(transform);   // contact kick + roll tumble on top of the pose, on unscaled time (CameraImpact.cs)
    }
}


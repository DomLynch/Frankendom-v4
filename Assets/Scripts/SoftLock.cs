using System;
using System.IO;
using UnityEngine;

// Soft (half) lock-on camera (Dom 15 Nov: "the auto target look is annoying, when I run past it keeps rotating the camera and is fixed on the enemy; it should be a half lock: helpful, but I can break it if I move too much, move the mouse, keys ...").
// CameraRig asks SoftLock.Broken(player, enemy, dt) once per frame while the lock camera would be on; true = the camera frees (the normal free-camera easing, the player's mouse drives it again) until the lock re-engages. The fight's own targeting is not touched, only the camera.
// Break when, with the foe farther than nearGapM (up close the mouse is the attack direction and the foe is the point of the fight, so it never breaks there): (1) the player moves (speed >= minSpeed) more than breakAngleDeg away from the foe for breakAfterS, or (2) the camera mouse moves more than mouseBreak (summed |Mouse X| + |Mouse Y| over mouseWindowS).
// Re-engage when the gap falls under relockGapM, or the player moves toward the foe (within relockAngleDeg, speed >= minSpeed) for relockAfterS, or the foe is within nearGapM. soft_lock.json (StreamingAssets first, then Assets/Data): enabled false / no file = the old hard lock, byte for byte.
public static class SoftLock
{
    [Serializable] public class Cfg { public bool enabled = false, log = true; public float nearGapM = 3.5f, relockGapM = 4.5f, minSpeed = 1.2f, breakAngleDeg = 70f, breakAfterS = 0.35f, relockAngleDeg = 40f, relockAfterS = 0.4f, mouseBreak = 2.5f, mouseWindowS = 0.4f; public string[] breakActions = new string[0]; }
    static Cfg c; static Transform lastEnemy; static bool broken, init; static Vector3 lastPos; static float awayT, towardT, mouseAcc, mouseT;
    public static Cfg Get()
    {
        if (c != null) return c;
        c = new Cfg();
        try
        {
            string p = Path.Combine(Application.streamingAssetsPath, "soft_lock.json"); if (!File.Exists(p)) p = Path.Combine(Application.dataPath, "Data/soft_lock.json");
            if (File.Exists(p)) { var k = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart((char)0xFEFF)); if (k != null) c = k; }
        }
        catch (Exception e) { Debug.LogWarning("[SoftLock] read failed, off: " + e.Message); c = new Cfg(); }
        return c;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset() { c = null; broken = false; init = false; awayT = towardT = mouseAcc = mouseT = 0f; }

    public static bool Broken(Transform player, Transform enemy, float dt)
    {
        var k = Get(); if (!k.enabled || player == null || enemy == null || dt <= 0f) return false;
        Vector3 pp = player.position, to = enemy.position - pp; to.y = 0f; float gap = to.magnitude;
        if (!init) { init = true; lastPos = pp; }
        if (enemy != lastEnemy) { lastEnemy = enemy; broken = false; awayT = towardT = 0f; }   // a new foe starts locked
        Vector3 v = (pp - lastPos) / dt; v.y = 0f; lastPos = pp; float spd = v.magnitude;
        float ang = gap > 0.01f && spd >= k.minSpeed ? Vector3.Angle(v, to) : -1f;   // -1 = not moving enough to say
        float mx = Mathf.Abs(Input.GetAxisRaw("Mouse X")) + Mathf.Abs(Input.GetAxisRaw("Mouse Y"));
        mouseT += dt; mouseAcc += mx; if (mouseT > k.mouseWindowS) { mouseT = 0f; mouseAcc = 0f; }
        if (!broken)
        {
            if (gap <= k.nearGapM) { awayT = 0f; return false; }
            awayT = ang > k.breakAngleDeg ? awayT + dt : 0f;
            bool byMouse = mouseAcc >= k.mouseBreak, byKey = false;
            if (k.breakActions != null) for (int i = 0; i < k.breakActions.Length && !byKey; i++) if (!string.IsNullOrEmpty(k.breakActions[i]) && InputMap.Down(k.breakActions[i], KeyCode.None)) byKey = true;   // a deliberate key (dodge, the lock key ...) from controls.json action ids
            if (awayT >= k.breakAfterS || byMouse || byKey) { broken = true; towardT = 0f; awayT = 0f; mouseAcc = 0f; if (k.log) Debug.Log("[SoftLock] break reason=" + (byKey ? "key" : byMouse ? "mouse" : "moving away") + " (gap " + gap.ToString("F1") + " m)"); }
        }
        else
        {
            towardT = ang >= 0f && ang < k.relockAngleDeg ? towardT + dt : 0f;
            if (gap < k.relockGapM || towardT >= k.relockAfterS) { broken = false; towardT = 0f; if (k.log) Debug.Log("[SoftLock] re-lock (gap " + gap.ToString("F1") + " m)"); }
        }
        return broken;
    }
}

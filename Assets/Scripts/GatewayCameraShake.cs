using UnityEngine;

// Camera shake for the gateway opening. Why not CameraImpact: that layer is driven only by combat events (ContactRelay) and has no public "shake" call, and
// CameraImpact.cs is a shared file. This component sits after CameraRig in the script order (CameraRig = 1000, CameraImpact.Apply runs inside it) and adds a
// small noise offset + roll for the render of this frame; Update() takes the previous offset back out first, so it never compounds (CameraRig also rewrites the pose
// absolutely each LateUpdate). Real time (unscaledDeltaTime): a hit-stop does not freeze it. Numbers come from gateway.json "shake" through GatewayRift.
// Optional later: add a "gateway" row to camera_impact.json and a public Kick() in CameraImpact; then delete this file.
[DefaultExecutionOrder(1500)]
public class GatewayCameraShake : MonoBehaviour
{
    float kickAmp, kickRoll, kickAge, kickDur, kickHz = 20f;
    float rumbleAmp, rumbleRoll, rumbleTarget = 0f;
    Vector3 appliedPos; float appliedRoll; float seed;

    void Awake() { seed = Random.value * 50f; }

    /// One shove with a quick attack and a squared decay.
    public void Kick(float amp, float rollDeg, float seconds, float hz)
    {
        kickAmp = amp; kickRoll = rollDeg; kickDur = Mathf.Max(0.05f, seconds); kickAge = 0f; kickHz = Mathf.Max(1f, hz);
    }

    /// Continuous low rumble level 0..1 times the given amplitudes (set 0 to stop).
    public void SetRumble(float level01, float amp, float rollDeg) { rumbleTarget = Mathf.Clamp01(level01); rumbleAmp = amp; rumbleRoll = rollDeg; }

    void Update()
    {
        // take back last frame's offset (CameraRig rewrites the pose anyway; without it the offset would persist on a camera nobody else moves)
        if (appliedPos != Vector3.zero) { transform.position -= appliedPos; appliedPos = Vector3.zero; }
        if (appliedRoll != 0f) { transform.rotation = transform.rotation * Quaternion.Euler(0f, 0f, -appliedRoll); appliedRoll = 0f; }
    }

    float level;

    void LateUpdate()
    {
        float dt = Time.unscaledDeltaTime;
        level = Mathf.MoveTowards(level, rumbleTarget, dt * 2.5f);
        float k = 0f;
        if (kickAge < kickDur) { float u = kickAge / kickDur; k = Mathf.Min(1f, kickAge / 0.04f) * (1f - u) * (1f - u); kickAge += dt; }
        float amp = kickAmp * k + rumbleAmp * level;
        float roll = kickRoll * k + rumbleRoll * level;
        if (amp <= 0f && roll <= 0f) return;
        float t = Time.unscaledTime;
        float hz = k > 0.01f ? kickHz : 11f;
        Vector3 off = new Vector3(Mathf.PerlinNoise(seed, t * hz) - 0.5f, Mathf.PerlinNoise(seed + 7.1f, t * hz) - 0.5f, Mathf.PerlinNoise(seed + 13.3f, t * hz) - 0.5f) * (2f * amp);
        float rl = (Mathf.PerlinNoise(seed + 21.7f, t * hz) - 0.5f) * 2f * roll;
        appliedPos = transform.TransformVector(off); transform.position += appliedPos;
        appliedRoll = rl; transform.rotation = transform.rotation * Quaternion.Euler(0f, 0f, rl);
    }
}

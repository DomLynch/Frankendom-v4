using UnityEngine;

// Camera impact layer ported from Frankendom V3 (src/fight/hit-impact.ts impactShove + ROLL_TUMBLE, camera-kick.ts shoveFor, camera.ts shove/tilt/settle).
// Presentation only. Every number comes from Assets/Data/camera_impact.json through ImpactTable; this file holds no tuning values.
//  - A contact (ContactRelay.OnEvent) builds a world-space offset: along the blow, sideways, down, across the frame (lateral blows, `screen`) and in/out along the view (parry and special, `push`).
//  - The offset is added AFTER the lock-camera pose (CameraRig calls Apply at the end of LateUpdate), so it never compounds into the smoothed camera position.
//  - The roll (ContactRelay.OnAction "roll", player only) tumbles the frame: a roll about the view axis leaning INTO the roll, plus a shift along the camera right and a dip, on one up-and-back sine envelope.
//  - All timing uses Time.unscaledDeltaTime so a hit-stop (timeScale / PresentationClock) does not freeze the shove or tilt (V3 draws FX on real dt while the sim is held).
public class CameraImpact : MonoBehaviour
{
    struct Shove { public float along, drop, side, hold, settle, screen, push; }

    CameraRig rig;
    Vector3 kickOffset;
    float kick, kickHold, kickRate = 1f;
    float tiltAngle, tiltFor, tiltAge, swayRight, swayDrop;

    void Awake() { rig = GetComponent<CameraRig>(); }
    void OnEnable() { ContactRelay.OnEvent += OnEvent; ContactRelay.OnAction += OnAction; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; ContactRelay.OnAction -= OnAction; }

    bool IsPlayer(DuelFighter f)
    {
        if (f == null || rig == null || rig.target == null) return false;
        return f.transform == rig.target || f.transform.IsChildOf(rig.target);
    }

    // ---- contacts -----------------------------------------------------------------------------------------------

    void OnEvent(CombatEvent e)
    {
        if (e.actor == null || e.target == null) return;
        Shove s;
        bool special;
        if (!Compute(e, out s, out special)) return;

        // the blow's heading: attacker -> defender on the ground plane (V3: a landed blow carries its heading; a block or parry takes the attacker's)
        Vector3 blow = e.target.transform.position - e.actor.transform.position; blow.y = 0f;
        if (blow.sqrMagnitude < 1e-6f) { blow = e.actor.transform.forward; blow.y = 0f; }
        blow.Normalize();
        Vector3 blowRight = new Vector3(blow.z, 0f, -blow.x);   // V3: (cos h, -sin h) for heading (sin h, cos h)

        Vector3 off = blow * s.along + blowRight * s.side + Vector3.down * s.drop;
        if (s.screen != 0f) off += transform.right * s.screen;     // across the frame (camera right, + = right)
        if (s.push != 0f) off += transform.forward * s.push;       // along the view (+ = in)
        kickOffset = off * ImpactTable.MoveMul(e.moveId);   // P1: hit-weight scale (camera_impact.json moveWeights)
        kick = 1f;
        kickHold = s.hold;
        kickRate = s.settle > 0f ? 1f / s.settle : 1f / 0.15f;
        if (special && rig != null) rig.OnSpecialLanded();
    }

    // Port of impactShove (hit-impact.ts) falling back to shoveFor (camera-kick.ts). Returns false when the event moves nothing.
    bool Compute(CombatEvent e, out Shove s, out bool special)
    {
        s = default(Shove); special = false;
        var d = ImpactTable.Get();
        float settle = d.feel.knockSettle;
        float hz = d.simHz > 0f ? d.simHz : 1f;

        // a landed kick is its own beat: straight down, no push, no side
        if (e.type == "Hit" && e.moveId == "kick")
        {
            s.drop = ImpactTable.ContactRow("kick").drop; s.settle = settle; return true;
        }

        string tier; ImpactTable.BaseKick b;
        switch (e.type)
        {
            case "GuardBroken": tier = "full"; b = ImpactTable.KickRow("guardBroken"); break;
            case "Parried":     tier = "parry"; b = ImpactTable.KickRow("parried"); break;
            case "Blocked":     tier = "block"; b = ImpactTable.KickRow(e.heavy ? "blockedHeavy" : (e.perfect ? "blockedPerfect" : "blockedLight")); break;
            case "Hit":
                if (!string.IsNullOrEmpty(e.moveId) && e.moveId.StartsWith("skill_")) { tier = "special"; special = true; b = ImpactTable.KickRow("hitHeavy"); }   // along/drop replaced below
                else if (e.heavy) { tier = "full"; b = ImpactTable.KickRow("hitHeavy"); }
                else { tier = "half"; b = ImpactTable.KickRow("hitLight"); }
                break;
            default: return false;
        }

        s.along = b.along; s.drop = b.drop; s.side = b.side; s.hold = b.holdFrames / hz; s.settle = b.settle;
        if (e.type == "Parried" && e.direction == "left") s.side = -s.side;   // the left cut's blade deflects the other way
        if (special) { s.along = d.feel.specialKickAlong; s.drop = d.feel.specialKickDrop; }   // V3: only along/drop of the special kick survive; hold 0 / settle .12 below

        string row;
        switch (tier) { case "special": row = "special"; break; case "full": row = "hitHeavy"; break; case "half": row = "hitLight"; break; case "parry": row = "parry"; break; default: row = "blockLight"; break; }
        var c = ImpactTable.ContactRow(row);
        bool attackerIsPlayer = IsPlayer(e.actor);

        if (c.push != 0f)
        {
            // V3: push = attacker === 1 ? push : -push (attacker 1 = the opponent: in toward him; the player's own blow: back)
            s.push = attackerIsPlayer ? -c.push : c.push;
            s.hold = 0f; s.settle = settle;
            return true;
        }

        bool lateral = e.direction == "left" || e.direction == "right";
        // V3: from = +1 when the blow arrives from screen right; struck===1 (the opponent was struck) == the attacker is the player
        float from = lateral ? (((e.direction == "right") == attackerIsPlayer) ? 1f : -1f) : 0f;
        s.drop = b.drop + (lateral ? 0f : c.knock);
        if (special) s.drop = d.feel.specialKickDrop + (lateral ? 0f : c.knock);
        s.screen = -from * c.knock;
        s.hold = 0f; s.settle = settle;
        return true;
    }

    // ---- roll tumble --------------------------------------------------------------------------------------------

    void OnAction(DuelFighter f, string action)
    {
        if (action != "roll" || !IsPlayer(f)) return;
        var rt = ImpactTable.Get().rollTumble;
        if (rt.seconds <= 0f) return;
        Vector3 dir = f.moveDir.sqrMagnitude > 0.01f ? f.moveDir : -f.transform.forward;   // the fighter's own roll direction rule (DuelFighter.StartRoll)
        dir.y = 0f;
        float way = Mathf.Sign(Vector3.Dot(dir, transform.right));
        if (Mathf.Abs(Vector3.Dot(dir, transform.right)) < 1e-4f) way = 1f;                  // +1: the roll goes to screen right
        tiltAngle = -way * rt.angleDeg;                                                       // lean INTO the roll: right tips clockwise
        tiltFor = rt.seconds; tiltAge = 0f;
        swayRight = way * rt.shift; swayDrop = rt.dip;
    }

    // ---- per-frame ----------------------------------------------------------------------------------------------

    /// Called by CameraRig at the end of LateUpdate, after the pose is placed: draws the roll, the kick and the shift, then steps both on real time.
    public void Apply(Transform cam)
    {
        float dt = Time.unscaledDeltaTime;
        float swing = tiltAge < tiltFor ? Mathf.Sin(Mathf.PI * (tiltAge / tiltFor)) : 0f;
        if (swing != 0f) cam.rotation = cam.rotation * Quaternion.Euler(0f, 0f, tiltAngle * swing);
        if (kick > 0f) cam.position += kickOffset * kick;
        if (swing != 0f && (swayRight != 0f || swayDrop != 0f)) cam.position += cam.right * (swayRight * swing) + Vector3.down * (swayDrop * swing);

        // after the draw: the tilt ages, the kick holds then settles (camera.ts settle)
        if (tiltAge < tiltFor) tiltAge += dt;
        if (kick > 0f)
        {
            if (kickHold > 0f) kickHold -= dt;
            else kick = Mathf.Max(0f, kick - dt * kickRate);
        }
    }
}

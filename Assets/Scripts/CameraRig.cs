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
    bool yielded;   // FN1: a finisher scene owns the camera (SteerFinisherHost); LateUpdate does nothing meanwhile, on release the lock-on pose snaps back
    public bool Yield { get { return yielded; } set { if (yielded && !value) snapped = false; yielded = value; } }
    public float framingOk = 1f;   // test-only (read by diagnostics)
    public float lockRange = 12f;
    float lockW; Vector3 camPos, lookAt; bool snapped; float lockYaw;
    float colLen, colClearT, fovDeathW; bool colLenInit, freePitchInit, hadEnemy; Vector3 fcRes, fcResLook; readonly RaycastHit[] fcHits = new RaycastHit[16];   // FREECAM v1: free camera collision smoothing, one-time start pitch, fight-end catch-up (position + lens)
    // CAMZONE v1: read-only view for the -camtrace recorder and the numbers of the last lock frame (float stores only, the pose is unchanged)
    public float LockW { get { return lockW; } } public Vector3 LookAt { get { return lookAt; } } public float dbgGap, dbgBaseH, dbgBack, dbgColWanted; public bool dbgLockOn, dbgColHit; public Collider dbgColCollider;
    [System.Serializable] class FreeRow { public float yawLimitDeg = 55f, pitchMinDeg = -5f, pitchMaxDeg = 45f; }
    FreeRow free = new FreeRow(); bool freeLoaded;
    void LoadFree() { freeLoaded = true; foreach (var p in new[] { Application.streamingAssetsPath + "/camera_free.json", Application.dataPath + "/Data/camera_free.json" }) { if (!System.IO.File.Exists(p)) continue; try { free = JsonUtility.FromJson<FreeRow>(DataPath.ReadFile(p)); } catch { } break; } }
    [System.Serializable] class ColRow { public bool enabled = false; public float radius = 0.35f, minDistFromTarget = 1.2f, minGroundY = 0f, minHeightAboveGround = 0.6f; public int[] ignoreLayers = { 8, 9 }; }
    // V68: lock-on pose multipliers as data (camera_pose.json, StreamingAssets first). 1/1/0 = the V3 pose as before.
    [System.Serializable] class CamPoseRow { public float backMul = 1f, heightMul = 1f, lookYAdd = 0f, shoulder = 1f; public LockCamRow lockCam = new LockCamRow(); public CamZone.Row zone = new CamZone.Row(); /* CAMZONE v1 */ public FreeCamRow freeCam = new FreeCamRow(); /* FREECAM v1 */ } /*HCKIT1 v93*/
    /* FREECAM v1: the free (lock-off) camera as data, camera_pose.json "freeCam". enabled=false = the public fields above (distance 3.3 / height 1.6 / shoulder 0.5) and the old instant collision, exactly as before. */
    [System.Serializable] class FreeCamRow { public bool enabled = false, ignoreSelf = true; public float distance = 3.3f, height = 1.6f, shoulder = 0.5f, startPitchDeg = 8f, colReleaseMPerS = 3f, colHoldS = 0.35f, catchRate = 2.5f, fovEaseS = 1.2f; public float fovEaseMinS = 0.05f; /* DATALAYER b8 */ }
    /* K3: the V3 closed-form constants (every default = the literal that used to be inline; camera_pose.json lockCam ships the same numbers) */
    [System.Serializable] class FoeRow { public float refHeightM = 1.8f, scaleToM = 2.1f, back = 2.4f, height = 1.6f; public bool useRosterHeight = true; }   /* foe = how much taller than refHeightM; scaleToM = metres per scale unit; roster heightM > 0 wins */
    [System.Serializable] class LockCamRow { public float backMin = 4.2f, backPerM = 0.62f, backAdd = 2.8f, backOuter = 1.3f, heightMin = 3.2f, heightPerM = 1.3f, heightScale = 0.67f, heightOuter = 1.3f, blendRate = 8f, gapMin = 0.8f, lookUp = 0.8f, lookAhead = 0.3f, xClamp = 11.5f, fovFree = 50f, fovLock = 38.6f; public FoeRow foe = new FoeRow(); public Vector3 fallbackPos = new Vector3(0f, 3f, 5f), fallbackLook = new Vector3(0f, 1f, 15f); }
    CamPoseRow pose = new CamPoseRow(); bool poseLoaded;
    void LoadPose() { LoadPoseFile(); if (!goodSet) { lastGoodPos = pose.lockCam.fallbackPos; lastGoodLook = pose.lockCam.fallbackLook; } } /*HCKIT1 v93 K3: fallback pose from data until the first good frame */
    Transform foeTr; float foeRosterH;
    float FoeTall()   /* K3: was (enemy.lossyScale.y * 2.1f - 1.8f) / 1.8f; the roster height (heightM > 0) replaces the scale guess */
    {
        var fo = pose.lockCam.foe; float m = enemy.lossyScale.y * fo.scaleToM;
        if (fo.useRosterHeight) { if (foeTr != enemy) { foeTr = enemy; foeRosterH = Roster.Get(Roster.IdOf(enemy)).heightM; } if (foeRosterH > 0f) m = foeRosterH; }
        return Mathf.Max(0f, (m - fo.refHeightM) / fo.refHeightM);
    }
    void LoadPoseFile() { poseLoaded = true; foreach (var p in new[] { Application.streamingAssetsPath + "/camera_pose.json", Application.dataPath + "/Data/camera_pose.json" }) { if (!System.IO.File.Exists(p)) continue; try { var r = JsonUtility.FromJson<CamPoseRow>(DataPath.ReadFile(p)); if (r != null) { if (r.lockCam == null) r.lockCam = new LockCamRow(); if (r.zone == null) r.zone = new CamZone.Row(); /* CAMZONE v1 */ if (r.freeCam == null) r.freeCam = new FreeCamRow(); /* FREECAM v1 */ if (r.lockCam.foe == null) r.lockCam.foe = new FoeRow(); pose = r; } } catch { } break; } }
    ColRow col = new ColRow(); bool colLoaded, goodSet; Vector3 lastGoodPos = new Vector3(0f, 3f, 5f), lastGoodLook = new Vector3(0f, 1f, 15f), lastGoodFwd = Vector3.forward;
    static bool IsFinite(Vector3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }
    void LoadCol() { colLoaded = true; foreach (var p in new[] { Application.streamingAssetsPath + "/camera_collision.json", Application.dataPath + "/Data/camera_collision.json" }) { if (!System.IO.File.Exists(p)) continue; try { col = JsonUtility.FromJson<ColRow>(DataPath.ReadFile(p)); } catch { } break; } if (col == null) col = new ColRow(); }
    Camera cam; CameraImpact impact;
    public void Recenter() { if (target) yaw = target.eulerAngles.y; }
    // FREECAM v1: the free camera collision. Same cast as before (sphere from the look-at to the camera, same mask, same minDistFromTarget) except: the player's own colliders are skipped (ignoreSelf),
    // a hit pulls the camera in AT ONCE (never through a wall), and it is released slowly (colReleaseMPerS) and only after colHoldS without a hit, so a post or a prop edge cannot make it pop in and out.
    Vector3 FreeCol(Vector3 from, Vector3 dirv, float len, int mask, Vector3 drawPos)
    {
        var fc = pose.freeCam; Vector3 dn = dirv / len; float want = len, best = float.MaxValue;
        int n = Physics.SphereCastNonAlloc(from, col.radius, dn, fcHits, len, mask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var c = fcHits[i].collider; if (c == null) continue;
            if (fc.ignoreSelf && target != null && c.transform.IsChildOf(target)) continue;
            if (fcHits[i].distance < best) { best = fcHits[i].distance; dbgColCollider = c; }
        }
        if (best < float.MaxValue) { want = Mathf.Min(len, Mathf.Max(col.minDistFromTarget, best)); dbgColHit = true; } else dbgColCollider = null;
        float dt = Time.deltaTime;
        if (!colLenInit) { colLen = want; colLenInit = true; colClearT = 0f; }
        else if (want <= colLen) { colLen = want; colClearT = 0f; }
        else { colClearT += dt; if (colClearT >= fc.colHoldS) colLen = Mathf.MoveTowards(colLen, want, fc.colReleaseMPerS * dt); }
        colLen = Mathf.Min(colLen, len);
        return colLen < len - 0.0001f ? from + dn * colLen : drawPos;
    }
    void Awake() { cam = GetComponent<Camera>(); impact = GetComponent<CameraImpact>(); if (!impact) impact = gameObject.AddComponent<CameraImpact>(); foreach (var a in System.Environment.GetCommandLineArgs()) if (a == "-autoplay") lockRange = 80f; }
    void Start() { if (Application.isPlaying && !Application.isBatchMode) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; } }

    // Hook for the presentation layer: CameraImpact calls this when a special landed (a Hit by a skill_ move; never Blocked/GuardBroken). Hit-stop timing lives in PresentationClock, not here.
    public void OnSpecialLanded() { }

    // test-only (AutoShot): fraction of feet/waist/head points of a fighter inside the safe area
    public float VisibleFraction(Transform t)
    {
        float s = FighterScale.Eff(t.lossyScale.y, t); int ok = 0;
        foreach (float h in new[] { 0.1f, 0.9f, 1.7f })
        {
            Vector3 v = cam.WorldToViewportPoint(t.position + Vector3.up * h * s);
            if (v.z > 0.1f && v.x > 0.05f && v.x < 0.95f && v.y > 0.03f && v.y < 0.97f) ok++;
        }
        return ok / 3f;
    }

    void LateUpdate()
    {
        if (yielded && CamZone.YieldStuck(pose.zone, Time.unscaledDeltaTime)) Yield = false;   // CAMZONE v1: zones only, a finisher yield that never released
        if (!target || yielded) return;
        if (!colLoaded) LoadCol();
        float dt = Time.deltaTime; if (!poseLoaded) LoadPose();
        if (CamZone.Active(pose.zone)) CamZone.Pick(pose.zone, this, target);   // CAMZONE v1: zones only: the lock target must be fighting Joan and near, else the nearest fighting one, else the free camera
        bool lockOn = lockEnabled && enemy && enemyHp && !enemyHp.Dead && Vector3.Distance(target.position, enemy.position) < lockRange; dbgLockOn = lockOn;  /* CAMZONE v1 */
        lockW = Mathf.MoveTowards(lockW, lockOn ? 1f : 0f, dt * DirectionalInput.LockEase(lockOn));   // CTRL v68: controls.json camera.easeIn / easeOut (3 / 3 without the file)

        // free camera (lock-on off): mouse yaw/pitch over the right shoulder
        if (!freeLoaded) LoadFree();
        if (!DirectionalInput.MouseReserved && !MenuUI.Open /* MENU v100: the pause menu owns the mouse */)   // CTRL v68: locked-on swipe scheme = the mouse is the attack / guard direction, not the camera
        {
            yaw += Input.GetAxis("Mouse X") * sensitivity * MenuSettings.CamSens; /* MENU v100: Settings > Gameplay > camera sensitivity */ yaw = Mathf.Clamp(Mathf.DeltaAngle(0f, yaw), -free.yawLimitDeg, free.yawLimitDeg);
            pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * sensitivity * MenuSettings.CamSens, free.pitchMinDeg, CamZone.PitchMax(pose.zone, free.pitchMaxDeg)); /* MENU v100 */ /* CAMZONE v1 */
        }
        var fcr = pose.freeCam; bool fcOn = fcr.enabled && lockW <= 0.001f;   // FREECAM v1: the free camera only; while any lock weight is left, the collision path below is the old one
        if (fcr.enabled && !freePitchInit) { freePitchInit = true; pitch = Mathf.Clamp(fcr.startPitchDeg, free.pitchMinDeg, CamZone.PitchMax(pose.zone, free.pitchMaxDeg)); }   // FREECAM v1: the scene starts at 2 deg, the zone free pitch after a lock is 8: one value
        var rot = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 focus = target.position + Vector3.up * (fcr.enabled ? fcr.height : height) + rot * Vector3.right * (fcr.enabled ? fcr.shoulder : shoulder);   // FREECAM v1
        Vector3 freePos = focus - rot * Vector3.forward * (fcr.enabled ? fcr.distance : distance), freeLook = focus;

        if (enemy)
        {
            hadEnemy = true;   // FREECAM v1
            Vector3 P = target.position, E = enemy.position;
            P.y = Mathf.Max(P.y, col.minGroundY); E.y = Mathf.Max(E.y, col.minGroundY);   // a fighter that fell out of the world must not drag the camera down with it (audit E)
            Vector3 flat = E - P; flat.y = 0f; float d = Mathf.Max(pose.lockCam.gapMin, CamZone.CapGap(pose.zone, flat.magnitude));  /* CAMZONE v1 */
            float tgtYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;          // camera behind the player, facing the enemy
            float blend = 1f - Mathf.Exp(-pose.lockCam.blendRate * dt);
            if (!snapped) lockYaw = tgtYaw; else if (CamZone.SnapYaw(pose.zone, lockW)) lockYaw = tgtYaw; else lockYaw += CamZone.YawStep(pose.zone, Mathf.DeltaAngle(lockYaw, tgtYaw) * blend, dt);  /* CAMZONE v1 */
            var lk = pose.lockCam; float foe = FoeTall();   // how much taller than a man (the enemy 2.5 m = 0.39)
            if (!poseLoaded) LoadPose();
            float back = (Mathf.Max(lk.backMin, lk.backPerM * d + lk.backAdd) + foe * lk.foe.back) * lk.backOuter * pose.backMul * MountRider.CamBackMul;   // laneHORSE v2
            float h = (Mathf.Max(lk.heightMin, lk.heightPerM * d) * lk.heightScale + foe * lk.foe.height) * lk.heightOuter * pose.heightMul * MountRider.CamHeightMul;   // laneHORSE v2
            dbgGap = d; dbgBaseH = h; dbgBack = back;  /* CAMZONE v1 */ Quaternion yr = Quaternion.Euler(0f, lockYaw, 0f);
            Vector3 fwd = yr * Vector3.forward, right = yr * Vector3.right;
            Vector3 lockPos = new Vector3(P.x, P.y + h, P.z) - fwd * back + right * pose.shoulder;
            lockPos = CamOcclusion.Shift(lockPos, right, d, lockW);   // OCCLUDE v105: DARK near-range slide so the player stops covering the foe (camera_occlusion.json fix.enabled=false = no-op)
            Vector3 mid = (P + E) * 0.5f;
            Vector3 lockLook = new Vector3(mid.x, mid.y + lk.lookUp + pose.lookYAdd, mid.z) + fwd * lk.lookAhead;   // nudged toward the foe so he sits more central
            lockLook = CamReframe.Look(lockLook, P, FighterScale.Eff(target.lossyScale.y, target), E, FighterScale.Eff(enemy.lossyScale.y, enemy), lockW);   // CAMREFRAME v102: far separation = look-at to the midpoint (camera_reframe.json)
            lockPos.x = Mathf.Clamp(lockPos.x, -CamZone.XClamp(pose.zone, lk.xClamp), CamZone.XClamp(pose.zone, lk.xClamp));  /* CAMZONE v1 */
            Vector3 wantPos = Vector3.Lerp(freePos, lockPos, lockW), wantLook = Vector3.Lerp(freeLook, lockLook, lockW);
            CameraDeath.Shape(target, enemy, enemyHp, ref wantPos, ref wantLook, camPos);   // V61B: after a death keep the look-at on the dead fighter's body (camera_death.json)
            CamFraming.Begin(P, FighterScale.Eff(target.lossyScale.y, target), E, FighterScale.Eff(enemy.lossyScale.y, enemy));   // V65CAM: both fighters stay in the safe area (camera_framing.json)
            if (!snapped) { camPos = wantPos; lookAt = wantLook; snapped = true; }
            else { camPos = Vector3.Lerp(camPos, wantPos, blend); lookAt = Vector3.Lerp(lookAt, wantLook, blend); }
            if (lockW > 0.5f) { yaw = lockYaw; pitch = CamZone.FreePitch(pose.zone, pitch); /* CAMZONE v1 */ }   // keep the free camera continuous when the lock breaks
        }
        else
        {
            if (fcr.enabled && snapped)   // FREECAM v1: the fight ended (CamZone released rig.enemy / the death camera let go): the camera eases from where it was to the free pose (the offset decays, no lag while walking), it no longer jumps
            {
                if (hadEnemy) { fcRes = camPos - freePos; fcResLook = lookAt - freeLook; hadEnemy = false; }
                float kdec = Mathf.Exp(-fcr.catchRate * dt); fcRes *= kdec; fcResLook *= kdec; camPos = freePos + fcRes; lookAt = freeLook + fcResLook;
            }
            else { camPos = freePos; lookAt = freeLook; }
            CamFraming.Clear();
        }

        var drawPos = enemy ? CamFraming.Fit(camPos, lookAt, lockW, cam.aspect) : camPos;   // V65CAM: pushed straight back along the view ray until both fighters fit
        if (enemy) drawPos = CamReframe.Pos(drawPos, lookAt, lockW, cam.aspect);   // CAMREFRAME v102: pulled in toward maxDistM while both still fit, pitch <= 4 deg
        if (!colLoaded) LoadCol();
        if (col.enabled)   // camera collision: spherecast from the look target to the camera, never ignoring walls/gate (fighter layers ignored), data camera_collision.json
        {
            Vector3 from = lookAt, dirv = drawPos - from; float len = dirv.magnitude; dbgColHit = false; dbgColWanted = len; dbgColCollider = null;  /* CAMZONE v1 */
            if (len > 0.01f)
            {
                int mask = PhysMasks.NotPhys; foreach (var l in col.ignoreLayers) mask &= ~(1 << l);
                if (!fcOn) colLenInit = false;   // FREECAM v1: the smoothed length restarts from the live cast when the free camera begins
                if (fcOn) drawPos = FreeCol(from, dirv, len, mask, drawPos);   // FREECAM v1
                else if (Physics.SphereCast(from, col.radius, dirv / len, out var ch, len, mask, QueryTriggerInteraction.Ignore))
                    { drawPos = from + dirv / len * Mathf.Max(col.minDistFromTarget, ch.distance); dbgColHit = true; dbgColCollider = ch.collider; /* CAMZONE v1 */ }
            }
        }
        if (!IsFinite(drawPos) || !IsFinite(lookAt)) { drawPos = lastGoodPos; lookAt = lastGoodLook; camPos = lastGoodPos; }
        transform.position = drawPos;
        var lookDir = lookAt - transform.position; if (lookDir.sqrMagnitude < 1e-6f) lookDir = lastGoodFwd;
        transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up);
        float dw = CameraDeath.Weight;   // FREECAM v1: CameraDeath.Shape only runs while rig.enemy is set, so once CamZone released the corpse its weight (FOV 38) stayed 1 in the free camera for good: it now eases out over fovEaseS
        if (fcr.enabled) { if (!enemy) { fovDeathW = Mathf.MoveTowards(fovDeathW, 0f, dt / Mathf.Max(fcr.fovEaseMinS, fcr.fovEaseS) /* DATALAYER b8 */); dw = fovDeathW; } else fovDeathW = dw; }
        cam.fieldOfView = CamFraming.WidenFov(transform, Mathf.Lerp(Mathf.Lerp(pose.lockCam.fovFree, pose.lockCam.fovLock, lockW), CameraDeath.Fov, dw), lockW, cam.aspect);   // V65CAM: opens up only if collision still crops a fighter
        if (impact) impact.Apply(transform); CamShake.Apply(transform);   // V65CAM: weight-scaled shake (camera_impact.json shake) on top;   // contact kick + roll tumble on top of the pose, on unscaled time (CameraImpact.cs)
        // hard floor for EVERY camera mode (impact shake / roll dip / crit / low-HP cuts included): never below ground + minHeight, never NaN
        {
            var cp = transform.position; if (!IsFinite(cp)) { cp = lastGoodPos; transform.position = cp; transform.rotation = Quaternion.LookRotation(lastGoodFwd, Vector3.up); }
            float floorY = col.minGroundY + col.minHeightAboveGround; if (cp.y < floorY) { cp.y = floorY; transform.position = cp; }
            lastGoodPos = transform.position; lastGoodLook = lookAt; lastGoodFwd = transform.forward; goodSet = true;
        }
    }
}




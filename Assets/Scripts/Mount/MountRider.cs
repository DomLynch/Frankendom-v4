using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

// laneHORSE v2 (staged DARK, UNPROVEN in Unity). A rideable mount, all tuning in mount.json (enabled=false ships dark). No sim change: it only scales the player's walkSpeed / runSpeed fields
// (read by DuelFighter.MoveTick), lifts Fighter.model so Joan sits in the saddle, and dismounts on the rules in mount.json. Summon key = controls.json id "mount" (fallback H).
// v2: the body is Dom's Tripo horse (80k-tri LOD, StreamingAssets/Mount/horse_lod.glb, no rig) loaded at runtime with glTFast like the camp NPCs; a procedural gait (bob / pitch / roll locked to the
// ground speed) stands in for animation; mount.json horse.heightM is the ONE size key (the static camp horse reads it too, HubNpcs splice); the horse must be owned (loot_save owned list, item requireOwned).
// Never runs in a harness run (MenuCfg.Harness); never touches Duel.cs, AIBrain or the save (read only). Merge: this file + mount.json (Assets/Data AND StreamingAssets) + the CameraRig hook + the HubNpcs/HubCfg scale hook.
[Serializable] public class MtHorse
{
    public string id = "warhorse_brown", glb = "Mount/horse_lod.glb";
    public float heightM = 1.75f, glbHeightM = 1.75f, seatRaiseM = 0.72f, seatBackM = 0.13f, radiusM = 0.55f, yawOffsetDeg = 0f;   // heightM = withers; seatRaiseM / seatBackM are for glbHeightM and scale with heightM
    public bool convertMaterials = true, shadows = false;
}
[Serializable] public class MtSpeed { public float walkMul = 1.6f, runMul = 2.2f; }
[Serializable] public class MtStamina { public float max = 100f, gallopDrainPerS = 14f, regenPerS = 22f, minToGallop = 15f; }
[Serializable] public class MtSummon { public float cooldownS = 3f; }
[Serializable] public class MtDismount { public string strikeAction = "strike", heavyAction = "heavy"; public string[] strikeKeys = { "Mouse0", "F" }, heavyKeys = { "G" }; /* FIX2 v1: were literals in Update */ public bool onStrike = true, onOpponent = true, inHub = true, onMenu = true, onFinisher = true; public float foeRadiusM = 0f; /* MOUNTRULE v1: 0 = any live opponent engages (old); > 0 = only an opponent closer than this many metres */ }
[Serializable] public class MtZ { public string id = ""; public float zFrom, zTo; }
[Serializable] public class MtCam { public float backMul = 1f, heightMul = 1f; }
[Serializable] public class MtGait
{
    public float walkCyclesPerM = 0.55f, gallopCyclesPerM = 0.28f, bobWalkM = 0.02f, bobGallopM = 0.06f, pitchWalkDeg = 1.5f, pitchGallopDeg = 5f, rollWalkDeg = 1.5f, rollGallopDeg = 1f, breathM = 0.004f, breathCyclesPerS = 0.3f, blendS = 0.12f, stillMps = 0.15f;
}
[Serializable] public class MtVariant { public string item = ""; public MtHorse horse = new MtHorse(); public MtSpeed speed = new MtSpeed(); public MtStamina stamina = new MtStamina(); }   // hades-env variants v1: a second horse = one row (owning item unlocks it)
[Serializable] public class MountCfg
{
    public bool enabled = false, debugLog = false, playAreaIsBoss = true; public string summonAction = "mount", summonKey = "H", requireOwned = ""; public float turnDegPerS = 200f;
    public MtHorse horse = new MtHorse(); public MtSpeed speed = new MtSpeed(); public MtStamina stamina = new MtStamina(); public MtSummon summon = new MtSummon(); public MtDismount dismount = new MtDismount();
    public MtZ[] noMountZ = new MtZ[0]; public MtCam camera = new MtCam(); public MtGait gait = new MtGait();
    public MtVariant[] variants = new MtVariant[0];   // hades-env variants v1
}

public static class MountData
{
    static MountCfg c;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { c = null; }
    public static MountCfg Get()
    {
        if (c != null) return c; c = new MountCfg();
        string txt = DataPath.ReadText("mount.json");
        if (txt != null) { try { var k = JsonUtility.FromJson<MountCfg>(txt); if (k != null) c = k; } catch (Exception e) { Debug.LogWarning("[Mount] mount.json unreadable, mount off: " + e.Message); c = new MountCfg(); } }
        if (c.horse == null) c.horse = new MtHorse(); if (c.speed == null) c.speed = new MtSpeed(); if (c.stamina == null) c.stamina = new MtStamina(); if (c.summon == null) c.summon = new MtSummon();
        if (c.dismount == null) c.dismount = new MtDismount(); if (c.noMountZ == null) c.noMountZ = new MtZ[0]; if (c.camera == null) c.camera = new MtCam(); if (c.gait == null) c.gait = new MtGait();
        if (c.requireOwned == null) c.requireOwned = ""; if (c.horse.glb == null) c.horse.glb = ""; if (c.variants == null) c.variants = new MtVariant[0];
        return c;
    }
    /// <summary>True when world z is inside a boss arena (mount.json noMountZ rows, or a play_areas.json row when playAreaIsBoss).</summary>
    public static bool BossZ(float z)
    {
        var k = Get(); foreach (var r in k.noMountZ) if (r != null && z >= r.zFrom && z <= r.zTo) return true;
        return k.playAreaIsBoss && PlayArea.Row(z) != null;
    }
    /// <summary>The ONE size key: mount.json horse.heightM over the height the glb was exported at (glbHeightM). 1 when they match. Read by MountRider and the static camp horse (HubNpcs).</summary>
    public static float HorseScale() { return HorseScale(Get().horse); }
    public static float HorseScale(MtHorse h) { return h != null && h.heightM > 0f && h.glbHeightM > 0f ? h.heightM / h.glbHeightM : 1f; }   // hades-env variants v1
    /// <summary>hades-env variants v1: the LAST variants[] row whose item the player owns (later rows are the better horse), or null = the plain horse.</summary>
    public static MtVariant Variant() { var k = Get(); MtVariant v = null; foreach (var r in k.variants) if (r != null && !string.IsNullOrEmpty(r.item) && LootSave.Owns(r.item)) v = r; return v; }
    /// <summary>StreamingAssets/horse.glb path, or null when the file is missing.</summary>
    public static string GlbFile() { return GlbFile(Get().horse); }
    public static string GlbFile(MtHorse h)   // hades-env variants v1
    {
        var g = h != null ? h.glb : null; if (string.IsNullOrEmpty(g)) return null;
        try { var p = System.IO.Path.Combine(Application.streamingAssetsPath, g); return System.IO.File.Exists(p) ? p : null; } catch (Exception) { return null; }
    }
}

[DefaultExecutionOrder(-100)]   // after PlayerBrain (-200) set df.run / moveDir, before DuelFighter reads them
public sealed class MountRider : MonoBehaviour
{
    public static MountRider Inst; float nextBind;   // FIX2 v1: nextBind throttles the player search
    public static bool Mounted { get { return Inst != null && Inst.mounted; } }
    // MOUNTPROOF v1 (unity-build): test hook for MountProof.cs only (-mountproof launch flag). ProofKey = one virtual press of the summon key; ProofOwn = treat the horse as owned (the save is never written). Both false in normal play.
    public static bool ProofKey, ProofOwn;
    public static bool ProofReady { get { return Inst != null && Inst.loaded && Inst.df != null; } }
    public static string ProofInfo() { var i = Inst; if (i == null) return "no rider"; return "mounted=" + i.mounted + " loaded=" + i.loaded + " speedNow=" + i.speedNow.ToString("F2") + " stam=" + i.stam.ToString("F0") + " horse=" + (i.horse != null && i.horse.activeSelf ? i.horse.transform.position.ToString("F2") : "off") + " player=" + (i.df != null ? i.df.transform.position.ToString("F2") : "none") + " scale=" + (i.loaded ? MountData.HorseScale(i.H).ToString("F3") : "-"); }
    public static string ProofRefuse() { var i = Inst; return i == null || i.df == null ? "no rider" : i.Refuse(); }
    bool ProofTake() { bool k = ProofKey; ProofKey = false; return k; }
    /// <summary>FIX2 v1: the ride's speed multiplier, read by DuelFighter.MoveTick (and ChaseAgent for the player) ON THE SIM TICK. walkSpeed / runSpeed fields are no longer written from Update, so the speed a tick sees never depends on frame timing. 1 when not mounted or for any other fighter.</summary>
    public static float SpeedMul(DuelFighter f, bool run) { var i = Inst; if (i == null || !i.mounted || i.K == null || f != i.df) return 1f; var S = i.cur != null ? i.cur.speed : i.K.speed; return run ? S.runMul : S.walkMul; }   // hades-env variants v1
    /// <summary>For a CameraRig hook: 1 on foot, mount.json camera.* while mounted.</summary>
    public static float CamBackMul { get { return Mounted ? MountData.Get().camera.backMul : 1f; } }
    public static float CamHeightMul { get { return Mounted ? MountData.Get().camera.heightMul : 1f; } }

    MtVariant cur; MtHorse H { get { return cur != null ? cur.horse : K.horse; } } MtStamina St { get { return cur != null ? cur.stamina : K.stamina; } }   // hades-env variants v1: cur = the owned variant (null = plain)
    MountCfg K; DuelFighter df; Fighter fi; GameObject horse; Transform hBody; GLTFast.GltfImport imp; List<Material> mats;
    bool mounted, exhausted, modelSaved, loadStarted, loaded, haveLast; float stam, nextSummon, baseWalk, baseRun, horseYaw, phase, ampBlend, speedNow, breath, bob, pitch, roll; Vector3 baseModelPos, lastPos;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Inst = null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Inst != null) return; if (!MountData.Get().enabled || MenuCfg.Harness) return;
        var go = new GameObject("MountRider"); DontDestroyOnLoad(go); Inst = go.AddComponent<MountRider>(); go.AddComponent<MountFollow>();
    }
    void Awake() { K = MountData.Get(); stam = St.max; }
    void OnDestroy()
    {
        if (horse != null) Destroy(horse);
        if (mats != null) foreach (var m in mats) if (m != null) Destroy(m);
        if (imp != null) { try { imp.Dispose(); } catch (Exception) { } }
    }

    bool Bind()
    {
        if (df != null) return true;
        if (Time.unscaledTime < nextBind) return false; nextBind = Time.unscaledTime + 0.5f;   // FIX2 v1: was FindObjectsByType every frame while no player exists
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) if (f.name.Contains(Roster.PlayerObject)) { df = f; fi = f.GetComponent<Fighter>(); break; }
        return df != null;
    }

    bool Owned() { return ProofOwn || string.IsNullOrEmpty(K.requireOwned) || LootSave.Owns(K.requireOwned) || MountData.Variant() != null; }   // hades-env variants v1: owning only the war horse is enough

    string Refuse()
    {
        if (!Owned()) return "no horse owned";
        if (!loaded) return "horse not loaded yet";
        if (df.Dead) return "dead";
        if (MountData.BossZ(df.transform.position.z)) return "boss arena";
        if (K.dismount.onOpponent && df.opponent != null && !df.opponent.Dead) { float fr = K.dismount.foeRadiusM; if (fr <= 0f || (df.opponent.transform.position - df.transform.position).sqrMagnitude <= fr * fr) return "fight engaged"; }   // MOUNTRULE v1: foeRadiusM (mount.json dismount) limits "engaged" to a boss that is actually close
        if (K.dismount.inHub && (HubSafe.PlayerInside() || MarketSafe.NoCombat)) return "safe zone";
        if (K.dismount.onMenu && MenuUI.Open) return "menu";
        if (K.dismount.onFinisher && Duel.Frozen) return "finisher";
        return null;
    }

    void Update()
    {
        if (!Bind()) return; float dt = Time.deltaTime;
        if (loaded && !mounted && MountData.Variant() != cur) { if (horse != null) Destroy(horse); if (imp != null) { try { imp.Dispose(); } catch (Exception) { } imp = null; } horse = null; hBody = null; loaded = false; loadStarted = false; }   // hades-env variants v1: a new horse was bought: reload
        if (!loadStarted && Owned()) { loadStarted = true; var t = LoadHorse(); }   // paid for only once the horse is owned
        string why = Refuse(); bool key = InputMap.Down(K.summonAction, KeyCodeOf(K.summonKey)) | ProofTake();
        if (mounted)
        {
            if (why != null || key || (K.dismount.onStrike && StrikeDown())) { Dismount(why ?? "key"); return; }
            // horse stamina: the run key is a gallop. Empty = no gallop until minToGallop is back.
            if (df.run) { if (exhausted || stam <= 0f) df.run = false; else { stam -= St.gallopDrainPerS * dt; if (stam <= 0f) { stam = 0f; exhausted = true; df.run = false; } } }
            else { stam = Mathf.Min(St.max, stam + St.regenPerS * dt); if (exhausted && stam >= St.minToGallop) exhausted = false; }
            df.guardHeld = false;   // v1: no fighting from the saddle
            Gait(dt);
        }
        else if (key && Time.time >= nextSummon)
        {
            if (why != null) { if (K.debugLog) Debug.Log("[Mount] refused: " + why); return; }
            nextSummon = Time.time + K.summon.cooldownS; Mount();
        }
    }

    // FIX2 v1: the strike / heavy actions and their fallback keys are mount.json dismount.* (defaults = the old literals)
    KeyCode[] strikeK, heavyK;
    static KeyCode[] KeysOf(string[] names) { var l = new List<KeyCode>(); if (names != null) foreach (var n in names) { try { l.Add((KeyCode)Enum.Parse(typeof(KeyCode), n, true)); } catch (Exception) { } } return l.ToArray(); }
    static bool DownKeys(string id, KeyCode[] k) { if (k.Length >= 2) return InputMap.Down(id, k[0], k[1]); if (k.Length == 1) return InputMap.Down(id, k[0]); return InputMap.Down(id, KeyCode.None); }
    bool StrikeDown() { var D = K.dismount; if (strikeK == null) { strikeK = KeysOf(D.strikeKeys); heavyK = KeysOf(D.heavyKeys); } return DownKeys(D.strikeAction, strikeK) || DownKeys(D.heavyAction, heavyK); }

    static KeyCode KeyCodeOf(string s) { try { return (KeyCode)Enum.Parse(typeof(KeyCode), s, true); } catch (Exception) { return KeyCode.H; } }

    async Task LoadHorse()
    {
        cur = MountData.Variant(); stam = St.max; string file = MountData.GlbFile(H); if (file == null) { Debug.LogWarning("[Mount] horse glb not found (mount.json horse.glb under StreamingAssets): no mount"); return; }
        try
        {
            var root = new GameObject("MountHorse"); DontDestroyOnLoad(root); root.SetActive(false);
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            imp = new GLTFast.GltfImport();
            if (!await imp.LoadFile(file) || !await imp.InstantiateMainSceneAsync(body.transform)) { Debug.LogWarning("[Mount] horse glb failed to load: " + file); Destroy(root); return; }
            foreach (var c in root.GetComponentsInChildren<Collider>(true)) Destroy(c);
            var rl = new List<Renderer>(root.GetComponentsInChildren<Renderer>(true));
            foreach (var r in rl) r.shadowCastingMode = H.shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            if (H.convertMaterials) mats = GearMaterials.Convert(rl);   // glTFast shader-graph materials -> HDRP/Lit, same as the camp NPCs
            body.transform.localScale = Vector3.one * MountData.HorseScale(H);
            horse = root; hBody = body.transform; loaded = true;
            if (K.debugLog) Debug.Log("[Mount] horse loaded, scale " + MountData.HorseScale(H).ToString("F3"));
        }
        catch (Exception e) { Debug.LogWarning("[Mount] horse load error: " + e.Message); }
    }

    void Mount()
    {
        mounted = true; exhausted = false; baseWalk = df.walkSpeed; baseRun = df.runSpeed; phase = 0f; ampBlend = 0f; haveLast = false; bob = 0f; pitch = 0f; roll = 0f;
        /* FIX2 v1: the multiplier is MountRider.SpeedMul on the sim tick, no field write */
        if (fi != null && fi.model != null) { baseModelPos = fi.model.localPosition; modelSaved = true; }
        horseYaw = df.transform.eulerAngles.y; horse.SetActive(true);
        PlaceHorse(); if (K.debugLog) Debug.Log("[Mount] mounted " + H.id);
    }

    void Dismount(string why)
    {
        mounted = false; /* FIX2 v1: nothing to restore, the fields were never written */
        if (modelSaved && fi != null && fi.model != null) fi.model.localPosition = baseModelPos;
        if (horse != null) horse.SetActive(false);
        if (K.debugLog) Debug.Log("[Mount] dismounted: " + why);
    }

    /// <summary>Procedural gait (the glb has no rig): phase advances with the ground speed, bob / pitch / roll blend with how fast the horse moves.</summary>
    void Gait(float dt)
    {
        var G = K.gait; var p = df.transform.position;
        if (haveLast && dt > 1e-5f) { var d = p - lastPos; d.y = 0f; speedNow = Mathf.Lerp(speedNow, d.magnitude / dt, Mathf.Clamp01(dt / Mathf.Max(1e-3f, G.blendS))); }
        lastPos = p; haveLast = true;
        bool gallop = df.run && speedNow > G.stillMps; float moving = speedNow > G.stillMps ? 1f : 0f;
        ampBlend = Mathf.MoveTowards(ampBlend, moving, dt / Mathf.Max(1e-3f, G.blendS));
        float cycles = (gallop ? G.gallopCyclesPerM : G.walkCyclesPerM) * speedNow * dt;   // stride cycles are per metre travelled: the feet-to-ground ratio stays put at any speed
        phase = Mathf.Repeat(phase + cycles * 2f * Mathf.PI, 2f * Mathf.PI); breath = Mathf.Repeat(breath + dt * G.breathCyclesPerS * 2f * Mathf.PI, 2f * Mathf.PI);
        float bobA = gallop ? G.bobGallopM : G.bobWalkM, pitA = gallop ? G.pitchGallopDeg : G.pitchWalkDeg, rolA = gallop ? G.rollGallopDeg : G.rollWalkDeg;
        bob = Mathf.Abs(Mathf.Sin(phase)) * bobA * ampBlend + Mathf.Sin(breath) * G.breathM * (1f - ampBlend);
        pitch = Mathf.Sin(phase * 2f) * pitA * ampBlend; roll = Mathf.Sin(phase) * rolA * ampBlend;
    }

    public void PlaceHorse()
    {
        if (horse == null || df == null || !mounted) return; var p = df.transform.position; float sc = MountData.HorseScale(H);
        if (df.moveDir.sqrMagnitude > 0.01f) { float want = Mathf.Atan2(df.moveDir.x, df.moveDir.z) * Mathf.Rad2Deg; horseYaw = Mathf.MoveTowardsAngle(horseYaw, want, K.turnDegPerS * Time.deltaTime); }   // hc:ok 0.01 = move threshold / divide guard
        var rot = Quaternion.Euler(0f, horseYaw + H.yawOffsetDeg, 0f);
        horse.transform.SetPositionAndRotation(p + rot * Vector3.forward * (H.seatBackM * sc), rot);   // glb pivot = hooves, centre of the barrel; the saddle sits seatBackM behind it
        if (hBody != null) { hBody.localPosition = Vector3.up * bob; hBody.localRotation = Quaternion.Euler(pitch, 0f, roll); }
        if (fi != null && fi.model != null) fi.model.localPosition = baseModelPos + Vector3.up * (H.seatRaiseM * sc / Mathf.Max(0.01f, df.transform.lossyScale.y) + bob);   // hc:ok 0.01 = move threshold / divide guard
    }
}

[DefaultExecutionOrder(700)]   // after ArenaLimits (600): the horse follows the clamped position
public sealed class MountFollow : MonoBehaviour
{
    void LateUpdate() { if (MountRider.Inst != null && MountRider.Mounted) MountRider.Inst.PlaceHorse(); }
}

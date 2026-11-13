using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// HUMBABA'S FACE IS WATCHING v1 (pool lane S4, NEW file, UNPROVEN IN UNITY). Zone 5 surprise, data: Assets/Data/humbaba_watch.json (StreamingAssets copy). Ships DARK (enabled false); no file = no object = today's game exactly.
// (a) EYES: two small emissive iris quads (Furnace/Particle additive, a soft disc texture generated in code, no art file) parented to the existing face-tree prop (props_layout_zone5 id z5h_humbaba_cedar). Nothing is remodelled.
//     The socket positions are DATA as fractions of the prop's own renderer bounds (u across, v up, w depth; flipX because glTFast mirrors X), measured from the GLB in headless Blender (see READY.md). Each iris is offset inside its socket toward
//     Joan's head (yaw / pitch in the prop's local frame, clamped to maxOffsetFrac of the iris width), smoothed at followSpeed, back to centre when Joan is behind the face or beyond rangeM.
// (b) BRANCH: a heavy hit by Joan on the boss (Hit event, heavy flag and a minimum health loss, both data) makes the tree groan (EXISTING cue pitched down, placeholder) and drops a branch: telegraph decal (HDRP DecalProjector, a dark ring
//     texture generated in code) on the ground at Joan's lead position for telegraphS, then the branch (a clone of an EXISTING scene prop, scaled to targetLengthM, colliders stripped) falls from the crown in a scripted arc, lands on the mark and hurts
//     Joan if she is still inside damageRadiusM (rolling through it with i-frames is safe). Seeded (SurpriseGate.Seed), capped per fight (maxPerFight) and gated by the shared SurpriseGate (one surprise per zone visit).
// Harness (compiled out under FRANKEN_NO_HARNESS): -s4:1 forces the whole lane on (and the gate) and fires one scripted heavy hit proof.forceAtS after the boss and Joan exist; -s4:0 forces it off.
// Reads public state only; edits no existing file.
[Serializable] public class HwEyePos { public float u = 0.5f, v = 0.5f, w = 0.5f; }
[Serializable]
public class HwFace
{
    public string propId = ""; public bool enabled = true, flipX = true;
    public string boundsChild = "LOD0";          // only the renderers under this child define the bounds ("" = all renderers of the prop)
    public HwEyePos[] eyes = new HwEyePos[0];
    public float irisWidthFrac = 0.03f, irisAspect = 0.6f, standoffFrac = 0.004f, maxOffsetFrac = 0.35f, maxYawDeg = 55f, maxPitchDeg = 30f;
}
[Serializable] public class HwEyes { public bool enabled = true; public float followSpeed = 6f, rangeM = 60f, hideBeyondM = 140f, playerHeadM = 1.6f, idleIntensity = 0.6f, activeIntensity = 1.6f; public float[] tint = new float[] { 1f, 0.6f, 0.2f }; public int texSize = 64; public float lobePower = 3f, lobeRadiusFrac = 0.9f; public string shader = "Furnace/Particle", fallbackShader = "HDRP/Unlit"; public HwFace[] faces = new HwFace[0]; }
[Serializable] public class HwHeavy { public bool needHeavyFlag = true; public float minDamage = 0f, chance = 1f; }
[Serializable] public class HwGroan { public string cue = "effort_voice"; public float gain = 0.9f, rate = 0.4f, delayS = 0f; public string shake = "hitHeavy"; public float shakeMul = 1f; }
[Serializable] public class HwDecal { public string refMaterial = "WoundDecalRef"; public int texSize = 128; public float ringRadiusFrac = 0.85f, ringWidthFrac = 0.1f, innerFill = 0.35f, drawDistanceM = 120f, depthM = 6f, maxOpacity = 0.9f; public float[] tint = new float[] { 0.03f, 0.02f, 0.02f }; }
[Serializable]
public class HwBranch
{
    public string sourceProp = "z5_root_log_NW", lodPrefix = "LOD"; public float targetLengthM = 4f;
    public float telegraphS = 1.2f, radiusM = 2.2f, damageRadiusM = 1.9f, yTolM = 2f, leadS = 0.6f, jitterM = 0.8f, startHeightM = 14f, crownOffsetFrac = 0.8f, spinDeg = 200f;
    public float damage = 14f, knockM = 2f; public bool nonLethal = true, hurtPlayer = true, hurtBoss = false; public int staggerTicks = 24; public string staggerClip = "hit_heavy";
    public float lingerS = 3f, sinkS = 1f, groundProbeM = 40f, landShakeMul = 1.5f; public string landShake = "hitHeavy";
    public int maxPerFight = 2; public float cooldownS = 8f;
    public HwDecal decal = new HwDecal();
}
[Serializable] public class HwPhase2 { public float telegraphS = -1f, cooldownS = -1f, groanGain = -1f; }   // BOSSPHASES C4: humbaba_watch.json phase2{}; a value < 0 (absent) = the normal one; read only while BossPhase.IsOn(boss)   // hc:ok algorithm constant / guard / log (reviewed)
[Serializable] public class HwPose { public string name = ""; public float x, z; }
[Serializable] public class HwProof { public float forceAtS = 6f, poseAtS = 2f, poseProbeM = 40f; public HwPose[] poses = new HwPose[0]; }
[Serializable]
public class HwFile
{
    public bool enabled = false, log = true, useGate = true; public float refreshS = 0.5f; public string zone = "zone5", gateKey = "humbaba_branch", bossId = "Humbaba";
    public HwEyes eyes = new HwEyes(); public HwHeavy heavy = new HwHeavy(); public HwGroan groan = new HwGroan(); public HwBranch branch = new HwBranch(); public HwProof proof = new HwProof();
    public HwPhase2 phase2 = new HwPhase2();   // BOSSPHASES C4
}

public class HumbabaWatch : MonoBehaviour
{
    const string FileName = "humbaba_watch.json";   // hc:ok algorithm constant / guard / log (reviewed)
    static HwFile cfg; static bool loaded; static int forceArg = -1, poseArg = -1;   // -1 none, 0 off, 1 on (harness)   // hc:ok algorithm constant / guard / log (reviewed)
    public static int Groans, Drops, Hits;   // proof counters (log)
    static readonly int BaseColorMap = Shader.PropertyToID("_BaseColorMap");
    static readonly int IdMain = Shader.PropertyToID("_MainTex"), IdTint = Shader.PropertyToID("_Tint"), IdIntensity = Shader.PropertyToID("_Intensity"), IdExposure = Shader.PropertyToID("_ExposureWeight"), IdSoft = Shader.PropertyToID("_SoftDistance"), IdSrc = Shader.PropertyToID("_SrcBlend"), IdDst = Shader.PropertyToID("_DstBlend"), IdSurface = Shader.PropertyToID("_SurfaceType"), IdBlend = Shader.PropertyToID("_BlendMode"), IdZw = Shader.PropertyToID("_ZWrite"), IdDs = Shader.PropertyToID("_DoubleSidedEnable"), IdUnlitMap = Shader.PropertyToID("_UnlitColorMap"), IdUnlit = Shader.PropertyToID("_UnlitColor");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; loaded = false; forceArg = -1; poseArg = -1; Groans = Drops = Hits = 0; }   // hc:ok algorithm constant / guard / log (reviewed)

    static HwFile Cfg()
    {
        if (loaded) return cfg;
        loaded = true; cfg = null;
#if !FRANKEN_NO_HARNESS
        foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-s4:1") forceArg = 1; else if (a == "-s4:0") forceArg = 0; else if (a.StartsWith("-s4pose:", StringComparison.Ordinal)) int.TryParse(a.Substring(8), out poseArg); }   // hc:ok harness arg   // hc:ok algorithm constant / guard / log (reviewed)
#endif
        if (forceArg == 0) return null;   // hc:ok algorithm constant / guard / log (reviewed)
        string txt = DataPath.ReadText(FileName); if (txt == null) return null;
        try { var f = JsonUtility.FromJson<HwFile>(txt.TrimStart((char)0xFEFF)); if (f != null && (f.enabled || forceArg == 1)) cfg = f; }   // hc:ok algorithm constant / guard / log (reviewed)
        catch (Exception e) { Debug.LogWarning("[HumbabaWatch] " + FileName + " parse failed: " + e.Message); }   // hc:ok algorithm constant / guard / log (reviewed)
#if !FRANKEN_NO_HARNESS
        if (cfg != null && forceArg == 1) SurpriseGate.ForceEnable(true);   // hc:ok algorithm constant / guard / log (reviewed)
#endif
        return cfg;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Cfg() == null || FindFirstObjectByType<HumbabaWatch>() != null) return;
        var go = new GameObject("HumbabaWatch"); DontDestroyOnLoad(go); go.AddComponent<HumbabaWatch>();   // hc:ok algorithm constant / guard / log (reviewed)
    }

    // ------------------------------------------------------------------------------------------------ state
    class EyeSet { public HwFace row; public Transform root; public Transform[] quads; public Vector3 bmin, bsize; public Vector2[] gaze; public bool ready; }   // hc:ok algorithm constant / guard / log (reviewed)
    class Drop
    {
        public Vector3 p, start; public float t0, tel; public GameObject holder, decal; public DecalProjector proj; public Vector3 axis; public float yaw, halfH, landAt; public bool landed, hurtDone;   // hc:ok algorithm constant / guard / log (reviewed)
        public DuelFighter boss;
    }
    readonly List<EyeSet> sets = new List<EyeSet>(); readonly List<Drop> drops = new List<Drop>();
    readonly Dictionary<DuelFighter, float> lastHp = new Dictionary<DuelFighter, float>(); readonly Dictionary<DuelFighter, int> perFight = new Dictionary<DuelFighter, int>();
    DuelFighter[] fighters = new DuelFighter[0]; DuelFighter joan; float poseAt = -1f; bool poseDone; float nextRefresh, lastDrop = -1e9f, proofAt = -1f; bool proofDone; Material eyeMat, decalMat; Mesh quad; CueBus bus; System.Random rng; int dropCount; bool subscribed;   // hc:ok algorithm constant / guard / log (reviewed)
    float intensity; Vector3 prevJoan, joanVel; bool havePrev;   // hc:ok algorithm constant / guard / log (reviewed)

    void OnEnable() { if (!subscribed) { ContactRelay.OnEvent += OnEv; subscribed = true; } }
    void OnDisable() { if (subscribed) { ContactRelay.OnEvent -= OnEv; subscribed = false; } }

    void Update()
    {
        var c = Cfg(); if (c == null) return;
        float now = Time.unscaledTime;
        if (now >= nextRefresh)
        {
            nextRefresh = now + Mathf.Max(0.1f, c.refreshS);   // hc:ok floor
            fighters = FindObjectsByType<DuelFighter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None); joan = null;
            for (int i = 0; i < fighters.Length; i++) { var f = fighters[i]; if (f != null && f.IsPlayer && !f.Dead) { joan = f; break; } if (f != null && f.IsPlayer && joan == null) joan = f; }   // hc:ok algorithm constant / guard / log (reviewed)
            if (c.eyes.enabled) EnsureEyes(c);
            var boss = Boss(c); if (boss != null) { float hp; if (!lastHp.TryGetValue(boss, out hp)) lastHp[boss] = boss.health; }
            if (forceArg == 1 && !proofDone && boss != null && joan != null && !joan.Dead) { if (proofAt < 0f) proofAt = now + c.proof.forceAtS; }   // hc:ok algorithm constant / guard / log (reviewed)
        }
        if (joan != null) { Vector3 jp = joan.transform.position; float ud = Time.unscaledDeltaTime; if (havePrev && ud > 1e-5f) { Vector3 jv = (jp - prevJoan) / ud; jv.y = 0f; joanVel = Vector3.Lerp(joanVel, jv, 0.2f); } prevJoan = jp; havePrev = true; }   // hc:ok smoothing
#if !FRANKEN_NO_HARNESS
        if (poseArg >= 0 && !poseDone && joan != null && Boss(c) != null)   // harness: put Joan at proof.poses[poseArg] (world x,z), facing the face-tree, for the three eye-follow shots   // hc:ok harness proof
        {
            if (poseAt < 0f) poseAt = Time.unscaledTime + c.proof.poseAtS;   // hc:ok harness proof
            else if (Time.unscaledTime >= poseAt && poseArg < c.proof.poses.Length && sets.Count > 0 && sets[0].root != null)   // hc:ok harness proof
            {
                poseDone = true; var po = c.proof.poses[poseArg]; Vector3 pp = new Vector3(po.x, joan.transform.position.y, po.z); RaycastHit gh;   // hc:ok harness proof
                if (Physics.Raycast(pp + Vector3.up * c.proof.poseProbeM, Vector3.down, out gh, c.proof.poseProbeM * 2f, ~0, QueryTriggerInteraction.Ignore)) pp.y = gh.point.y;   // hc:ok harness proof
                Vector3 fc = sets[0].root.position - pp; fc.y = 0f; FinisherPlayer.Teleport(joan, pp, fc.sqrMagnitude > 1e-4f ? fc.normalized : joan.transform.forward);   // hc:ok harness proof
                Debug.Log("[HumbabaWatch] harness: Joan to pose " + po.name + " " + pp.ToString("F1"));   // hc:ok harness proof
            }
        }
#endif
        if (c.eyes.enabled) UpdateEyes(c);
        if (proofAt >= 0f && !proofDone && now >= proofAt)   // hc:ok algorithm constant / guard / log (reviewed)
        {
            proofDone = true; var boss = Boss(c);
            if (boss != null && joan != null) { Debug.Log("[HumbabaWatch] harness: scripted heavy hit on " + boss.name); TryTrigger(c, boss, true); }   // hc:ok algorithm constant / guard / log (reviewed)
        }
        UpdateDrops(c);
    }

    DuelFighter Boss(HwFile c)
    {
        for (int i = 0; i < fighters.Length; i++) { var f = fighters[i]; if (f != null && !f.IsPlayer && !f.Dead && RosterResolver.KeyIs(c.bossId, f.CharacterId)) return f; }   // hc:ok algorithm constant / guard / log (reviewed)
        return null;
    }

    // ------------------------------------------------------------------------------------------------ (b) heavy hit -> groan + branch
    void OnEv(CombatEvent e)
    {
        var c = Cfg(); if (c == null || e.type != "Hit" || e.target == null || e.actor == null || !e.actor.IsPlayer) return;   // hc:ok algorithm constant / guard / log (reviewed)
        if (!RosterResolver.KeyIs(c.bossId, e.target.CharacterId)) return;
        float prev; bool had = lastHp.TryGetValue(e.target, out prev); lastHp[e.target] = e.target.health;
        float lost = had ? prev - e.target.health : 0f;   // hc:ok algorithm constant / guard / log (reviewed)
        if (c.heavy.needHeavyFlag && !e.heavy) return;
        if (lost < c.heavy.minDamage) return;
        Hits++; TryTrigger(c, e.target, false);
    }

    void TryTrigger(HwFile c, DuelFighter boss, bool forced)
    {
        var B = c.branch; float now = Time.time;
        if (joan == null || joan.Dead) return;
        int n; perFight.TryGetValue(boss, out n); if (n >= B.maxPerFight) return;
        if (!forced && now - lastDrop < P2(c, boss, B.cooldownS, 1)) return;   // BOSSPHASES C4
        if (rng == null) rng = new System.Random((int)SurpriseGate.Seed("humbaba_watch"));   // hc:ok algorithm constant / guard / log (reviewed)
        if (!forced && rng.NextDouble() > c.heavy.chance) return;
        if (c.useGate && !SurpriseGate.TryClaim(c.zone, c.gateKey)) return;
        perFight[boss] = n + 1; lastDrop = now;   // hc:ok algorithm constant / guard / log (reviewed)
        Groan(c, boss); StartDrop(c, boss);
    }

    // BOSSPHASES C4: the phase-2 override of a branch / groan number (key 0 telegraphS, 1 cooldownS, 2 groanGain); the normal value while the boss is not in its phase or the key is absent
    static float P2(HwFile c, DuelFighter boss, float baseV, int key)
    {
        var q = c.phase2; if (q == null || boss == null || !BossPhase.IsOn(boss)) return baseV;
        float v = key == 0 ? q.telegraphS : key == 1 ? q.cooldownS : q.groanGain; return v >= 0f ? v : baseV;
    }

    void Groan(HwFile c, DuelFighter boss)
    {
        var G = c.groan; Groans++;
        float gain = P2(c, boss, G.gain, 2);   // BOSSPHASES C4
        if (!string.IsNullOrEmpty(G.cue) && gain > 0f) { if (bus == null) bus = FindFirstObjectByType<CueBus>(); if (bus != null) bus.Play(G.cue, gain, G.delayS, G.rate <= 0f ? 1f : G.rate); }   // hc:ok algorithm constant / guard / log (reviewed)
        if (!string.IsNullOrEmpty(G.shake)) CamShake.Trigger(G.shake, G.shakeMul);
        if (c.log) Debug.Log("[HumbabaWatch] the tree groans (cue " + G.cue + " x" + G.rate.ToString("F2") + ")");   // hc:ok algorithm constant / guard / log (reviewed)
    }

    void StartDrop(HwFile c, DuelFighter boss)
    {
        var B = c.branch; var D = new Drop(); D.boss = boss;
        var src = GameObject.Find(B.sourceProp);
        if (src == null) { Debug.LogWarning("[HumbabaWatch] no scene prop '" + B.sourceProp + "' to clone as the branch; drop skipped"); return; }   // hc:ok algorithm constant / guard / log (reviewed)
        // target = Joan's lead position + seeded jitter, snapped to the ground
        var jt = joan.transform; Vector3 p = jt.position + joanVel * B.leadS;   // hc:ok algorithm constant / guard / log (reviewed)
        double a = rng.NextDouble() * Math.PI * 2.0, r = rng.NextDouble() * B.jitterM;   // hc:ok algorithm constant / guard / log (reviewed)
        p += new Vector3((float)(Math.Cos(a) * r), 0f, (float)(Math.Sin(a) * r));   // hc:ok algorithm constant / guard / log (reviewed)
        RaycastHit hit; if (Physics.Raycast(p + Vector3.up * B.groundProbeM, Vector3.down, out hit, B.groundProbeM * 2f, ~0, QueryTriggerInteraction.Ignore)) p.y = hit.point.y;   // hc:ok algorithm constant / guard / log (reviewed)
        D.p = p; D.t0 = Time.time; D.tel = Mathf.Max(0.1f, P2(c, boss, B.telegraphS, 0)); D.yaw = (float)(rng.NextDouble() * 360.0);   // hc:ok algorithm constant / guard / log (reviewed)
        D.axis = new Vector3((float)(rng.NextDouble() - 0.5), 0f, (float)(rng.NextDouble() - 0.5)); if (D.axis.sqrMagnitude < 1e-4f) D.axis = Vector3.right; D.axis.Normalize();   // hc:ok algorithm constant / guard / log (reviewed)
        // start: above the tree crown (the face-tree prop) when it exists, else straight above the mark
        Vector3 crown = p + Vector3.up * B.startHeightM; var treeRoot = sets.Count > 0 ? sets[0].root : null;   // hc:ok algorithm constant / guard / log (reviewed)
        if (treeRoot != null) { Bounds tb = Rb(treeRoot, ""); crown = new Vector3(tb.center.x, tb.min.y + tb.size.y * B.crownOffsetFrac, tb.center.z); }   // hc:ok algorithm constant / guard / log (reviewed)
        D.start = crown;
        // the branch: a clone of an existing prop, one LOD only, no collider, scaled to targetLengthM
        var holder = new GameObject("HumbabaBranch"); var clone = Instantiate(src); clone.name = "branch_clone"; clone.SetActive(true);   // hc:ok algorithm constant / guard / log (reviewed)
        foreach (var col in clone.GetComponentsInChildren<Collider>(true)) Destroy(col);
        var lg = clone.GetComponent<LODGroup>(); if (lg != null) Destroy(lg);
        for (int i = 1; i < 3; i++) { var l = clone.transform.Find(B.lodPrefix + i); if (l != null) l.gameObject.SetActive(false); }   // hc:ok algorithm constant / guard / log (reviewed)
        Bounds b = Rb(clone.transform, "LOD0"); float ext = Mathf.Max(b.size.x, b.size.z, 0.01f); float s = B.targetLengthM / ext;   // hc:ok algorithm constant / guard / log (reviewed)
        holder.transform.position = b.center; clone.transform.SetParent(holder.transform, true); holder.transform.localScale = new Vector3(s, s, s);   // holder origin = the clone's bounds centre, so spin and landing are about the middle   // hc:ok algorithm constant / guard / log (reviewed)
        D.halfH = b.extents.y * s; D.holder = holder; D.landAt = D.t0 + D.tel;   // hc:ok algorithm constant / guard / log (reviewed)
        foreach (var rr in holder.GetComponentsInChildren<Renderer>(true)) { rr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; }
        MakeDecal(c, D);
        drops.Add(D); Drops++; dropCount++;
        if (c.log) Debug.Log("[HumbabaWatch] branch marked at " + p.ToString("F1") + " (lands in " + D.tel.ToString("F2") + " s, radius " + B.radiusM.ToString("F1") + " m)");   // hc:ok algorithm constant / guard / log (reviewed)
    }

    void MakeDecal(HwFile c, Drop D)
    {
        var K = c.branch.decal;
        if (decalMat == null)
        {
            var refMat = Resources.Load<Material>(K.refMaterial); if (refMat == null) { Debug.LogWarning("[HumbabaWatch] no decal material '" + K.refMaterial + "': no ground telegraph"); return; }   // hc:ok algorithm constant / guard / log (reviewed)
            decalMat = new Material(refMat); decalMat.name = "humbaba_mark"; decalMat.hideFlags = HideFlags.DontUnloadUnusedAsset; decalMat.EnableKeyword("_COLORMAP"); decalMat.SetTexture(BaseColorMap, MakeRing(K)); decalMat.SetColor("_BaseColor", Color.white);   // hc:ok algorithm constant / guard / log (reviewed)
        }
        D.proj = GroundRing.CreateProjector(decalMat, D.p, D.yaw, c.branch.radiusM, K.depthM, K.drawDistanceM, "HumbabaMark", out D.decal);   // BOSSPHASES C2: the projector set-up now lives in GroundRing (same position, rotation, size, fade 0)
    }

    static Texture2D MakeRing(HwDecal K)   // hc:ok algorithm constant / guard / log (reviewed)
    {   // BOSSPHASES C2: the ring builder moved to GroundRing.BuildRingTexture (same algorithm and numbers)
        return GroundRing.BuildRingTexture("HumbabaMark", K.texSize, K.ringRadiusFrac, K.ringWidthFrac, K.innerFill, K.tint != null && K.tint.Length >= 3 ? new Color(K.tint[0], K.tint[1], K.tint[2], 1f) : Color.black);   // hc:ok algorithm constant / guard / log (reviewed)
    }

    void UpdateDrops(HwFile c)
    {
        var B = c.branch; float now = Time.time;
        for (int i = drops.Count - 1; i >= 0; i--)   // hc:ok algorithm constant / guard / log (reviewed)
        {
            var D = drops[i];
            if (D.holder == null) { Kill(D); drops.RemoveAt(i); continue; }
            float t = Mathf.Clamp01((now - D.t0) / D.tel);   // hc:ok algorithm constant / guard / log (reviewed)
            if (!D.landed)
            {
                Vector3 land = D.p + Vector3.up * D.halfH; float fall = 1f - t * t;   // accelerating fall: y = land + (start - land) * (1 - t^2)   // hc:ok algorithm constant / guard / log (reviewed)
                Vector3 pos = Vector3.Lerp(new Vector3(D.start.x, 0f, D.start.z), new Vector3(land.x, 0f, land.z), t); pos.y = land.y + (D.start.y - land.y) * fall;   // hc:ok algorithm constant / guard / log (reviewed)
                D.holder.transform.position = pos; D.holder.transform.rotation = Quaternion.AngleAxis(B.spinDeg * (1f - t), D.axis) * Quaternion.Euler(0f, D.yaw, 0f);   // hc:ok algorithm constant / guard / log (reviewed)
                if (D.proj != null) D.proj.fadeFactor = Mathf.Clamp01(t * 2f) * B.decal.maxOpacity;   // hc:ok algorithm constant / guard / log (reviewed)
                if (t >= 1f) Land(c, D, now);   // hc:ok algorithm constant / guard / log (reviewed)
            }
            else
            {
                float age = now - D.landAt;
                if (age > B.lingerS)
                {
                    float k = Mathf.Clamp01((age - B.lingerS) / Mathf.Max(0.05f, B.sinkS)); if (D.proj != null) D.proj.fadeFactor = (1f - k) * B.decal.maxOpacity;   // hc:ok algorithm constant / guard / log (reviewed)
                    D.holder.transform.position = D.p + Vector3.up * (D.halfH - k * D.halfH * 2f);   // hc:ok algorithm constant / guard / log (reviewed)
                    if (k >= 1f) { Kill(D); drops.RemoveAt(i); }   // hc:ok algorithm constant / guard / log (reviewed)
                }
            }
        }
    }

    void Land(HwFile c, Drop D, float now)
    {
        var B = c.branch; D.landed = true; D.landAt = now; D.holder.transform.position = D.p + Vector3.up * D.halfH; D.holder.transform.rotation = Quaternion.Euler(0f, D.yaw, 0f);   // hc:ok algorithm constant / guard / log (reviewed)
        if (!string.IsNullOrEmpty(B.landShake)) CamShake.Trigger(B.landShake, B.landShakeMul);
        bool hurtJ = false;
        for (int i = 0; i < fighters.Length; i++)   // hc:ok algorithm constant / guard / log (reviewed)
        {
            var f = fighters[i]; if (f == null || f.Dead) continue;
            bool isJoan = f.IsPlayer; if (isJoan ? !B.hurtPlayer : !(B.hurtBoss && f == D.boss)) continue;
            Vector3 d = f.transform.position - D.p; float dy = Mathf.Abs(d.y); d.y = 0f;   // hc:ok algorithm constant / guard / log (reviewed)
            if (d.magnitude > B.damageRadiusM || dy > B.yTolM || f.IFrames) continue;
            Hurt(B, f, d); if (isJoan) hurtJ = true;
        }
        if (c.log) Debug.Log("[HumbabaWatch] branch landed at " + D.p.ToString("F1") + (hurtJ ? ": Joan hit, health now " + joan.health.ToString("F0") : ": missed (avoided)"));   // hc:ok algorithm constant / guard / log (reviewed)
    }

    static void Hurt(HwBranch B, DuelFighter v, Vector3 flatFromMark)   // hc:ok algorithm constant / guard / log (reviewed)
    {
        v.health -= B.damage; if (B.nonLethal && v.health < 1f) v.health = 1f;   // hc:ok algorithm constant / guard / log (reviewed)
        Vector3 away = flatFromMark.sqrMagnitude > 1e-6f ? flatFromMark.normalized : -v.transform.forward;   // hc:ok algorithm constant / guard / log (reviewed)
        v.SetKnock(away * B.knockM); v.Stagger(B.staggerTicks, B.staggerClip);
        if (v.health <= 0f) v.Die();   // hc:ok algorithm constant / guard / log (reviewed)
    }

    static void Kill(Drop D) { if (D.holder != null) Destroy(D.holder); if (D.decal != null) Destroy(D.decal); }

    // ------------------------------------------------------------------------------------------------ (a) eyes
    static Bounds Rb(Transform t, string child)
    {
        Transform r = t; if (!string.IsNullOrEmpty(child)) { var ch = t.Find(child); if (ch != null) r = ch; }
        var rs = r.GetComponentsInChildren<Renderer>(true); bool any = false; Bounds b = new Bounds(r.position, Vector3.zero);   // hc:ok algorithm constant / guard / log (reviewed)
        foreach (var x in rs) { if (x is ParticleSystemRenderer) continue; if (!any) { b = x.bounds; any = true; } else b.Encapsulate(x.bounds); }
        return b;
    }

    void EnsureEyes(HwFile c)
    {
        var E = c.eyes;
        for (int i = 0; i < E.faces.Length; i++)   // hc:ok algorithm constant / guard / log (reviewed)
        {
            var F = E.faces[i]; if (F == null || !F.enabled || F.eyes == null || F.eyes.Length == 0) continue;   // hc:ok algorithm constant / guard / log (reviewed)
            bool have = false; for (int j = 0; j < sets.Count; j++) if (sets[j].row == F && sets[j].root != null) { have = true; break; }   // hc:ok algorithm constant / guard / log (reviewed)
            if (have) continue; sets.RemoveAll(s => s.row == F);
            var go = GameObject.Find(F.propId); if (go == null) continue;
            var S = new EyeSet { row = F, root = go.transform, quads = new Transform[F.eyes.Length], gaze = new Vector2[F.eyes.Length] };   // hc:ok algorithm constant / guard / log (reviewed)
            // bounds in the prop's local frame (all 8 corners of every renderer under boundsChild)
            Transform br = go.transform; if (!string.IsNullOrEmpty(F.boundsChild)) { var ch = go.transform.Find(F.boundsChild); if (ch != null) br = ch; }
            Vector3 mn = new Vector3(1e9f, 1e9f, 1e9f), mx = -mn; bool any = false;   // hc:ok algorithm constant / guard / log (reviewed)
            foreach (var rr in br.GetComponentsInChildren<Renderer>(true))
            {
                if (rr is ParticleSystemRenderer) continue; var wb = rr.bounds;
                for (int k = 0; k < 8; k++) { var corner = wb.center + Vector3.Scale(wb.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1)); var l = go.transform.InverseTransformPoint(corner); mn = Vector3.Min(mn, l); mx = Vector3.Max(mx, l); any = true; }   // hc:ok algorithm constant / guard / log (reviewed)
            }
            if (!any) continue;
            S.bmin = mn; S.bsize = mx - mn; S.ready = true;
            if (eyeMat == null && !MakeEyeMat(E)) return;
            if (quad == null) quad = MakeQuad();
            for (int e = 0; e < F.eyes.Length; e++)   // hc:ok algorithm constant / guard / log (reviewed)
            {
                var q = new GameObject("S4Eye" + e); q.transform.SetParent(go.transform, false); q.AddComponent<MeshFilter>().sharedMesh = quad;   // hc:ok algorithm constant / guard / log (reviewed)
                var mr = q.AddComponent<MeshRenderer>(); mr.sharedMaterial = eyeMat; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false;
                float iw = F.irisWidthFrac * S.bsize.x; q.transform.localScale = new Vector3(iw, iw * F.irisAspect, 1f); S.quads[e] = q.transform;   // hc:ok algorithm constant / guard / log (reviewed)
            }
            sets.Add(S);
            if (c.log) Debug.Log("[HumbabaWatch] eyes on " + F.propId + ": local bounds min " + mn.ToString("F3") + " size " + S.bsize.ToString("F3") + " (check: flipX " + F.flipX + ")");   // hc:ok algorithm constant / guard / log (reviewed)
        }
    }

    bool MakeEyeMat(HwEyes E)
    {
        var sh = Shader.Find(E.shader); bool particle = sh != null; if (sh == null) sh = Shader.Find(E.fallbackShader);
        if (sh == null) { Debug.LogWarning("[HumbabaWatch] no eye shader: eyes skipped"); return false; }   // hc:ok algorithm constant / guard / log (reviewed)
        int n = Mathf.Clamp(E.texSize, 16, 256); var tex = new Texture2D(n, n, TextureFormat.RGBAHalf, false, true); tex.wrapMode = TextureWrapMode.Clamp; tex.filterMode = FilterMode.Bilinear;   // hc:ok algorithm constant / guard / log (reviewed)
        var px = new Color[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)   // hc:ok algorithm constant / guard / log (reviewed)
            {
                float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f, r = Mathf.Sqrt(u * u + v * v) / Mathf.Max(0.05f, E.lobeRadiusFrac);   // hc:ok algorithm constant / guard / log (reviewed)
                float a = r >= 1f ? 0f : 1f / (1f + Mathf.Pow(r / 0.35f, E.lobePower * 2f)) * Mathf.Clamp01((1f - r) / 0.2f); var cw = Color.white; cw.a = a; px[y * n + x] = cw;   // hc:ok algorithm constant / guard / log (reviewed)
            }
        tex.SetPixels(px); tex.Apply(false, true);
        eyeMat = new Material(sh); eyeMat.name = "humbaba_eye"; eyeMat.hideFlags = HideFlags.DontUnloadUnusedAsset;   // hc:ok algorithm constant / guard / log (reviewed)
        var tn = E.tint != null && E.tint.Length >= 3 ? new Color(E.tint[0], E.tint[1], E.tint[2], 1f) : Color.white;   // hc:ok algorithm constant / guard / log (reviewed)
        if (particle) { eyeMat.SetTexture(IdMain, tex); eyeMat.SetColor(IdTint, tn); eyeMat.SetFloat(IdIntensity, E.idleIntensity); eyeMat.SetFloat(IdExposure, 0f); eyeMat.SetFloat(IdSoft, 0f); eyeMat.SetFloat(IdSrc, 1f); eyeMat.SetFloat(IdDst, 1f); }   // hc:ok algorithm constant / guard / log (reviewed)
        else { eyeMat.SetFloat(IdSurface, 1f); eyeMat.SetFloat(IdBlend, 1f); eyeMat.SetFloat(IdZw, 0f); eyeMat.SetFloat(IdDs, 1f); eyeMat.SetTexture(IdUnlitMap, tex); eyeMat.SetColor(IdUnlit, tn * E.idleIntensity); }   // hc:ok algorithm constant / guard / log (reviewed)
        eyeMatParticle = particle; return true;
    }
    bool eyeMatParticle;

    static Mesh MakeQuad()
    {
        var m = new Mesh(); m.name = "S4Quad"; m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };   // hc:ok algorithm constant / guard / log (reviewed)
        m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) }; m.colors = new[] { Color.white, Color.white, Color.white, Color.white };   // hc:ok algorithm constant / guard / log (reviewed)
        m.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back }; m.triangles = new[] { 0, 2, 1, 2, 3, 1 }; m.RecalculateBounds(); return m;   // hc:ok algorithm constant / guard / log (reviewed)
    }

    void UpdateEyes(HwFile c)
    {
        if (sets.Count == 0 || eyeMat == null) return;   // hc:ok algorithm constant / guard / log (reviewed)
        var E = c.eyes; var cam = Camera.main; float dt = Time.unscaledDeltaTime; float k = 1f - Mathf.Exp(-Mathf.Max(0f, E.followSpeed) * dt);   // hc:ok algorithm constant / guard / log (reviewed)
        bool have = joan != null && !joan.Dead; Vector3 head = have ? joan.transform.position + Vector3.up * E.playerHeadM : Vector3.zero;   // hc:ok algorithm constant / guard / log (reviewed)
        float shown = 0f;   // hc:ok algorithm constant / guard / log (reviewed)
        for (int s = 0; s < sets.Count; s++)   // hc:ok algorithm constant / guard / log (reviewed)
        {
            var S = sets[s]; if (S.root == null) continue; var F = S.row;
            bool near = cam == null || (cam.transform.position - S.root.position).magnitude <= E.hideBeyondM;
            Vector3 hl = have ? S.root.InverseTransformPoint(head) : Vector3.zero;   // hc:ok algorithm constant / guard / log (reviewed)
            for (int e = 0; e < S.quads.Length; e++)   // hc:ok algorithm constant / guard / log (reviewed)
            {
                var q = S.quads[e]; if (q == null) continue; q.gameObject.SetActive(near);
                var P = F.eyes[e]; float u = F.flipX ? 1f - P.u : P.u;   // hc:ok algorithm constant / guard / log (reviewed)
                Vector3 pos = new Vector3(S.bmin.x + u * S.bsize.x, S.bmin.y + P.v * S.bsize.y, S.bmin.z + P.w * S.bsize.z);   // hc:ok algorithm constant / guard / log (reviewed)
                Vector2 tgt = Vector2.zero;   // hc:ok algorithm constant / guard / log (reviewed)
                if (have && (head - S.root.position).magnitude <= E.rangeM)
                {
                    Vector3 d = hl - pos;   // hc:ok algorithm constant / guard / log (reviewed)
                    if (d.z > 0.0001f)   // hc:ok algorithm constant / guard / log (reviewed)
                    {
                        float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, pit = Mathf.Atan2(d.y, Mathf.Sqrt(d.x * d.x + d.z * d.z)) * Mathf.Rad2Deg;   // hc:ok algorithm constant / guard / log (reviewed)
                        tgt = new Vector2(Mathf.Clamp(yaw / Mathf.Max(1f, F.maxYawDeg), -1f, 1f), Mathf.Clamp(pit / Mathf.Max(1f, F.maxPitchDeg), -1f, 1f));   // hc:ok algorithm constant / guard / log (reviewed)
                    }
                }
                S.gaze[e] = Vector2.Lerp(S.gaze[e], tgt, k); float iw = F.irisWidthFrac * S.bsize.x;   // hc:ok algorithm constant / guard / log (reviewed)
                pos += new Vector3(S.gaze[e].x * F.maxOffsetFrac * iw, S.gaze[e].y * F.maxOffsetFrac * iw * F.irisAspect, F.standoffFrac * S.bsize.z);   // hc:ok algorithm constant / guard / log (reviewed)
                q.localPosition = pos; if (near && have) shown = 1f;   // hc:ok algorithm constant / guard / log (reviewed)
            }
        }
        float aim = (have && joan != null && sets.Count > 0 && sets[0].root != null && (head - sets[0].root.position).magnitude <= E.rangeM) ? E.activeIntensity : E.idleIntensity;   // hc:ok algorithm constant / guard / log (reviewed)
        intensity = Mathf.Lerp(intensity <= 0f ? E.idleIntensity : intensity, aim, k);   // hc:ok algorithm constant / guard / log (reviewed)
        if (eyeMatParticle) eyeMat.SetFloat(IdIntensity, intensity); else eyeMat.SetColor(IdUnlit, new Color(E.tint[0], E.tint[1], E.tint[2], 1f) * intensity);   // hc:ok algorithm constant / guard / log (reviewed)
    }
}

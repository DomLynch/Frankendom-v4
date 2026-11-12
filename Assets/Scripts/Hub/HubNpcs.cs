using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

// laneNPCSPAWN v1: the camp's NPCs. One self-booting driver, created only when hub.json enabled=true (HubCfg), never in a harness run.
//   * Every hub.json pollSeconds it asks one pure question (HubResident): is the player within spawnMarginM of the camp rectangle (and are the zones live)? Yes = the NPC rows are spawned, one body GLB at a time
//     (glTFast, the same load path as GearAssetLoader), idle clip played from its phase offset; no (past despawnMarginM) = the bodies are destroyed and their imports disposed (leaveMode destroy, the "one zone paid for
//     at a time" rule) or only switched off (leaveMode sleep: memory stays, no reload hitch).
//   * An NPC is a plain object: no DuelFighter, no CharacterController, no Rigidbody, every Collider removed, no shadow caster unless hub.json spawner.shadows is true, animation culled when off screen.
//     Nothing it does is read by the sim: the duel cannot see it (FindObjectsByType<DuelFighter> never lists it).
//   * Positions come from zones.json spots (world x / z, ground by a down ray, once per spawn), yaw / phase / bodies / clips / hidden layers from hub.json.
//   * HudTag + refusal toast: drawn here only when hub.json indicator.hudTag.enabled (leave it false when laneSTALL's market is on, MarketUI draws its own SAFE tag).
// The trader's shop is NOT opened from here (laneSTALL's MarketUI owns the key and the stall point); HubNpcs.TryGet gives it the NPC if a later decision wants the NPC to be the anchor. UNPROVEN IN UNITY.
public sealed class HubNpcs : MonoBehaviour
{
    public static HubNpcs Inst;
    static readonly List<HubNpcs> All = new List<HubNpcs>(); static int pendingCamp = -1; int campIdx = -1;   // VENDORS v1: one HubNpcs per camp (Inst = the main one)
    /// <summary>Number of NPC bodies standing right now (for a log line or a test).</summary>
    public static int Standing { get { int t = 0; foreach (var h in All) if (h != null) t += h.standing.Count; return t; } }   // VENDORS v1: every camp

    sealed class Npc { public HubNpcRow row; public GameObject go; public GltfHold body, clip; public List<Material> mats; public bool placed; public Transform probe; public Vector3 probePos; public Quaternion probeRot; public float probeAt; }   // CAMPNPC v1: mats = the HDRP/Lit copies (destroyed with the body), placed = ground found and positioned, probe* = the "does the idle move" check
    sealed class GltfHold { public GLTFast.GltfImport imp; }

    HubData K; HubResident res; float nextPoll; int gen; bool loading, asleep, tried; GameObject root;
    bool preloaded, hiddenLoad; float liveSince = -1f, wakeSince = -1f;   // CAMPNPC v1: preloaded = the hidden load ran this scene; hiddenLoad = it is running; wakeSince = when the player arrived and the ground was first asked
    readonly List<Npc> standing = new List<Npc>();
    static readonly RaycastHit[] hits = new RaycastHit[12];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Inst = null; All.Clear(); pendingCamp = -1; }   // VENDORS v1

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Inst != null) return;
        var k = HubCfg.Get(); if (!k.enabled) return;                 // DARK: shipped state, nothing below runs
        if (PerfFlags.HarnessRun()) return;                            // harness runs never start the camp
        HubSafe.Arm(k);
        HubTrade.Install(k);
        var go = new GameObject("HubNpcs"); DontDestroyOnLoad(go); Inst = go.AddComponent<HubNpcs>();
        for (int ci = 0; ci < k.camps.Length; ci++)   // VENDORS v1: nothing ships in camps[], so this loop is empty until a camp is enabled
        {
            if (HubCfg.Camp(ci) == null) continue;
            pendingCamp = ci; var g2 = new GameObject("HubNpcs_camp" + ci); DontDestroyOnLoad(g2); g2.AddComponent<HubNpcs>(); pendingCamp = -1;
        }
    }

    void Awake() { campIdx = pendingCamp; K = campIdx >= 0 ? HubCfg.Camp(campIdx) : HubCfg.Get(); if (K == null) { Destroy(gameObject); return; } All.Add(this); res = new HubResident(K.spawner.spawnMarginM, K.spawner.despawnMarginM); }   // VENDORS v1
    void OnEnable() { SceneManager.sceneLoaded += OnLoaded; }
    void OnDisable() { SceneManager.sceneLoaded -= OnLoaded; }
    void OnDestroy() { if (K != null) Clear(); All.Remove(this); if (Inst == this) Inst = null; }   // VENDORS v1
    void OnLoaded(Scene s, LoadSceneMode m) { if (m == LoadSceneMode.Single) { Clear(); res.Reset(); preloaded = false; liveSince = -1f; } }   // CAMPNPC v1

    /// <summary>The standing NPC with this id (a hub.json npcs[].id), or null.</summary>
    public static GameObject TryGet(string id)
    {
        foreach (var h in All) { if (h == null) continue; foreach (var n in h.standing) if (n.row.id == id && n.go != null) return n.go; }   // VENDORS v1: every camp
        return null;
    }

    void Update()
    {
        if (K == null) return;   // VENDORS v1
        float now = Time.unscaledTime; if (now < nextPoll) return; nextPoll = now + K.spawner.pollSeconds;
        var p = GameLoop.Player; bool zones = ZoneRuntime.Owns && ZoneSpan.Live && p != null;
        PreloadStep(zones, now, p); ProbeTick(now);   // CAMPNPC v1
        bool want = res.Step(zones ? p.transform.position.z : 0f, K.area.zMin, K.area.zMax, zones && !ZoneGroundHold.Active);   // CAMPGROUND v1: no NPC while the player's own ground is still being found
        if (want) { if (asleep) Wake(); else if (!loading && !tried && standing.Count == 0 && !(K.spawner.holdWhileEngaged && p != null && p.opponent != null && !p.opponent.Dead)) { tried = true;  /* TRADEFIN v1: no 3 x 25 MB glb import in a fight */ StartCoroutine(SpawnAll(++gen)); } }
        else if ((standing.Count > 0 && !asleep) || (loading && !hiddenLoad)) Leave();   // CAMPNPC v1: a sleeping camp / a hidden load is the normal state while the player is away
    }

    // ------------------------------------------------------------------ leave / wake
    void Leave()
    {
        gen++; tried = false; wakeSince = -1f;   // cancels a spawn that is still loading; the next visit tries again   (CAMPNPC v1: wakeSince)
        if (K.spawner.leaveMode == "sleep" && standing.Count > 0 && !loading) { foreach (var n in standing) if (n.go != null) n.go.SetActive(false); asleep = true; return; }
        Clear();
    }
    // CAMPNPC v1: the player is here. An NPC that was loaded hidden is placed now (ground ray; the camp ring may still be waking: wait spawner.waitGroundSeconds, then the fallback), one that already stood is switched on.
    void Wake()
    {
        float now = Time.unscaledTime; if (wakeSince < 0f) wakeSince = now;
        if (now - wakeSince < K.spawner.waitGroundSeconds) foreach (var n in standing) if (n.go != null && !n.placed && !GroundAt(n)) return;   // next poll
        wakeSince = -1f; asleep = false;
        foreach (var n in standing) if (n.go != null) { if (!n.placed) { Place(n); n.placed = true; } else n.go.SetActive(true); Play(n); }
    }

    // CAMPNPC v1: zones live (+ preload.startDelaySeconds, so it lands inside the ZoneHitch warm cover) and the player is not in a fight: load every NPC now, inactive and unplaced.
    void PreloadStep(bool zones, float now, DuelFighter p)
    {
        var Pl = K.spawner.preload; if (!zones) { liveSince = -1f; return; }
        if (!Pl.enabled || preloaded || loading || tried || standing.Count > 0) return;
        if (liveSince < 0f) liveSince = now;
        if (now - liveSince < Pl.startDelaySeconds) return;
        if (K.spawner.holdWhileEngaged && p != null && p.opponent != null && !p.opponent.Dead) return;
        preloaded = true; tried = true; StartCoroutine(SpawnAll(++gen, true));
    }

    // CAMPNPC v1: a body whose idle does not reach its skeleton stands in its pose (the T-pose of v108b). Say so once, with the bone that was watched.
    void ProbeTick(float now)
    {
        foreach (var n in standing)
        {
            if (n.probe == null || now < n.probeAt) continue;
            if (n.go == null || !n.go.activeInHierarchy) { n.probe = null; continue; }
            bool seen = false; foreach (var r in n.go.GetComponentsInChildren<Renderer>(false)) if (r.isVisible) { seen = true; break; }
            if (!seen) { n.probePos = n.probe.localPosition; n.probeRot = n.probe.localRotation; n.probeAt = now + K.spawner.probeSeconds; continue; }   // animation is culled while no renderer is seen: judge it only after it was on screen for probeSeconds
            bool moved = (n.probe.localPosition - n.probePos).sqrMagnitude > 1e-6f || Quaternion.Angle(n.probe.localRotation, n.probeRot) > 0.2f;
            string msg = "[Hub] npc " + n.row.id + " idle check after " + K.spawner.probeSeconds.ToString("0.0") + " s: bone '" + K.spawner.probeBone + "' " + (moved ? "moves, the clip drives the skeleton" : "did NOT move: the clip is not driving this body (T-pose)");
            if (moved) { if (K.spawner.logLoads) Debug.Log(msg); } else Debug.LogWarning(msg);
            n.probe = null;
        }
    }

    void NoteClip(Npc n, Animation an, AnimationState st, float t0)
    {
        var S = K.spawner; int states = 0; foreach (AnimationState s in an) states++;
        if (S.logLoads) Debug.Log("[Hub] npc " + n.row.id + " plays clip '" + st.name + "' (" + st.length.ToString("0.00") + " s, from " + t0.ToString("0.00") + " s; " + states + " clips on '" + an.gameObject.name + "', playing " + an.IsPlaying(st.name) + ")");
        n.probe = null; if (S.probeBone.Length == 0) return;
        var b = FindDeep(n.go.transform, S.probeBone); if (b == null) { Debug.LogWarning("[Hub] npc " + n.row.id + ": probe bone '" + S.probeBone + "' not found, idle not checked"); return; }
        n.probe = b; n.probePos = b.localPosition; n.probeRot = b.localRotation; n.probeAt = Time.unscaledTime + S.probeSeconds;
    }

    static bool OnWithin(Transform t, Transform stop) { for (; t != null; t = t.parent) { if (!t.gameObject.activeSelf) return false; if (t == stop) return true; } return true; }   // every node from t up to stop is switched on (own flag, not the parent's)
    static int TriCount(GameObject go)
    {
        int t = 0; if (go == null) return 0;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (!OnWithin(r.transform, go.transform)) continue;   // includeInactive: the whole NPC may be switched off while it is hidden
            Mesh m = null; var sk = r as SkinnedMeshRenderer; if (sk != null) m = sk.sharedMesh; else { var mf = r.GetComponent<MeshFilter>(); if (mf != null) m = mf.sharedMesh; }
            if (m != null) for (int i = 0; i < m.subMeshCount; i++) t += (int)(m.GetIndexCount(i) / 3);
        }
        return t;
    }
    int TotalTris() { int t = 0; foreach (var n in standing) t += TriCount(n.go); return t; }

    void Clear()
    {
        gen++; hiddenLoad = false; wakeSince = -1f;   // CAMPNPC v1: a spawn that is still loading must see that the camp was cleared (scene load, destroy)
        foreach (var n in standing) Free(n);
        standing.Clear(); asleep = false; loading = false; tried = false;
        if (root != null) Destroy(root); root = null;
    }
    static void Free(Npc n)
    {
        if (n == null) return;
        if (n.go != null) Destroy(n.go);
        if (n.body != null && n.body.imp != null) { try { n.body.imp.Dispose(); } catch (Exception) { } }
        if (n.clip != null && n.clip.imp != null) { try { n.clip.imp.Dispose(); } catch (Exception) { } }
        if (n.mats != null) { foreach (var m in n.mats) if (m != null) Destroy(m); n.mats = null; }   // CAMPNPC v1
    }

    // ------------------------------------------------------------------ spawn
    IEnumerator SpawnAll(int myGen, bool hidden = false)
    {
        loading = true; hiddenLoad = hidden; var S = K.spawner;   // CAMPNPC v1: hidden = load + instantiate, keep inactive and unplaced
        var en = new bool[K.npcs.Length]; for (int i = 0; i < en.Length; i++) en[i] = K.npcs[i] != null && K.npcs[i].enabled;
        var order = HubClip.Pick(en, K.npcRules.maxSimultaneous);
        if (root == null) { root = new GameObject("HubNpcs_Live"); root.transform.SetParent(transform, false); }
        for (int oi = 0; oi < order.Count; oi++)
        {
            var row = K.npcs[order[oi]]; Npc n = new Npc { row = row }; float lt0 = Time.realtimeSinceStartup; var task = LoadOne(row, n, root.transform);   // CAMPNPC v1: lt0
            while (!task.IsCompleted) yield return null;
            if (myGen != gen || this == null) { Free(n); if (myGen == gen) loading = false; yield break; }   // the player left while this body loaded
            if (task.IsFaulted || !task.Result) { Debug.LogWarning("[Hub] npc " + row.id + " not loaded: " + (task.IsFaulted && task.Exception != null ? task.Exception.GetBaseException().Message : "see the lines above")); Free(n); continue; }
            if (!hidden) { float gw = Time.unscaledTime + K.spawner.waitGroundSeconds; while (!GroundAt(n) && Time.unscaledTime < gw && myGen == gen) yield return null; if (myGen != gen || this == null) { Free(n); if (myGen == gen) loading = false; yield break; } }   // CAMPGROUND v1: wait for the camp's ground ring
            if (hidden) standing.Add(n); else { Place(n); n.placed = true; Play(n); standing.Add(n); }   // CAMPNPC v1
            if (S.logLoads) Debug.Log("[Hub] npc " + row.id + (hidden ? " loaded hidden (" : " standing at " + n.go.transform.position + " (") + ((Time.realtimeSinceStartup - lt0) * 1000f).ToString("0") + " ms wall, " + TriCount(n.go) + " tris)");   // CAMPNPC v1
            for (int f = 0; f < S.framesBetweenLoads; f++) yield return null;   // one body per few frames: no frame carries all three
            if (myGen != gen || this == null) yield break;   // TRADEFIN v1: Leave() / Clear() ran in that wait (root is gone, loading already false); the next iteration used root.transform = NullReferenceException
        }
        loading = false; hiddenLoad = false;
        if (hidden) { tried = false; asleep = standing.Count > 0; if (S.logLoads) Debug.Log("[Hub] camp NPCs preloaded hidden: " + standing.Count + " of " + order.Count + ", " + TotalTris() + " tris"); }   // CAMPNPC v1: the next poll with the player here wakes them
    }

    async Task<bool> LoadOne(HubNpcRow row, Npc n, Transform parent)
    {
        var S = K.spawner; string bodyFile = Find(row.runtime.glb, row.runtime.sources);
        if (bodyFile == null) { Debug.LogWarning("[Hub] npc " + row.id + ": body GLB not found (runtime.glb / sources in hub.json)"); return false; }
        n.go = new GameObject("npc_" + row.id); n.go.transform.SetParent(parent, false); n.go.SetActive(false);   // off until placed: no frame shows it at the origin
        var body = new GameObject(S.bodyName); body.transform.SetParent(n.go.transform, false);
        var pre = GlbPreload.Take(bodyFile); GLTFast.GltfImport got = null;   // CAMPNPC v1: parsed during the intro (perf_fixes.json C3.files); only the instantiate is left
        if (pre != null) { try { got = await pre; } catch (Exception) { got = null; } }
        if (S.logLoads) Debug.Log("[Hub] npc " + row.id + (got != null ? ": parsed import taken from GlbPreload" : ": no preloaded import, parsing now (a hitch)"));
        if (got != null) n.body = new GltfHold { imp = got };
        else { n.body = new GltfHold { imp = new GLTFast.GltfImport() }; if (!await n.body.imp.LoadFile(bodyFile)) return false; }
        if (!await n.body.imp.InstantiateMainSceneAsync(body.transform)) return false;
        if (row.runtime.clipGlb.Length > 0 || row.runtime.clipSources.Length > 0)
        {
            string clipFile = Find(row.runtime.clipGlb, row.runtime.clipSources);
            if (clipFile == null) Debug.LogWarning("[Hub] npc " + row.id + ": clip GLB not found, the body stays in its pose");
            else
            {
                n.clip = new GltfHold { imp = new GLTFast.GltfImport() };
                if (await n.clip.imp.LoadFile(clipFile))
                {
                    var clips = n.clip.imp.GetAnimationClips(); AnimationClip pick = null;
                    if (clips != null) foreach (var c in clips) if (c != null && (pick == null || (row.runtime.clipName.Length > 0 && c.name == row.runtime.clipName))) pick = c;
                    if (pick != null)
                    {
                        pick.legacy = true; pick.wrapMode = WrapMode.Loop;
                        var an = body.GetComponent<Animation>(); if (an == null) an = body.AddComponent<Animation>();
                        an.AddClip(pick, "idle");
                    }
                    else Debug.LogWarning("[Hub] npc " + row.id + ": no animation in the clip GLB");
                }
                else Debug.LogWarning("[Hub] npc " + row.id + ": clip GLB failed to load");
            }
        }
        foreach (var h in row.runtime.hideNodes) { var t = FindDeep(body.transform, h); if (t != null) t.gameObject.SetActive(false); }
        Cheapen(n.go, S);
        if (S.convertMaterials) { var rl = new List<Renderer>(); foreach (var r in body.GetComponentsInChildren<Renderer>(true)) if (OnWithin(r.transform, body.transform)) rl.Add(r); n.mats = GearMaterials.Convert(rl); }   // CAMPNPC v1: glTFast shader-graph materials -> HDRP/Lit like the gear (base colour + normal map; hideNodes skipped; n.go is switched off here, so includeInactive + own-flag test)
        float rsc = row.runtime.scale * (row.runtime.horseScale ? MountData.HorseScale() : 1f);   // laneHORSE v2: the static camp horse reads the same size key as the ridden one (mount.json horse.heightM)
        if (rsc > 0f && Mathf.Abs(rsc - 1f) > 1e-4f) body.transform.localScale = Vector3.one * rsc;
        return true;
    }

    static void Cheapen(GameObject go, HubSpawner S)
    {
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) Destroy(rb);
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            r.shadowCastingMode = S.shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            var sk = r as SkinnedMeshRenderer; if (sk != null) sk.updateWhenOffscreen = false;
        }
        if (S.cullWhenOffscreen) foreach (var a in go.GetComponentsInChildren<Animation>(true)) a.cullingType = AnimationCullingType.BasedOnRenderers;
    }

    /// <summary>CAMPGROUND v1: true when a down ray at the NPC's spot finds ground (ring on). Same ray as Place.</summary>
    bool GroundAt(Npc n)
    {
        float x, z; if (!HubCfg.SpotXZ(K.zone, n.row.spot, out x, out z)) return true; var S = K.spawner; var p = GameLoop.Player; float py = p != null ? p.transform.position.y : S.fallbackY;
        int cnt = Physics.RaycastNonAlloc(new Vector3(x, py + S.groundRayUpM, z), Vector3.down, hits, S.groundRayUpM + S.groundRayDownM, S.groundMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < cnt; i++) { var c = hits[i].collider; if (c != null && !(p != null && c.transform.IsChildOf(p.transform))) return true; }
        return false;
    }

    void Place(Npc n)
    {
        float x, z; var S = K.spawner; var p = GameLoop.Player; float py = p != null ? p.transform.position.y : 0f;
        if (!HubCfg.SpotXZ(K.zone, n.row.spot, out x, out z)) { Debug.LogWarning("[Hub] npc " + n.row.id + ": spot '" + n.row.spot + "' is not in zone '" + K.zone + "', standing at the player's depth"); x = 0f; z = p != null ? p.transform.position.z : 0f; }
        float y = S.fallbackY; bool found = false;   // CAMPGROUND v1: was 'float y = py' = the PLAYER's height, which is the falling player's y when no ground was found
        int cnt = Physics.RaycastNonAlloc(new Vector3(x, py + S.groundRayUpM, z), Vector3.down, hits, S.groundRayUpM + S.groundRayDownM, S.groundMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        for (int i = 0; i < cnt; i++)
        {
            var c = hits[i].collider; if (c == null || (p != null && c.transform.IsChildOf(p.transform))) continue;
            if (hits[i].distance < best) { best = hits[i].distance; y = hits[i].point.y; found = true; }
        }
        if (!found) Debug.LogWarning("[Hub] npc " + n.row.id + ": no ground under x " + x.ToString("F1") + " z " + z.ToString("F1") + " (ray from y " + (py + S.groundRayUpM).ToString("F1") + "), standing at fallbackY " + S.fallbackY.ToString("F2"));   // CAMPGROUND v1
        if (found) y = Code2Cfg.SnapNpcY(K.zone, n.row.id, y, hits, cnt, p != null ? p.transform : null);   // CODE2 v1: feet from the collider top to the visible floor (code2.json npcSnap, off = unchanged)
        n.go.transform.SetPositionAndRotation(new Vector3(x, y, z), Quaternion.Euler(0f, n.row.yaw + S.yawOffsetDeg, 0f));
        n.go.SetActive(true);
    }

    void Play(Npc n)   // CAMPNPC v1: was static (NoteClip needs the config)
    {
        var an = n.go.GetComponentInChildren<Animation>(true); if (an == null) { Debug.LogWarning("[Hub] npc " + n.row.id + ": no Animation component (no clip loaded), the body stays in its pose");  /* TRADEFIN v1 */ return; }
        an.wrapMode = WrapMode.Loop;
        AnimationState st = an["idle"];
        if (st == null) foreach (AnimationState s in an) { if (n.row.runtime.clipName.Length == 0 || s.name == n.row.runtime.clipName) { st = s; break; } }   // a body that carries its own idle
        if (st == null) { Debug.LogWarning("[Hub] npc " + n.row.id + ": clip state not found (clipName '" + n.row.runtime.clipName + "')");  /* TRADEFIN v1 */ return; }
        st.wrapMode = WrapMode.Loop; float t0 = HubClip.StartTime(n.row.phaseSeconds, st.length); st.time = t0; an.Play(st.name); st.time = t0;   // CAMPNPC v1: the time again after Play (a clip that started by itself would sit at 0, every NPC in step)
        NoteClip(n, an, st, t0);
    }

    // ------------------------------------------------------------------ files
    string Find(string rel, string[] extra)
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(rel)) { list.Add(Path.Combine(Application.streamingAssetsPath, rel)); }
        if (extra != null) foreach (var e in extra) if (!string.IsNullOrEmpty(e)) list.Add(e);
        foreach (var f in list)
        {
            try
            {
                if (Path.IsPathRooted(f)) { if (File.Exists(f)) return f; continue; }
                foreach (var r in K.spawner.searchRoots) { string q = Path.GetFullPath(Path.Combine(Application.dataPath, "../" + r + "/" + f)); if (File.Exists(q)) return q; }
            }
            catch (Exception) { }
        }
        return null;
    }
    static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++) { var r = FindDeep(t.GetChild(i), name); if (r != null) return r; }
        return null;
    }

    // ------------------------------------------------------------------ the SAFE tag and the refusal toast (data: hub.json indicator)
    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;   // UIDATA_C v1: draw only (tag + toast), no layout pass
        if (campIdx >= 0) return;   // MARKET2 v1: the main driver draws the tag and the toast once; HubSafe.PlayerInside already covers every camp area
        var I = K.indicator; if (!I.hudTag.enabled && I.refuseToast.seconds <= 0f) return;
        bool inside = HubSafe.PlayerInside(); float age = Time.unscaledTime - HubSafe.RefusedAt; bool toast = age >= 0f && age <= I.refuseToast.seconds;
        if (!inside && !toast) return;
        float s = HudCfg.Scale(); var m = GUI.matrix; GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f)); float vw = Screen.width / s, vh = Screen.height / s; var H = HudCfg.Get();
        if (inside && I.hudTag.enabled) HudDraw.Caps(vw * 0.5f, I.hudTag.top, 0.5f, K.text.hudTag, "title", I.hudTag.big, I.hudTag.small, I.hudTag.tracking, HudCfg.Col(I.hudTag.colour));
        if (toast)
        {
            var ZH = ZoneUiCfg.Get().hub; float T = I.refuseToast.seconds; HudDraw.Alpha = Mathf.Clamp01(Mathf.Min(age / ZH.toastFadeInS, (T - age) / ZH.toastFadeOutS));   // UIDATA_C v1
            HudDraw.Text(new Rect(0f, vh - I.refuseToast.bottom, vw, I.refuseToast.size * ZH.toastLineH), K.text.noFight, "serif", I.refuseToast.size, TextAnchor.UpperCenter, true, HudCfg.Col(H.colors.ink));
        }
        HudDraw.Alpha = 1f; GUI.matrix = m;
    }
}

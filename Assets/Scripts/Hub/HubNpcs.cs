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
    /// <summary>Number of NPC bodies standing right now (for a log line or a test).</summary>
    public static int Standing { get { return Inst == null ? 0 : Inst.standing.Count; } }

    sealed class Npc { public HubNpcRow row; public GameObject go; public GltfHold body, clip; }
    sealed class GltfHold { public GLTFast.GltfImport imp; }

    HubData K; HubResident res; float nextPoll; int gen; bool loading, asleep, tried; GameObject root;
    readonly List<Npc> standing = new List<Npc>();
    static readonly RaycastHit[] hits = new RaycastHit[12];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Inst = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Inst != null) return;
        var k = HubCfg.Get(); if (!k.enabled) return;                 // DARK: shipped state, nothing below runs
        if (PerfFlags.HarnessRun()) return;                            // harness runs never start the camp
        HubSafe.Arm(k);
        HubTrade.Install(k);
        var go = new GameObject("HubNpcs"); DontDestroyOnLoad(go); Inst = go.AddComponent<HubNpcs>();
    }

    void Awake() { K = HubCfg.Get(); res = new HubResident(K.spawner.spawnMarginM, K.spawner.despawnMarginM); }
    void OnEnable() { SceneManager.sceneLoaded += OnLoaded; }
    void OnDisable() { SceneManager.sceneLoaded -= OnLoaded; }
    void OnDestroy() { Clear(); if (Inst == this) Inst = null; }
    void OnLoaded(Scene s, LoadSceneMode m) { if (m == LoadSceneMode.Single) { Clear(); res.Reset(); } }

    /// <summary>The standing NPC with this id (a hub.json npcs[].id), or null.</summary>
    public static GameObject TryGet(string id)
    {
        if (Inst == null) return null;
        foreach (var n in Inst.standing) if (n.row.id == id && n.go != null) return n.go;
        return null;
    }

    void Update()
    {
        float now = Time.unscaledTime; if (now < nextPoll) return; nextPoll = now + K.spawner.pollSeconds;
        var p = GameLoop.Player; bool zones = ZoneRuntime.Owns && ZoneSpan.Live && p != null;
        bool want = res.Step(zones ? p.transform.position.z : 0f, K.area.zMin, K.area.zMax, zones);
        if (want) { if (asleep) Wake(); else if (!loading && !tried && standing.Count == 0) { tried = true; StartCoroutine(SpawnAll(++gen)); } }
        else if (standing.Count > 0 || loading) Leave();
    }

    // ------------------------------------------------------------------ leave / wake
    void Leave()
    {
        gen++; tried = false;   // cancels a spawn that is still loading; the next visit tries again
        if (K.spawner.leaveMode == "sleep" && standing.Count > 0 && !loading) { foreach (var n in standing) if (n.go != null) n.go.SetActive(false); asleep = true; return; }
        Clear();
    }
    void Wake() { asleep = false; foreach (var n in standing) if (n.go != null) { n.go.SetActive(true); Play(n); } }

    void Clear()
    {
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
    }

    // ------------------------------------------------------------------ spawn
    IEnumerator SpawnAll(int myGen)
    {
        loading = true; var S = K.spawner;
        var en = new bool[K.npcs.Length]; for (int i = 0; i < en.Length; i++) en[i] = K.npcs[i] != null && K.npcs[i].enabled;
        var order = HubClip.Pick(en, K.npcRules.maxSimultaneous);
        if (root == null) { root = new GameObject("HubNpcs_Live"); root.transform.SetParent(transform, false); }
        for (int oi = 0; oi < order.Count; oi++)
        {
            var row = K.npcs[order[oi]]; Npc n = new Npc { row = row }; var task = LoadOne(row, n, root.transform);
            while (!task.IsCompleted) yield return null;
            if (myGen != gen || this == null) { Free(n); if (myGen == gen) loading = false; yield break; }   // the player left while this body loaded
            if (task.IsFaulted || !task.Result) { Debug.LogWarning("[Hub] npc " + row.id + " not loaded: " + (task.IsFaulted && task.Exception != null ? task.Exception.GetBaseException().Message : "see the lines above")); Free(n); continue; }
            Place(n); Play(n); standing.Add(n);
            if (S.logLoads) Debug.Log("[Hub] npc " + row.id + " standing at " + n.go.transform.position);
            for (int f = 0; f < S.framesBetweenLoads; f++) yield return null;   // one body per few frames: no frame carries all three
        }
        loading = false;
    }

    async Task<bool> LoadOne(HubNpcRow row, Npc n, Transform parent)
    {
        var S = K.spawner; string bodyFile = Find(row.runtime.glb, row.runtime.sources);
        if (bodyFile == null) { Debug.LogWarning("[Hub] npc " + row.id + ": body GLB not found (runtime.glb / sources in hub.json)"); return false; }
        n.go = new GameObject("npc_" + row.id); n.go.transform.SetParent(parent, false); n.go.SetActive(false);   // off until placed: no frame shows it at the origin
        var body = new GameObject(S.bodyName); body.transform.SetParent(n.go.transform, false);
        n.body = new GltfHold { imp = new GLTFast.GltfImport() };
        if (!await n.body.imp.LoadFile(bodyFile)) return false;
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
        if (row.runtime.scale > 0f && Mathf.Abs(row.runtime.scale - 1f) > 1e-4f) body.transform.localScale = Vector3.one * row.runtime.scale;
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

    void Place(Npc n)
    {
        float x, z; var S = K.spawner; var p = GameLoop.Player; float py = p != null ? p.transform.position.y : 0f;
        if (!HubCfg.SpotXZ(K.zone, n.row.spot, out x, out z)) { Debug.LogWarning("[Hub] npc " + n.row.id + ": spot '" + n.row.spot + "' is not in zone '" + K.zone + "', standing at the player's depth"); x = 0f; z = p != null ? p.transform.position.z : 0f; }
        float y = py;
        int cnt = Physics.RaycastNonAlloc(new Vector3(x, py + S.groundRayUpM, z), Vector3.down, hits, S.groundRayUpM + S.groundRayDownM, S.groundMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        for (int i = 0; i < cnt; i++)
        {
            var c = hits[i].collider; if (c == null || (p != null && c.transform.IsChildOf(p.transform))) continue;
            if (hits[i].distance < best) { best = hits[i].distance; y = hits[i].point.y; }
        }
        n.go.transform.SetPositionAndRotation(new Vector3(x, y, z), Quaternion.Euler(0f, n.row.yaw + S.yawOffsetDeg, 0f));
        n.go.SetActive(true);
    }

    static void Play(Npc n)
    {
        var an = n.go.GetComponentInChildren<Animation>(true); if (an == null) return;
        an.wrapMode = WrapMode.Loop;
        AnimationState st = an["idle"];
        if (st == null) foreach (AnimationState s in an) { if (n.row.runtime.clipName.Length == 0 || s.name == n.row.runtime.clipName) { st = s; break; } }   // a body that carries its own idle
        if (st == null) return;
        st.wrapMode = WrapMode.Loop; st.time = HubClip.StartTime(n.row.phaseSeconds, st.length); an.Play(st.name);
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
        var I = K.indicator; if (!I.hudTag.enabled && I.refuseToast.seconds <= 0f) return;
        bool inside = HubSafe.PlayerInside(); float age = Time.unscaledTime - HubSafe.RefusedAt; bool toast = age >= 0f && age <= I.refuseToast.seconds;
        if (!inside && !toast) return;
        float s = HudCfg.Scale(); var m = GUI.matrix; GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f)); float vw = Screen.width / s, vh = Screen.height / s; var H = HudCfg.Get();
        if (inside && I.hudTag.enabled) HudDraw.Caps(vw * 0.5f, I.hudTag.top, 0.5f, K.text.hudTag, "title", I.hudTag.big, I.hudTag.small, I.hudTag.tracking, HudCfg.Col(I.hudTag.colour));
        if (toast)
        {
            float T = I.refuseToast.seconds; HudDraw.Alpha = Mathf.Clamp01(Mathf.Min(age / 0.12f, (T - age) / 0.35f));
            HudDraw.Text(new Rect(0f, vh - I.refuseToast.bottom, vw, I.refuseToast.size * 1.5f), K.text.noFight, "serif", I.refuseToast.size, TextAnchor.UpperCenter, true, HudCfg.Col(H.colors.ink));
        }
        HudDraw.Alpha = 1f; GUI.matrix = m;
    }
}

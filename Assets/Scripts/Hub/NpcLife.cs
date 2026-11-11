using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// NPCLIFE v1 (unity-dev2): ambient clips for the hub NPCs on top of the base idle that HubNpcs already plays (legacy Animation, state "idle").
// Data: StreamingAssets/npc_life.json (flat, generated from OpenTrident npc_life_assign.json), clip GLBs under StreamingAssets/Npc/life/<rig>/.
// A hidden driver polls for the spawned NPC bodies (GameObject "npc_<row id>" with a child that has an Animation), loads each listed clip GLB once (shared),
// then per NPC: wait U(minInterval,maxInterval), pick one eligible entry by weight (not in cooldown, 'near' entries only with the player within nearM),
// crossfade in, play once, crossfade back to "idle". At most one ambient clip per NPC and none within minGapBetweenAmbientS of another NPC's. No existing file is edited.
// Off by default only through npc_life.json enabled=false (or -nonpclife). Missing file = nothing.
public class NpcLife : MonoBehaviour
{
    [Serializable] class Entry { public string clip = "", file = ""; public float weight = 1f, minIntervalS = 10f, maxIntervalS = 30f, durationS = 5f, blendInS = 0.4f, blendOutS = 0.4f, cooldownS = 30f; public bool near; }
    [Serializable] class Row { public string id = ""; public Entry[] ambient = new Entry[0]; }
    [Serializable] class Cfg { public bool enabled = true, log = true; public float pollSeconds = 1f, nearM = 3.5f, minGapBetweenAmbientS = 4f; public int maxNpcsActive = 6; public Row[] rows = new Row[0]; }

    class State { public Row row; public Animation an; public float nextAt, endAt, backAt; public bool playing, backed; public string cur; public Dictionary<string, float> cd = new Dictionary<string, float>(); }

    Cfg C; float nextPoll, lastAnyAt = -99f; readonly Dictionary<Animation, State> states = new Dictionary<Animation, State>();
    static readonly Dictionary<string, AnimationClip> clips = new Dictionary<string, AnimationClip>(); static readonly HashSet<string> loading = new HashSet<string>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        foreach (var a in Environment.GetCommandLineArgs()) if (a == "-nonpclife") return;
        string txt = DataPath.ReadText("npc_life.json"); if (txt == null) return;
        Cfg c; try { c = JsonUtility.FromJson<Cfg>(txt); } catch (Exception e) { Debug.LogWarning("[NpcLife] npc_life.json unreadable: " + e.Message); return; }
        if (c == null || !c.enabled || c.rows.Length == 0) return;
        var g = new GameObject("NpcLife"); DontDestroyOnLoad(g); g.hideFlags = HideFlags.HideInHierarchy; var d = g.AddComponent<NpcLife>(); d.C = c;
        if (c.log) Debug.Log("[NpcLife] armed: " + c.rows.Length + " NPC rows");
    }

    void Update()
    {
        if (Time.unscaledTime < nextPoll) return; nextPoll = Time.unscaledTime + Mathf.Max(0.2f, C.pollSeconds);
        try { Scan(); Tick(); } catch (Exception e) { Debug.LogWarning("[NpcLife] " + e.Message); }
    }

    void Scan()
    {
        foreach (var an in FindObjectsByType<Animation>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (an == null) continue; var key = an; if (states.ContainsKey(key)) continue;
            var p = an.transform.parent; if (p == null || !p.name.StartsWith("npc_")) continue;
            Row row = null; foreach (var r in C.rows) if (p.name == "npc_" + r.id) { row = r; break; }
            if (row == null || row.ambient.Length == 0 || an["idle"] == null) continue;
            var st = new State { row = row, an = an, nextAt = Time.unscaledTime + UnityEngine.Random.Range(3f, 12f) }; states[key] = st;
            foreach (var e in row.ambient) Load(e);
            if (C.log) Debug.Log("[NpcLife] " + row.id + ": " + row.ambient.Length + " ambient clips queued");
        }
    }

    async void Load(Entry e)
    {
        if (clips.ContainsKey(e.file) || !loading.Add(e.file)) return;
        try
        {
            string path = Path.Combine(Application.streamingAssetsPath, e.file); if (!File.Exists(path)) { if (C.log) Debug.LogWarning("[NpcLife] missing " + path); return; }
            var imp = new GLTFast.GltfImport(); if (!await imp.LoadFile(path)) return;
            var cl = imp.GetAnimationClips(); if (cl != null && cl.Length > 0 && cl[0] != null) { cl[0].legacy = true; cl[0].wrapMode = WrapMode.Once; clips[e.file] = cl[0]; }
        }
        catch (Exception ex) { Debug.LogWarning("[NpcLife] load " + e.file + ": " + ex.Message); }
    }

    void Tick()
    {
        float now = Time.unscaledTime; var pl = GameLoop.Player; int active = 0;
        foreach (var st in states.Values) if (st.playing) active++;
        List<Animation> dead = null;
        foreach (var kv in states)
        {
            var st = kv.Value; if (st.an == null) { (dead ?? (dead = new List<Animation>())).Add(kv.Key); continue; }
            if (!st.an.gameObject.activeInHierarchy) continue;
            if (st.playing)
            {
                if (!st.backed && now >= st.backAt) { st.an.CrossFade("idle", Mathf.Max(0.05f, FindEntry(st, st.cur) != null ? FindEntry(st, st.cur).blendOutS : 0.4f)); st.backed = true; }
                if (now >= st.endAt) { st.playing = false; active--; var e0 = FindEntry(st, st.cur); st.nextAt = now + (e0 != null ? UnityEngine.Random.Range(e0.minIntervalS, e0.maxIntervalS) : 15f); }
                continue;
            }
            if (now < st.nextAt || active >= C.maxNpcsActive || now - lastAnyAt < C.minGapBetweenAmbientS) continue;
            float tot = 0f; var el = new List<Entry>();
            foreach (var e in st.row.ambient)
            {
                if (!clips.ContainsKey(e.file)) continue; float cd; if (st.cd.TryGetValue(e.clip, out cd) && now < cd) continue;
                if (e.near) { if (pl == null || Vector3.Distance(pl.transform.position, st.an.transform.position) > C.nearM) continue; }
                el.Add(e); tot += Mathf.Max(0.01f, e.weight);
            }
            if (el.Count == 0) { st.nextAt = now + 5f; continue; }
            float r = UnityEngine.Random.value * tot; Entry pick = el[el.Count - 1];
            foreach (var e in el) { r -= Mathf.Max(0.01f, e.weight); if (r <= 0f) { pick = e; break; } }
            var clip = clips[pick.file]; string nm = "life_" + pick.clip;
            if (st.an[nm] == null) st.an.AddClip(clip, nm);
            var s = st.an[nm]; if (s == null) { st.nextAt = now + 10f; continue; }
            s.wrapMode = WrapMode.Once; s.time = 0f; st.an.CrossFade(nm, Mathf.Max(0.05f, pick.blendInS));
            float dur = Mathf.Max(1f, clip.length);
            st.playing = true; st.backed = false; st.cur = pick.clip; st.endAt = now + dur; st.backAt = now + Mathf.Max(0.2f, dur - pick.blendOutS);
            st.cd[pick.clip] = now + pick.cooldownS; lastAnyAt = now; active++;
            if (C.log) Debug.Log("[NpcLife] " + st.row.id + " plays " + pick.clip + " (" + dur.ToString("0.0") + " s)");
        }
        if (dead != null) foreach (var k in dead) states.Remove(k);
    }

    static Entry FindEntry(State st, string clip) { foreach (var e in st.row.ambient) if (e.clip == clip) return e; return null; }
}


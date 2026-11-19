using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ZONESCOPE v1 (fk_loadscope_kit, FK 16 GB piece A2, 18 Nov 2026). NOT APPLIED / NOT BUILT / NOT RUN when this file was written (Roslyn only).
// Idea: the runtime-loaded GLBs (GlbPreload late tier: boss weapons, camp vendors, gear alternates; hub camp NPCs; ZoneHitch.Warm creature prefabs) are not zone-independent memory. Keep only the CURRENT zone and its
// neighbours (zone_scope.json neighbours[].keep), stage the next zone's files AHEAD in calm frames outside the arrival window (hitch.quietAfterArrivalS), and drop what left the scope (GltfImport.Dispose) after dropAfterS.
// Scene-baked textures (the 4.5 GB of idle z2-z7 world maps) are NOT touched here: that is the scene split (fk_split_code_kit).
// Everything is data (zone_scope.json, shipped enabled:false): enabled false = every public member below returns at once = today's behaviour bit for bit (two log lines more: "[ZoneScope] off (data)").
// No MonoBehaviour, no serialized field, no RuntimeInitializeOnLoadMethod: driven from ZoneSpan's existing ResetStatics / Bind / Poll / Update (compile-only take). Switches: -zonescope:on / -zonescope:off.
// Log lines (all "[ZoneScope]"): cfg, scope <zone> keep [..] queue N est MB, stage <file> est ms, drop <file> est MB texNow A->B, camp <zone> dropped, arrival <zone> window: frames >N ms count worst, resident est MB.
[Serializable] public class ZscHitch
{
    public float quietAfterArrivalS = 12f, calmFrameMs = 40f, perPollBudgetMs = 350f, arrivalBudgetMs = 0f, msPerMpx = 18f, maxWaitS = 30f;
    public int spacingFrames = 24, countOverMs = 100; public bool holdWhileEngaged = true;
}
[Serializable]
public class ZscDecode
{
    public string mode = "off", scope = "groups", cacheDir = "zonescope_img";   // mode "uri" = decode big images off the main thread (ZoneDecode); scope "groups" = only files in a zone_scope group, "all" = every GlbPreload parse
    public bool standalone, nonReadable, stripMips = true, logImages = true;   // standalone: works with scope enabled:false; nonReadable: drop the CPU copy (look-neutral unless something reads pixels); stripMips: copy to a 1-mip texture when the decoder returns a chain (today's glTFast textures have none)
    public int minPixels = 4000000, maxParallel = 1, imageSpacingFrames = 6;
}
[Serializable] public class ZscCamp { public string zone = ""; public float aheadM = 30f, behindM = 30f, hysteresisM = 15f, minAgeS = 0f; }   // v2: the hub NPC set is wanted only while the player is within aheadM before / behindM after the camp area (hub.json area zMin..zMax); minAgeS = not before this many s after the scene load
[Serializable] public class ZscGroup { public string id = "", zone = "", policy = "zone"; public string[] files = new string[0], stageExtra = new string[0]; public float mbEst, mpx; }
[Serializable] public class ZscNeigh { public string zone = ""; public string[] keep = new string[0]; }
[Serializable]
public class ZscCfg
{
    public bool enabled, log = true, scopeTemplates = true, scopeCamps = true, dropCamps = true; public string startZone = "pit_yard", lootAlts = "preload";
    public float keepLoadedMB = 1700f, dropAfterS = 20f, templateMsEst = 60f;
    public ZscHitch hitch = new ZscHitch(); public ZscDecode decode = new ZscDecode(); public ZscNeigh[] neighbours = new ZscNeigh[0]; public ZscCamp[] camps = new ZscCamp[0]; public ZscGroup[] groups = new ZscGroup[0];
}

public static class ZoneScope
{
    sealed class Item { public string path, name, group, zone; public bool template; public float estMs, mb; }
    static ZscCfg cfg; static bool loaded, active, decodeActive;
    static string cur; static float arrivalAt = -1000f, queueReadyAt = -1f;
    static readonly HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, float> outSince = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);   // zone -> time it left the scope
    static readonly Dictionary<string, Item> resident = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);     // file path -> item (zone-owned files that are loaded or loading)
    static readonly Dictionary<string, Item> deferred = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);     // file path -> item (zone-owned files skipped at start)
    static readonly List<Item> queue = new List<Item>(); static readonly Dictionary<string, float> campOut = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase); static readonly Dictionary<string, int> campState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> templatesDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static int lastStartFrame = -100000, winFrames, winOver, winStaged; static float winWorst, winEst; static bool winOpen; static float reportTexAt = -1f; static float texBefore;
    static int stagedTotal, droppedTotal, campDrops;

    /// <summary>ZoneSpan.ResetStatics (existing boot hook).</summary>
    public static void ResetStatics()
    {
        cfg = null; loaded = false; active = false; decodeActive = false; cur = null; arrivalAt = -1000f; queueReadyAt = -1f; keep.Clear(); outSince.Clear(); resident.Clear(); deferred.Clear(); queue.Clear(); campOut.Clear(); campState.Clear(); templatesDone.Clear();
        lastStartFrame = -100000; winFrames = winOver = winStaged = 0; winWorst = winEst = 0f; winOpen = false; reportTexAt = -1f; stagedTotal = droppedTotal = campDrops = 0;
    }
    /// <summary>ZoneSpan.Bind (a new scene = Joan is back in the start zone; the GlbPreload cache survives a scene load, so the next Poll recomputes the scope and queues drops / stages).</summary>
    public static void OnBind() { if (!On) return; cur = null; }

    public static bool On
    {
        get
        {
            if (!loaded) Load();
            return active;
        }
    }

    static void Load()
    {
        loaded = true; cfg = null; active = false; decodeActive = false;
        try
        {
            string t = HudCfg.ReadData("zone_scope.json");
            if (t != null) { cfg = JsonUtility.FromJson<ZscCfg>(t.TrimStart((char)0xFEFF)); }
            if (cfg != null)
            {
                if (cfg.hitch == null) cfg.hitch = new ZscHitch(); if (cfg.neighbours == null) cfg.neighbours = new ZscNeigh[0]; if (cfg.groups == null) cfg.groups = new ZscGroup[0]; if (cfg.decode == null) cfg.decode = new ZscDecode(); if (cfg.camps == null) cfg.camps = new ZscCamp[0];
                foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-zonescope:on") cfg.enabled = true; else if (a == "-zonescope:off") cfg.enabled = false; }
                foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-zonedecode:on") cfg.decode.mode = "uri"; else if (a == "-zonedecode:off") cfg.decode.mode = "off"; }
                active = cfg.enabled && !PerfFlags.HarnessRun();
                decodeActive = cfg.decode.mode == "uri" && (active || cfg.decode.standalone) && !PerfFlags.HarnessRun();
            }
        }
        catch (Exception e) { Debug.LogWarning("[ZoneScope] zone_scope.json unreadable, off: " + e.Message); cfg = null; active = false; }
        if (decodeActive) Debug.Log("[ZoneDecode] ON mode uri, scope " + cfg.decode.scope + ", minPixels " + cfg.decode.minPixels + ", nonReadable " + cfg.decode.nonReadable + ", stripMips " + cfg.decode.stripMips + ", maxParallel " + cfg.decode.maxParallel + ", spacing " + cfg.decode.imageSpacingFrames + " frames");
        Debug.Log("[ZoneScope] " + (active ? "ON, start " + cfg.startZone + ", " + cfg.groups.Length + " groups, keepLoadedMB " + cfg.keepLoadedMB + ", dropAfterS " + cfg.dropAfterS + ", lootAlts " + cfg.lootAlts : "off (data)"));
        if (active) SetCur(cfg.startZone, true);
    }

    // ------------------------------------------------------------------ scope
    static void SetCur(string zone, bool first)
    {
        float now = Time.unscaledTime; string prev = cur; cur = zone; if (!first) arrivalAt = now; keep.Clear();
        bool found = false;
        foreach (var n in cfg.neighbours) if (n != null && string.Equals(n.zone, zone, StringComparison.OrdinalIgnoreCase)) { found = true; if (n.keep != null) foreach (var k in n.keep) keep.Add(k); }
        keep.Add(zone);   // the current zone is always in scope, even with no neighbours row
        if (!found && cfg.log) Debug.LogWarning("[ZoneScope] no neighbours row for '" + zone + "': only the zone itself is kept");
        foreach (var g in cfg.groups) { if (g == null || g.zone.Length == 0) continue; if (keep.Contains(g.zone)) outSince.Remove(g.zone); else if (!outSince.ContainsKey(g.zone)) outSince[g.zone] = now; }
        foreach (var n in cfg.neighbours) if (n != null && n.zone.Length > 0) { if (keep.Contains(n.zone)) outSince.Remove(n.zone); else if (!outSince.ContainsKey(n.zone)) outSince[n.zone] = now; }
        // queue: every deferred or never-staged group file of a zone now in scope, plus its stageExtra files and its creature prefabs
        queue.Clear(); float estMb = 0f;
        foreach (var g in cfg.groups)
        {
            if (first) break;   // at start the GlbPreload late tier loads the in-scope files itself (Defer false); only a zone CHANGE stages ahead
            if (g == null || g.zone.Length == 0 || !keep.Contains(g.zone) || g.policy == "loot") continue;
            int n = Mathf.Max(1, g.files.Length + g.stageExtra.Length); float per = g.mpx / n * cfg.hitch.msPerMpx, mbp = g.mbEst / n;
            foreach (var f in g.files) AddQueue(f, g, per, mbp, ref estMb);
            foreach (var f in g.stageExtra) AddQueue(f, g, per, mbp, ref estMb);
        }
        if (!first) foreach (var n in cfg.neighbours) if (n != null && keep.Contains(n.zone)) QueueTemplates(n.zone);   // at start ZoneHitch.Warm loads the in-scope prefabs itself (SkipZone)
        queueReadyAt = now + cfg.hitch.quietAfterArrivalS;
        if (!first) { winOpen = true; winFrames = winOver = winStaged = 0; winWorst = 0f; winEst = 0f; }
        if (cfg.log) Debug.Log("[ZoneScope] scope " + (prev ?? "-") + " -> " + zone + " keep [" + string.Join(",", new List<string>(keep).ToArray()) + "] queue " + queue.Count + " (est " + estMb.ToString("0") + " MB to stage) resident est " + ResidentMb().ToString("0") + " MB texNow " + TexGb());
    }

    static void AddQueue(string file, ZscGroup g, float estMs, float mb, ref float estMb)
    {
        string path = FindAny(file); if (path == null) return; path = N(path);
        if (GlbPreload.WasStarted(path) && GlbPreload.IsCached(path)) { MarkResident(path, file, g, mb); return; }   // already parsed / loading
        if (resident.ContainsKey(path)) return;
        queue.Add(new Item { path = path, name = file, group = g.id, zone = g.zone, estMs = estMs, mb = mb }); estMb += mb;
    }

    static string N(string p) { try { return Path.GetFullPath(p); } catch (Exception) { return p; } }
    static string FindAny(string name)
    {
        try { foreach (var sub in new[] { "Gear", "Npc", "Mount", "MonsterLook" }) { string p = Path.Combine(Application.streamingAssetsPath, sub + "/" + name); if (File.Exists(p)) return p; } } catch (Exception) { }
        return null;
    }

    static void QueueTemplates(string zoneId)
    {
        if (!cfg.scopeTemplates) return; var zr = ZoneRuntime.Inst; if (zr == null) return;
        var z = ZoneData.Zone(zoneId); if (z == null || z.spawns == null) return;
        foreach (var s in z.spawns)
        {
            if (s == null || !s.enabled || s.picks == null) continue;
            foreach (var p in s.picks) { if (p == null || templatesDone.Contains(p.creature)) continue; var c = ZoneData.Creature(p.creature); if (c == null || c.source != "resources") continue; templatesDone.Add(p.creature); queue.Add(new Item { name = p.creature, zone = zoneId, template = true, estMs = cfg.templateMsEst }); }
        }
    }

    static void MarkResident(string path, string file, ZscGroup g, float mb) { if (!resident.ContainsKey(path)) resident[path] = new Item { path = path, name = file, group = g.id, zone = g.zone, mb = mb }; }

    static ZscGroup GroupOf(string path)
    {
        if (cfg == null) return null; string n = Path.GetFileName(path);
        foreach (var g in cfg.groups)
        {
            if (g == null) continue;
            foreach (var f in g.files) if (string.Equals(f, n, StringComparison.OrdinalIgnoreCase)) return g;
            foreach (var f in g.stageExtra) if (string.Equals(f, n, StringComparison.OrdinalIgnoreCase)) return g;
        }
        return null;
    }
    static float PerFile(ZscGroup g) { return g.mbEst / Mathf.Max(1, g.files.Length + g.stageExtra.Length); }

    /// <summary>ZoneDecode: true when decode mode is "uri" (and the scope is on, or decode.standalone).</summary>
    public static bool DecodeOn { get { if (!loaded) Load(); return decodeActive; } }
    internal static ZscCfg Cfg { get { return cfg; } }
    /// <summary>ZoneDecode: should this file's big images be decoded off the main thread (scope "all", or the file is in a zone_scope group).</summary>
    public static bool DecodeWants(string path) { return DecodeOn && (cfg.decode.scope == "all" || GroupOf(path) != null); }

    public static bool InKeep(string zone) { if (!On) return true; if (cur == null) SetCur(cfg.startZone, true); return string.IsNullOrEmpty(zone) || keep.Contains(zone); }

    // ------------------------------------------------------------------ hooks
    /// <summary>GlbPreloadRunner late loop: true = do NOT parse this file now (zone-owned and out of scope, or a loot alternate in on-demand mode). Files in no group are never deferred.</summary>
    public static bool Defer(string path)
    {
        if (!On) return false; path = N(path);
        if (GlbPreload.WasStarted(path)) { if (cfg.log) Debug.Log("[ZoneScope] already parsed or taken, not parsed a second time: " + Path.GetFileName(path)); return true; }   // v2: the double parse (W7): a consumer or the loading card started this file already
        var g = GroupOf(path); if (g == null) return false;
        if (cur == null) SetCur(cfg.startZone, true);
        if (g.policy != "loot" && keep.Contains(g.zone) && GlbPreload.WasStarted(path)) return true;   // staged ahead already: no second parse
        if (g.policy == "loot") { if (cfg.lootAlts == "ondemand") { if (cfg.log) Debug.Log("[ZoneScope] loot alternate left to the equip path: " + Path.GetFileName(path)); return true; } return false; }
        if (keep.Contains(g.zone)) { MarkResident(path, Path.GetFileName(path), g, PerFile(g)); return false; }
        deferred[path] = new Item { path = path, name = Path.GetFileName(path), group = g.id, zone = g.zone, mb = PerFile(g), estMs = g.mpx / Mathf.Max(1, g.files.Length + g.stageExtra.Length) * cfg.hitch.msPerMpx };
        if (cfg.log) Debug.Log("[ZoneScope] deferred " + Path.GetFileName(path) + " (zone " + g.zone + ", est " + PerFile(g).ToString("0") + " MB)");
        return true;
    }
    /// <summary>ZoneRuntime.SpanPreloadNames / SpanPreload: skip the creature prefabs of a zone that is out of scope (they are staged ahead by Tick instead).</summary>
    public static bool SkipZone(string zoneId) { return On && cfg.scopeTemplates && !InKeep(zoneId); }
    static ZscCamp CampRow(string zone) { if (cfg == null) return null; foreach (var c in cfg.camps) if (c != null && string.Equals(c.zone, zone, StringComparison.OrdinalIgnoreCase)) return c; return null; }
    /// <summary>HubNpcs.PreloadStep (poll): may the hidden camp load run now? With a camps[] row: only while playerZ is within aheadM before / behindM after the camp area and the scene is minAgeS old (z1 never loads the hub); without a row: the zone scope (v1).</summary>
    public static bool CampWanted(string zone, float zMin, float zMax, float playerZ, float sceneAgeS)
    {
        if (!On || !cfg.scopeCamps) return true; var r = CampRow(zone);
        if (r == null || float.IsNaN(playerZ)) return InKeep(zone);
        bool want = playerZ >= zMin - r.aheadM && playerZ <= zMax + r.behindM && sceneAgeS >= r.minAgeS;
        float now = Time.unscaledTime; int st = want ? 1 : 0;
        if (want) campOut.Remove(zone); else if (!campOut.ContainsKey(zone)) campOut[zone] = now;
        int prev; if (!campState.TryGetValue(zone, out prev) || prev != st) { campState[zone] = st; if (cfg.log) Debug.Log("[ZoneScope] camp " + zone + (want ? " in range" : " out of range") + " (player z " + playerZ.ToString("0") + ", area " + zMin.ToString("0") + ".." + zMax.ToString("0") + ", ahead " + r.aheadM.ToString("0") + " behind " + r.behindM.ToString("0") + " m, scene age " + sceneAgeS.ToString("0") + " s, minAge " + r.minAgeS.ToString("0") + ") texNow " + TexGb()); }
        return want;
    }
    /// <summary>HubNpcs.Update: a sleeping camp is cleared once the player has been beyond the range + hysteresisM for dropAfterS (never in a fight).</summary>
    public static bool CampDrop(string zone, float zMin, float zMax, float playerZ)
    {
        if (!On || !cfg.scopeCamps || !cfg.dropCamps) return false; var r = CampRow(zone);
        if (r == null || float.IsNaN(playerZ)) { if (InKeep(zone)) return false; float t0; if (!outSince.TryGetValue(zone, out t0)) return false; return Time.unscaledTime - t0 >= cfg.dropAfterS && !Engaged(); }
        bool inside = playerZ >= zMin - r.aheadM - r.hysteresisM && playerZ <= zMax + r.behindM + r.hysteresisM; if (inside) return false;
        float t; if (!campOut.TryGetValue(zone, out t)) return false; return Time.unscaledTime - t >= cfg.dropAfterS && !Engaged();
    }
    public static void NoteCampDropped(string zone) { campDrops++; if (cfg != null && cfg.log) Debug.Log("[ZoneScope] camp " + zone + " dropped (out of scope " + (Time.unscaledTime - (outSince.ContainsKey(zone) ? outSince[zone] : Time.unscaledTime)).ToString("0") + " s) texNow " + TexGb()); reportTexAt = Time.unscaledTime + 3f; texBefore = Texture.currentTextureMemory; }
    /// <summary>HubNpcs.LoadOne: the camp wants its body file now and nothing is cached for it: start the ONE parse through the staged path (smooth agent when C3.lateSmooth is on).</summary>
    public static bool StageForConsumer(string path) { return On && GlbPreload.StageAgain(N(path)); }   // v2: StageAgain also re-parses a file that was Taken / disposed before (a camp re-entered, a scene reload)

    public static bool Engaged() { try { var p = GameLoop.Player; return p != null && p.opponent != null && !p.opponent.Dead; } catch (Exception) { return false; } }
    static string TexGb() { return (Texture.currentTextureMemory / 1073741824.0).ToString("F2") + " GB"; }
    static float ResidentMb() { float m = 0f; foreach (var kv in resident) m += kv.Value.mb; return m; }

    /// <summary>ZoneSpan.Poll (every 0.25 s) with the id of the zone the player is in.</summary>
    public static void Poll(string zoneId)
    {
        if (!On || string.IsNullOrEmpty(zoneId)) return;
        if (cur == null || !string.Equals(cur, zoneId, StringComparison.OrdinalIgnoreCase)) SetCur(zoneId, false);
        float now = Time.unscaledTime;
        if (reportTexAt > 0f && now >= reportTexAt) { reportTexAt = -1f; if (cfg.log) Debug.Log("[ZoneScope] texNow after the drop: " + (texBefore / 1073741824.0).ToString("F2") + " -> " + TexGb()); }
        // drops: a resident zone file whose zone has been out of scope for dropAfterS (never while a fight is on)
        if (resident.Count > 0 && !Engaged())
        {
            List<string> gone = null;
            foreach (var kv in resident) { float t; if (kv.Value.zone.Length > 0 && !keep.Contains(kv.Value.zone) && outSince.TryGetValue(kv.Value.zone, out t) && now - t >= cfg.dropAfterS) { if (gone == null) gone = new List<string>(); gone.Add(kv.Key); } }
            if (gone != null)
            {
                float before = Texture.currentTextureMemory; float mb = 0f;
                foreach (var k in gone) { var it = resident[k]; resident.Remove(k); bool d = GlbPreload.Drop(k); mb += it.mb; deferred[k] = it; droppedTotal++; if (cfg.log) Debug.Log("[ZoneScope] drop " + it.name + " (zone " + it.zone + ", est " + it.mb.ToString("0") + " MB, " + (d ? "cache import disposed" : "already taken by a consumer: nothing to free here") + ")"); }
                texBefore = before; reportTexAt = now + 3f; if (cfg.log) Debug.Log("[ZoneScope] dropped " + gone.Count + " file(s), est " + mb.ToString("0") + " MB, resident est " + ResidentMb().ToString("0") + " MB");
            }
        }
        Stage(now);
    }

    static void Stage(float now)
    {
        if (queue.Count == 0) return; var h = cfg.hitch;
        bool inWin = now < queueReadyAt;                                      // the arrival window: nothing starts unless hitch.arrivalBudgetMs allows it
        if (inWin && h.arrivalBudgetMs <= 0f) return;
        if (Time.frameCount - lastStartFrame < Mathf.Max(1, h.spacingFrames)) return;
        if (Time.unscaledDeltaTime * 1000f > h.calmFrameMs) return;           // the last frame was not calm
        if (h.holdWhileEngaged && Engaged()) return;
        float started = 0f;
        while (queue.Count > 0 && started < h.perPollBudgetMs)
        {
            var it = queue[0];
            if (inWin && winEst + it.estMs > h.arrivalBudgetMs) return;
            if (!it.template && ResidentMb() + it.mb > cfg.keepLoadedMB) { if (cfg.log) Debug.Log("[ZoneScope] keepLoadedMB " + cfg.keepLoadedMB + " reached, not staging " + it.name); queue.RemoveAt(0); continue; }
            queue.RemoveAt(0);
            if (it.template)
            {
                var zr = ZoneRuntime.Inst; if (zr == null) continue; var go = zr.SpanTemplate(it.name);
                if (cfg.log) Debug.Log("[ZoneScope] stage prefab " + it.name + " (zone " + it.zone + ") " + (go != null ? "loaded" : "none") + " frame " + Time.frameCount);
            }
            else
            {
                if (!GlbPreload.Stage(it.path) && !GlbPreload.IsCached(it.path)) { /* already taken or started: nothing to do */ }
                resident[it.path] = it; deferred.Remove(it.path); stagedTotal++;
                if (cfg.log) Debug.Log("[ZoneScope] stage " + it.name + " (zone " + it.zone + ", est " + it.mb.ToString("0") + " MB, " + it.estMs.ToString("0") + " ms of decode) frame " + Time.frameCount + " texNow " + TexGb());
            }
            started += it.estMs; if (inWin) winEst += it.estMs; lastStartFrame = Time.frameCount; if (winOpen) winStaged++;
            break;   // one start per call: the next one waits spacingFrames (a 4096 decode is one frame; two in one poll would stack)
        }
    }

    /// <summary>ZoneSpan.Update (every frame): counts frames over hitch.countOverMs inside the arrival window (the COO measure), logs it when the window closes.</summary>
    public static void Frame()
    {
        if (!active || !winOpen) return;
        float dtMs = Time.unscaledDeltaTime * 1000f; float now = Time.unscaledTime; winFrames++;
        if (dtMs > cfg.hitch.countOverMs) { winOver++; if (dtMs > winWorst) winWorst = dtMs; }
        if (now - arrivalAt >= cfg.hitch.quietAfterArrivalS)
        {
            winOpen = false;
            if (cfg.log) Debug.Log("[ZoneScope] arrival " + cur + " window " + cfg.hitch.quietAfterArrivalS.ToString("0") + " s: " + winFrames + " frames, " + winOver + " over " + cfg.hitch.countOverMs + " ms, worst " + winWorst.ToString("0") + " ms, loads started in window " + winStaged + " (budget " + cfg.hitch.arrivalBudgetMs.ToString("0") + " ms), staged total " + stagedTotal + ", dropped " + droppedTotal + ", camp drops " + campDrops + ", resident est " + ResidentMb().ToString("0") + " MB texNow " + TexGb());
        }
    }
}

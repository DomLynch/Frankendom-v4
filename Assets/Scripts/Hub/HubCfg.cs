using System;
using UnityEngine;

// laneNPCSPAWN v1: the data side of the camp. Assets/Data/hub.json (copy in StreamingAssets), read through DataPath like every other loader. No character name, number or id lives in code:
// NPC ids / roles / spots / bodies / clips come from the file (the NPCs are camp roles, not roster characters; a roster id is never needed here).
// hub.json enabled=false (the shipped value) or a missing / broken file = HubCfg.Enabled is false and NOTHING else in this lane runs (no component, no allocation per frame, HubSafe is false).
// JsonUtility ignores every key not declared here, so the design notes in the file stay free text. UNPROVEN IN UNITY.
[Serializable] public class HubAreaRow { public float zMin = 0f, zMax = 0f, xHalf = 0f; }
[Serializable] public class HubSafeCombat { public bool playerAttack = true, playerGuard = true; public float damageToPlayer = 1f; public string[] allowPress = new string[0]; }
[Serializable] public class HubSafeRow { public HubSafeCombat combat = new HubSafeCombat(); }
[Serializable] public class HubInteract { public string kind = "none", shop = ""; public float radius = 0f; }
[Serializable] public class HubRuntime { public string glb = "", clipGlb = "", clipName = ""; public string[] sources = new string[0], clipSources = new string[0], hideNodes = new string[0]; public float scale = 1f; }
[Serializable] public class HubNpcRow
{
    public string id = "", role = "", title = "", spot = "", clip = ""; public bool enabled = true; public float yaw = 0f, phaseSeconds = 0f;
    public HubInteract interact = new HubInteract(); public HubRuntime runtime = new HubRuntime();
}
[Serializable] public class HubNpcRules { public int maxSimultaneous = 3; }
[Serializable] public class HubSpawner
{
    public float pollSeconds = 0.25f, spawnMarginM = 12f, despawnMarginM = 16f, groundRayUpM = 3f, groundRayDownM = 10f, yawOffsetDeg = 0f;
    public string leaveMode = "destroy"; public int maxLoadsInFlight = 1, framesBetweenLoads = 2, groundMask = -1;
    public bool shadows = false, cullWhenOffscreen = true, logLoads = true; public string[] searchRoots = new string[0]; public string bodyName = "Body";
}
[Serializable] public class HubTagRow { public bool enabled = false; public float[] colour = { 0.78f, 0.66f, 0.35f, 1f }; public float top = 26f, tracking = 3.2f; public int big = 20, small = 15; }
[Serializable] public class HubToastRow { public float seconds = 1.2f, bottom = 150f; public int size = 24; }
[Serializable] public class HubIndicator { public HubTagRow hudTag = new HubTagRow(); public HubToastRow refuseToast = new HubToastRow(); }
[Serializable] public class HubText { public string hudTag = "", noFight = ""; }
[Serializable] public class HubBuyRow
{
    public bool enabled; public float mul = 1f; public int minPrice = 1; public string[] items = new string[0], tabKeys = new string[0];
    public string tabSell = "", tabBuy = "", subtitleBuy = "", hintMoveTab = "", hintBuy = "", armedBuy = "", bought = "", detailAsks = "", tagOwned = "", tagPoor = "";
}
[Serializable] public class HubStallRow { public HubBuyRow buy = new HubBuyRow(); }
[Serializable] public class HubData
{
    public bool enabled; public int version = 1; public string zone = "";
    public HubAreaRow area = new HubAreaRow(); public HubSafeRow safe = new HubSafeRow(); public HubNpcRow[] npcs = new HubNpcRow[0]; public HubNpcRules npcRules = new HubNpcRules();
    public HubStallRow stall = new HubStallRow(); public HubSpawner spawner = new HubSpawner(); public HubIndicator indicator = new HubIndicator(); public HubText text = new HubText();
}

public static class HubCfg
{
    static HubData d;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { d = null; }

    public static HubData Get()
    {
        if (d != null) return d;
        d = new HubData();
        string txt = DataPath.ReadText("hub.json");
        if (txt != null) { try { var k = JsonUtility.FromJson<HubData>(txt); if (k != null) d = k; } catch (Exception e) { Debug.LogWarning("[Hub] hub.json unreadable, camp off: " + e.Message); d = new HubData(); } }
        Fix(d); return d;
    }
    public static void Reload() { d = null; }
    public static bool Enabled { get { return Get().enabled; } }

    static void Fix(HubData k)
    {
        if (k.area == null) k.area = new HubAreaRow();
        if (k.safe == null) k.safe = new HubSafeRow();
        if (k.safe.combat == null) k.safe.combat = new HubSafeCombat();
        if (k.safe.combat.allowPress == null) k.safe.combat.allowPress = new string[0];
        if (k.npcs == null) k.npcs = new HubNpcRow[0];
        if (k.npcs.Length > 0) k.enabled = ZoneFlag.Allowed(k.enabled);   // INT108 v1: -zones turns the camp on, -nozones off, no flag = the file
        if (k.npcRules == null) k.npcRules = new HubNpcRules();
        if (k.spawner == null) k.spawner = new HubSpawner();
        if (k.spawner.searchRoots == null) k.spawner.searchRoots = new string[0];
        if (k.indicator == null) k.indicator = new HubIndicator();
        if (k.indicator.hudTag == null) k.indicator.hudTag = new HubTagRow();
        if (k.indicator.refuseToast == null) k.indicator.refuseToast = new HubToastRow();
        if (k.text == null) k.text = new HubText();
        if (k.stall == null) k.stall = new HubStallRow();
        if (k.stall.buy == null) k.stall.buy = new HubBuyRow();
        if (k.stall.buy.items == null) k.stall.buy.items = new string[0];
        if (k.stall.buy.tabKeys == null) k.stall.buy.tabKeys = new string[0];
        foreach (var n in k.npcs)
        {
            if (n == null) continue;
            if (n.interact == null) n.interact = new HubInteract();
            if (n.runtime == null) n.runtime = new HubRuntime();
            if (n.runtime.sources == null) n.runtime.sources = new string[0];
            if (n.runtime.clipSources == null) n.runtime.clipSources = new string[0];
            if (n.runtime.hideNodes == null) n.runtime.hideNodes = new string[0];
        }
        k.spawner.pollSeconds = Mathf.Max(0.05f, k.spawner.pollSeconds); k.spawner.maxLoadsInFlight = Mathf.Max(1, k.spawner.maxLoadsInFlight); k.spawner.framesBetweenLoads = Mathf.Max(0, k.spawner.framesBetweenLoads);
        if (k.enabled && (k.area.zMax < k.area.zMin || k.area.xHalf <= 0f)) { Debug.LogWarning("[Hub] hub.json area is empty or inverted, camp off"); k.enabled = false; }
    }

    /// <summary>World x / z of a camp spot (zones.json). False when the zone or spot is missing.</summary>
    public static bool SpotXZ(string zoneId, string spotId, out float x, out float z)
    {
        x = z = 0f; var zz = ZoneData.Zone(zoneId); if (zz == null) return false;
        var sp = ZoneData.Spot(zz, spotId); if (sp == null || sp.pos == null || sp.pos.Length < 2) return false;
        x = sp.pos[0]; z = sp.pos[1]; return true;
    }
}

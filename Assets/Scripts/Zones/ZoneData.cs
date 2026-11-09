using System;
using System.Collections.Generic;
using UnityEngine;

// ZONES v1: the data side. Assets/Data/zones.json (copy in StreamingAssets). No character name, number or spot lives in code: ids come from the file and from Roster.
// A missing or broken file = zones off (the game is the old duel). enabled=false = zones off.
[Serializable] public class ZSpot { public string id = "", note = ""; public float[] pos = new float[0]; public float yaw; }
[Serializable] public class ZPick { public string creature = ""; public int weight = 1; }
[Serializable] public class ZSpawn
{
    public string id = "", spot = "", ladderRow = "", needs = "";
    public bool enabled = true;
    public ZPick[] picks = new ZPick[0];
    public int respawnMinS = 300, respawnMaxS = 600, difficulty = 0;
    public float aggro = 7f, leash = 24f, hpMul = 1f, damageMul = 1f;
}
[Serializable] public class ZGate
{
    public string id = "", spot = "", toZone = "", toSpot = "", unlockKilledSpawn = "", visual = "none";
    public float radius = 2f; public bool sticky = true;
    public string endScreen = "";   // ENDALPHA v1
}
[Serializable] public class ZService { public string id = "", kind = "", spot = "", needs = ""; public bool enabled = false; }
[Serializable] public class ZLimits { public float zMax = float.NaN; }
[Serializable] public class ZZone
{
    public string id = "", name = "", scene = "", mode = "inplace";
    public bool safe, start;
    public string[] sceneRoots = new string[0];
    public ZLimits limits = new ZLimits();
    public ZSpot[] spots = new ZSpot[0]; public ZSpawn[] spawns = new ZSpawn[0]; public ZGate[] gates = new ZGate[0]; public ZService[] services = new ZService[0];
}
[Serializable] public class ZCreature { public string id = "", source = "scene", path = "", scene = "";   /* ZONES-ATTACH v1: enemy scene the prefab is saved from (empty = the zone scene) */ }
[Serializable] public class ZStart { public string zone = "", spot = ""; }
[Serializable] public class ZClock { public int guardSeconds = 120; }
[Serializable] public class ZPersist { public string file = "zones_save.json"; public int version = 1; public float saveEverySeconds = 30f; public bool resetOnNewGame = false, skipSaveWhileDown = false, spanWarmFinally = false; /* SAVEFIX v2: all three default to today's behaviour, zones.json persistence turns them on */ }
// REVIVE v1 (laneSEAMLESS): zones.json hp.respawn = Joan's in-world respawn. enabled false = the old death (black, GameLoop.Retry reload). rule: arrive = the zone's arrive spot from `arrive` rows, hearth = the zone's hearth service spot (arrive if none).
[Serializable] public class ZArrive { public string zone = "", spot = ""; }
[Serializable] public class ZRespawn
{
    public bool enabled = false; public string rule = "arrive", foeReset = "spawn"; public ZArrive[] arrive = new ZArrive[0];
    public float delayS = 4f, hpFrac = 1f, coverS = 0.5f, coverMax = 1f, foeResetDelayS = 8f, fallbackAfterS = 12f;
}
[Serializable] public class ZHp
{
    public string onCross = "carry", onDeath = "hearth_full";
    public float crossFloorFrac = 0.35f, regenDelaySeconds = 6f, regenPerSecondFrac = 0.01f, regenCeilDangerFrac = 0.6f, regenCeilSafeFrac = 1f, hearthRadius = 2.5f, hearthRegenPerSecondFrac = 0.08f, deathHoldSeconds = 3f;
    public ZRespawn respawn = new ZRespawn();   // REVIVE v1
}
[Serializable] public class ZEngage { public int maxEngaged = 1; public float retargetMarginM = 1.5f; public bool finisherWithOthers = false;   /* MULTI v1 */ public float graceAfterArriveSeconds = 3f, corpseSeconds = 25f, spawnMinDistance = 30f; }
[Serializable] public class ZCover { public float[] color = new float[] { 1f, 0.84f, 0.58f }; public float inSeconds = 0.3f, holdMinSeconds = 0.1f, outSeconds = 0.55f, holdMaxSeconds = 8f; }
[Serializable] public class ZCrossing { public ZCover cover = new ZCover(); public bool allowWhileEngaged = false; public float preloadDistance = 14f; public bool freePassage = false; /* ALPHAFLOW v1 */ }

// TWOFIGHT v1 (laneTWOFIGHT): zones.json "twoFight" = the switches of the two-enemy rules (all rows in data; defaults here = the shipped rows)
[Serializable] public class ZTwoFight { public bool skipGivingUp = true, healTarget = true, noEngageInFinisher = true, killerExempt = true, safeZoneGiveUp = true, resetBarOnSwitch = true, clearStaleOpponent = false; public float staleMaxM = 0f; }   /* OPPSTALE v1 */
[Serializable] public class ZoneTable
{
    public bool enabled = false; public int version = 1; public float pollSeconds = 0.5f;
    public ZStart start = new ZStart(); public ZClock clock = new ZClock(); public ZPersist persistence = new ZPersist(); public ZHp hp = new ZHp();
    public ZEngage engage = new ZEngage(); public ZCrossing crossing = new ZCrossing(); public ZTwoFight twoFight = new ZTwoFight();   /* TWOFIGHT v1 */
    public ZCreature[] creatures = new ZCreature[0]; public ZZone[] zones = new ZZone[0];
    public EndScreenRow[] endScreens = new EndScreenRow[0];   // ENDALPHA v1
}

public static class ZoneData
{
    static ZoneTable t;
    public static string LastError = "";
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { t = null; LastError = ""; }

    public static ZoneTable Get()
    {
        if (t != null) return t;
        t = new ZoneTable();
        string txt = HudCfg.ReadData("zones.json");
        if (txt != null)
        {
            try { var k = JsonUtility.FromJson<ZoneTable>(txt); if (k != null) t = k; }
            catch (Exception e) { LastError = e.Message; Debug.LogWarning("[Zones] zones.json unreadable, zones off: " + e.Message); t = new ZoneTable(); }
        }
        Fix(t); return t;
    }
    public static void Reload() { t = null; }
    static void Fix(ZoneTable k)
    {
        if (k.zones == null) k.zones = new ZZone[0]; if (k.creatures == null) k.creatures = new ZCreature[0];
        if (k.start == null) k.start = new ZStart(); if (k.clock == null) k.clock = new ZClock(); if (k.persistence == null) k.persistence = new ZPersist();
        if (k.hp == null) k.hp = new ZHp(); if (k.engage == null) k.engage = new ZEngage(); if (k.crossing == null) k.crossing = new ZCrossing(); if (k.crossing.cover == null) k.crossing.cover = new ZCover();
        if (k.twoFight == null) k.twoFight = new ZTwoFight(); /* TWOFIGHT v1 */ k.pollSeconds = Mathf.Max(0.1f, k.pollSeconds); k.engage.maxEngaged = Mathf.Max(1, k.engage.maxEngaged);
        foreach (var z in k.zones)
        {
            if (z == null) continue;
            if (z.spots == null) z.spots = new ZSpot[0]; if (z.spawns == null) z.spawns = new ZSpawn[0]; if (z.gates == null) z.gates = new ZGate[0]; if (z.services == null) z.services = new ZService[0];
            if (z.sceneRoots == null) z.sceneRoots = new string[0]; if (z.limits == null) z.limits = new ZLimits();
            foreach (var s in z.spawns) if (s != null) { if (s.picks == null) s.picks = new ZPick[0]; s.respawnMinS = Mathf.Max(1, s.respawnMinS); s.respawnMaxS = Mathf.Max(s.respawnMinS, s.respawnMaxS); s.leash = Mathf.Max(s.leash, s.aggro + 1f); }
        }
    }

    public static bool On { get { var k = Get(); return ZoneFlag.Allowed(k.enabled) && k.zones.Length > 0; } }   // ZONEFLAG v1
    public static ZZone Zone(string id) { foreach (var z in Get().zones) if (z != null && z.id == id) return z; return null; }
    public static ZZone StartZone() { var k = Get(); var z = Zone(k.start.zone); if (z != null) return z; foreach (var q in k.zones) if (q != null && q.start) return q; return k.zones.Length > 0 ? k.zones[0] : null; }
    public static ZSpot Spot(ZZone z, string id) { if (z != null) foreach (var s in z.spots) if (s != null && s.id == id) return s; return null; }
    public static ZSpawn Spawn(ZZone z, string id) { if (z != null) foreach (var s in z.spawns) if (s != null && s.id == id) return s; return null; }
    public static ZCreature Creature(string id) { foreach (var c in Get().creatures) if (c != null && c.id == id) return c; return null; }
    /// <summary>Zone and row of a spawn id anywhere (gate unlock rules name a spawn of another zone).</summary>
    public static ZSpawn FindSpawn(string id, out ZZone zone)
    {
        foreach (var z in Get().zones) { var s = Spawn(z, id); if (s != null) { zone = z; return s; } }
        zone = null; return null;
    }
    /// <summary>The zone with a service of this kind (the hearth a dead player wakes at); the start zone first.</summary>
    public static ZZone HearthZone()
    {
        var sz = StartZone(); if (sz != null && HasService(sz, "hearth")) return sz;
        foreach (var z in Get().zones) if (z != null && HasService(z, "hearth")) return z;
        return sz;
    }
    public static bool HasService(ZZone z, string kind) { foreach (var s in z.services) if (s != null && s.enabled && s.kind == kind) return true; return false; }
    public static ZService Service(ZZone z, string kind) { foreach (var s in z.services) if (s != null && s.enabled && s.kind == kind) return s; return null; }

    /// <summary>Plain list of data mistakes (also run by zones_check.py): empty = fine.</summary>
    public static List<string> Validate()
    {
        var e = new List<string>(); var k = Get(); var ids = new HashSet<string>();
        foreach (var z in k.zones)
        {
            if (z == null) continue; if (!ids.Add("zone:" + z.id)) e.Add("duplicate zone " + z.id);
            foreach (var s in z.spawns)
            {
                if (s == null) continue; if (!ids.Add("spawn:" + s.id)) e.Add("duplicate spawn " + s.id);
                if (Spot(z, s.spot) == null) e.Add("spawn " + s.id + ": spot '" + s.spot + "' not in zone " + z.id);
                if (s.enabled) foreach (var p in s.picks) if (p != null && Creature(p.creature) == null) e.Add("spawn " + s.id + ": creature '" + p.creature + "' has no creatures[] row");
            }
            foreach (var g in z.gates)
            {
                if (g == null) continue;
                if (Spot(z, g.spot) == null) e.Add("gate " + g.id + ": spot '" + g.spot + "' not in zone " + z.id);
                var to = Zone(g.toZone); if (to == null) { e.Add("gate " + g.id + ": toZone '" + g.toZone + "' unknown"); continue; }
                if (Spot(to, g.toSpot) == null) e.Add("gate " + g.id + ": toSpot '" + g.toSpot + "' not in zone " + g.toZone);
                if (g.unlockKilledSpawn.Length > 0) { ZZone uz; if (FindSpawn(g.unlockKilledSpawn, out uz) == null) e.Add("gate " + g.id + ": unlockKilledSpawn '" + g.unlockKilledSpawn + "' unknown"); }
            }
        }
        return e;
    }
}

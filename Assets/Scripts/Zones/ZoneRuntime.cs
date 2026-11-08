using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// ZONES v1: walking zones instead of "win -> reload the scene". DARK: this object is only created when zones.json enabled=true and the run is not a harness run, so with zones off no line below executes
// and Duel.cs (the 60 Hz core), DuelFighter, the sim and its hash are untouched. Even with zones on it never writes into the sim's decision path: it changes WHO is registered/active (spawn, despawn,
// engage = enable that creature's AIBrain and set opponent), the player's hp at a gate/hearth (counted in sim ticks, see TickRegen) and the player's position under the gate flash.
// UNPROVEN IN UNITY. Checkpoints for the Unity dev are listed in MANIFEST.md section 9.
public sealed class ZoneRuntime : MonoBehaviour
{
    public static ZoneRuntime Inst;
    /// <summary>GameLoopDriver stands down while this is true (no victory card, no scene reload per fight).</summary>
    public static bool Owns { get { return Inst != null && Inst.on; } }

    enum Phase { Play, CoverIn, Hold, CoverOut, Down }
    sealed class Live { public ZSpawn row; public string creature; public int gen; public GameObject go; public DuelFighter df; public AIBrain brain; public Vector3 home; public float homeYaw; public bool engaged, killed; public float deadAt; public string zoneId; public ChaseAgent chase; /* CHASE v1 */ }

    bool on; int lastRebind = -1;
    ZoneTable T; ZZone zone; string lastSpot = ""; Phase phase; float phaseT, cover, arriveGraceUntil, nextPoll, nextSave, holdT; Color coverColor = Color.white;
    DuelFighter player; GatewayRift rift; bool riftOpen; ZGate crossGate; int lastTick, combatTicks; bool downHandled;
    readonly Dictionary<string, Live> live = new Dictionary<string, Live>();
    readonly Dictionary<string, GameObject> templates = new Dictionary<string, GameObject>();
    readonly HashSet<string> warned = new HashSet<string>(), noPrefab = new HashSet<string>();
    readonly Dictionary<string, AsyncOperation> loading = new Dictionary<string, AsyncOperation>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { Inst = null; FoeId = ""; }   /* TWOFIGHT v1 */

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Inst != null || !ZoneSave.Active) return;
        var errs = ZoneData.Validate(); if (errs.Count > 0) { foreach (var e in errs) Debug.LogWarning("[Zones] data: " + e); Debug.LogWarning("[Zones] zones.json has errors, zones stay off"); return; }
        var go = new GameObject("ZoneRuntime"); DontDestroyOnLoad(go); Inst = go.AddComponent<ZoneRuntime>();
    }
    void OnEnable() { SceneManager.sceneLoaded += OnLoaded; }
    void OnDisable() { SceneManager.sceneLoaded -= OnLoaded; }
    void Start() { Rebind(); }
    void OnLoaded(Scene s, LoadSceneMode m) { if (m == LoadSceneMode.Single) { loading.Clear(); Rebind(); } }
    void OnApplicationQuit() { SaveNow(); }
    void OnApplicationPause(bool p) { if (p) SaveNow(); }

    // ------------------------------------------------------------------ (re)bind to a freshly loaded duel scene
    void Rebind()
    {
        if (lastRebind == Time.frameCount) return; lastRebind = Time.frameCount;
        on = false; T = ZoneData.Get(); live.Clear(); templates.Clear(); player = null; rift = null; riftOpen = false; phase = Phase.Play; cover = 0f; downHandled = false; crossGate = null;
        var found = new List<DuelFighter>();
        foreach (var f in FindObjectsByType<DuelFighter>(FindObjectsInactive.Include, FindObjectsSortMode.None)) { if (f.name.Contains(Roster.PlayerObject)) player = f; else found.Add(f); }
        if (player == null) { Debug.LogWarning("[Zones] no player fighter in this scene, zones off here"); return; }
        foreach (var f in found)   // the enemy objects the scene was built with become inactive TEMPLATES; every spawn is a clone
        {
            string id = Roster.IdOf(f.transform); if (id.Length == 0 || templates.ContainsKey(id)) continue;
            templates[id] = f.gameObject; f.gameObject.SetActive(false);
        }
        rift = FindFirstObjectByType<GatewayRift>();
        var sv = ZoneSave.Data; ZZone z = null; string spot = T.start.spot;
        if (sv.player.has) { z = ZoneData.Zone(sv.player.zone); if (z != null) spot = sv.player.spot; }
        if (z == null) { z = ZoneData.StartZone(); spot = T.start.spot; }
        if (z == null) return;
        on = true; GameLoop.Player = player; GameLoop.Ready = true; GameLoop.LadderApplied = true;
        if (sv.player.has && player.maxHealth > 0f) SetHp(Mathf.Max(1f / player.maxHealth, sv.player.hpFrac) * player.maxHealth);
        var sp = ZoneData.Spot(z, spot); Vector3 pos = player.transform.position;
        EnterZone(z, spot, false);
        if (sp != null && sp.pos.Length >= 2) { Vector3 np = SpotPos(sp, pos); Place(player, np, sp.yaw); ZoneGround.Begin(player, np); /* CAMPGROUND v1 */ }
        arriveGraceUntil = Time.unscaledTime + T.engage.graceAfterArriveSeconds; lastTick = Duel.TickCount; combatTicks = 0;
        LookSpawns(true); Debug.Log("[Zones] on: zone " + z.id + " spot " + spot);
    }

    // ------------------------------------------------------------------ zone enter / leave
    void EnterZone(ZZone z, string spot, bool fromGate)
    {
        zone = z; lastSpot = spot;
        ArenaLimits.ZoneZMax = z.limits != null ? z.limits.zMax : float.NaN;
        // roots: only the current zone's roots are active (one arena drawn at a time)
        foreach (var q in T.zones) if (q != null && q.sceneRoots != null) SetRoots(q.sceneRoots, q == z);
        if (z.mode == "additive" && z.scene.Length > 0) { var sc = SceneManager.GetSceneByName(z.scene); if (sc.IsValid() && sc.isLoaded) { SetSceneRoots(sc, true); SceneManager.SetActiveScene(sc); } }
        foreach (var q in T.zones) if (q != null && q != z && q.mode == "additive" && q.scene.Length > 0) { var sc = SceneManager.GetSceneByName(q.scene); if (sc.IsValid() && sc.isLoaded) SetSceneRoots(sc, false); }
        if (fromGate) { arriveGraceUntil = Time.unscaledTime + T.engage.graceAfterArriveSeconds; }
        UpdateRift(true);
    }
    // ZONE2DUP v1: the span (a physical duplicate zone, walked into) switches the zone without a flash, a teleport or a despawn of anything engaged. Called only by ZoneSpan.
    public bool SpanEnter(string zoneId, string spot)
    {
        var z = ZoneData.Zone(zoneId); if (!on || z == null || z == zone) return false;
        zone = z; if (spot.Length > 0) lastSpot = spot; ArenaLimits.ZoneZMax = z.limits != null ? z.limits.zMax : float.NaN; arriveGraceUntil = 0f; UpdateRift(true); LookSpawns(false); return true;
    }
    public void SpanRestoreLimits() { if (zone != null) ArenaLimits.ZoneZMax = zone.limits != null ? zone.limits.zMax : float.NaN; }
    public void SpanWarm(string zoneId) { var z = ZoneData.Zone(zoneId); if (!on || z == null || z == zone || phase != Phase.Play) return; var keep = zone; zone = z; long zs = ZoneHitch.Mark(); LookSpawns(true); ZoneHitch.Lap("spanWarm.lookSpawns " + zoneId, ref zs); zone = keep; }
    // ZONEHITCH v1: load (and keep) every Resources prefab a zone spawn row can pick, before the player walks there; returns those prefab assets (ZoneHitch draws their materials once). Never instantiates, never touches `live`.
    public List<GameObject> SpanPreload()
    {
        var r = new List<GameObject>(); if (T == null || T.zones == null) return r;
        foreach (var q in T.zones) { if (q == null || q.spawns == null) continue; foreach (var s in q.spawns) { if (s == null || !s.enabled || s.picks == null) continue; foreach (var p in s.picks) { if (p == null) continue; var c = ZoneData.Creature(p.creature); if (c == null || c.source != "resources") continue; var g = Template(p.creature); if (g != null && !r.Contains(g)) r.Add(g); } } }
        return r;
    }
    public int SpanDespawn(string zoneId)   // idle creatures of that zone only: never an engaged one, never a corpse
    {
        List<string> rm = null;
        foreach (var kv in live) { var l = kv.Value; if (l.zoneId != zoneId || l.engaged || l.killed || l.go == null) continue; Destroy(l.go); (rm ?? (rm = new List<string>())).Add(kv.Key); }
        if (rm != null) foreach (var k in rm) live.Remove(k);
        return rm == null ? 0 : rm.Count;
    }
    /// <summary>The same rule the flash gate uses (laneCHASE): shut while a creature is engaged that cannot follow.</summary>
    public bool SpanGateBlocked() { return on && phase == Phase.Play && !T.crossing.allowWhileEngaged && !T.crossing.freePassage /* ALPHAFLOW v1 */ && EngagedCount() > 0 && !AllEngagedFollow(); }
    static void SetRoots(string[] names, bool active)
    {
        var sc = SceneManager.GetActiveScene(); var roots = sc.GetRootGameObjects();
        foreach (var n in names) foreach (var r in roots) if (r.name == n) r.SetActive(active);
    }
    static void SetSceneRoots(Scene sc, bool active) { foreach (var r in sc.GetRootGameObjects()) r.SetActive(active); }

    // ------------------------------------------------------------------ per frame
    void Update()
    {
        if (!on || player == null) return;
        float dt = Time.unscaledDeltaTime; Hud(); FoeResetStep();   // REVIVE v1
        switch (phase)
        {
            case Phase.CoverIn: phaseT += dt; cover = Mathf.Clamp01(phaseT / Mathf.Max(0.01f, T.crossing.cover.inSeconds)); if (cover >= 1f) { phase = Phase.Hold; holdT = 0f; coverColor = ColorOf(T.crossing.cover.color); } break;
            case Phase.Hold: HoldStep(dt); break;
            case Phase.CoverOut: phaseT += dt; cover = 1f - Mathf.Clamp01(phaseT / Mathf.Max(0.01f, T.crossing.cover.outSeconds)); if (cover <= 0f) { cover = 0f; phase = Phase.Play; } break;
            case Phase.Down: DownStep(dt); break;
        }
        if (phase == Phase.Play || phase == Phase.CoverOut)
        {
            if (player.Dead) { BeginDown(); }
            else { Engagement(); KillWatch(); TickRegen(); if (phase == Phase.Play) CheckGates(); }
        }
        if (ZoneEndScreen.Active) EndScreenStep();   // ENDALPHA v1
        if (Time.unscaledTime >= nextPoll) { nextPoll = Time.unscaledTime + T.pollSeconds; Poll(); }
        if (Time.unscaledTime >= nextSave) { nextSave = Time.unscaledTime + Mathf.Max(5f, T.persistence.saveEverySeconds); SaveNow(); }
    }

    /// <summary>GameLoop.Player / Enemy / Ready are what the HUD, camera framing and loot read; GameLoopDriver (which normally fills them) stands down while zones own the fight.</summary>
    void Hud()
    {
        GameLoop.Player = player; GameLoop.Ready = true; GameLoop.LadderApplied = true;
        if (GameLoop.Enemy != null && Find(GameLoop.Enemy) == null) GameLoop.Enemy = null;   // the template / a despawned clone
        Live best = null; float bd = float.MaxValue;
        foreach (var l in live.Values)
        {
            if (l.df == null) continue;
            if (l.engaged && !l.killed && (player.opponent == null || l.df == player.opponent)) { best = l; break; }   // MULTI v1 (Hud): the HUD enemy is the player's target
            float d = Flat(player.transform.position, l.df.transform.position); if (!l.killed && d < bd) { bd = d; best = l; }
        }
        if (best != null && (GameLoop.Enemy == null || EngagedCount() > 0 || !GameLoop.Enemy.Dead)) GameLoop.Enemy = best.df;
    }
    Live Find(DuelFighter f) { foreach (var l in live.Values) if (l.df == f) return l; return null; }

    void Poll()
    {
        if (phase == Phase.Play) LookSpawns(false);
        UpdateRift(false); Preload(); Corpses();
    }

    // ------------------------------------------------------------------ spawns: computed when someone looks
    void LookSpawns(bool arriving)
    {
        if (zone == null) return; long now = ZoneCore.NowMs();
        foreach (var row in zone.spawns)
        {
            if (row == null || !row.enabled || live.ContainsKey(row.id)) continue;
            var rec = ZoneSave.Rec(zone.id, row.id);
            if (ZoneCore.ClockGuard(rec, now, row.respawnMaxS, T.clock.guardSeconds)) ZoneSave.Put(rec);
            var look = ZoneCore.Look(rec, now); if (!look.alive) continue;
            if (!arriving && !CanAppear(row)) continue;   // never pop in front of the player: on arrival (under the flash), or off screen, or spawnMinDistance away
            SpawnOne(row, look.generation);
        }
    }
    /// <summary>A creature whose time is up appears when the player cannot see its spot (off screen) or is spawnMinDistance away, never in front of them.</summary>
    bool CanAppear(ZSpawn row)
    {
        var sp = ZoneData.Spot(zone, row.spot); if (sp == null) return false;
        Vector3 pp = player.transform.position; Vector3 at = SpotPos(sp, pp);
        if (sp.pos.Length < 2) { var tpl = Template(row.picks.Length > 0 ? row.picks[0].creature : ""); if (tpl == null || !tpl.scene.IsValid()) return false; at = tpl.transform.position; }
        if (Flat(pp, at) >= T.engage.spawnMinDistance) return true;
        var cam = Camera.main; if (cam == null) return false;
        Vector3 v = cam.WorldToViewportPoint(at + Vector3.up * 1.0f);
        return v.z <= 0f || v.x < -0.15f || v.x > 1.15f || v.y < -0.15f || v.y > 1.15f;
    }

    GameObject Template(string creature)
    {
        GameObject g; if (templates.TryGetValue(creature, out g) && g != null) return g;
        var c = ZoneData.Creature(creature);
        if (c != null && c.source == "resources" && c.path.Length > 0 && !noPrefab.Contains(creature)) { long zt = ZoneHitch.Mark(); g = Resources.Load<GameObject>(c.path); ZoneHitch.Lap("template.load " + creature, ref zt); if (g != null)  /* ZONEHITCH v1 */ { templates[creature] = g; return g; } noPrefab.Add(creature); }
        return null;
    }

    // ZONES-ATTACH v1 (INT107): the per-object form of every AfterSceneLoad attacher a fighter needs (same data want-checks, GetComponent guards, so a component already there is never doubled).
    static void AttachRuntime(GameObject go)
    {
        MonsterLook.AttachTo(go);   /* MONSTERLOOK v1: swap mesh + textures to the data look (same frame) when the character row says so; the prefab look otherwise */
        ArmHold.AttachTo(go); WeaponGrip.AttachTo(go); TailSpring.AttachTo(go); FootGround.AttachTo(go); FootYaw.AttachTo(go); HeadClamp.AttachTo(go);
        GuardTorsoTurn.AttachTo(go); GearSlots.AttachTo(go); HitReact.AttachTo(go);
    }
    void SpawnOne(ZSpawn row, int gen)
    {
        long zh = ZoneHitch.Mark();  /* ZONEHITCH v1 */
        var w = new int[row.picks.Length];
        for (int i = 0; i < w.Length; i++) w[i] = (row.picks[i] != null && Template(row.picks[i].creature) != null) ? Mathf.Max(0, row.picks[i].weight) : 0;
        int idx = ZoneCore.PickCreature(zone.id, row.id, gen, w);
        if (idx < 0) { Warn(row.id, "[Zones] spawn " + row.id + ": no creature available yet (missing scene object or Resources prefab), skipped"); return; }
        string cid = row.picks[idx].creature; var tpl = Template(cid); var sp = ZoneData.Spot(zone, row.spot);
        bool hasPos = sp != null && sp.pos.Length >= 2;
        if (!hasPos && (tpl.scene.IsValid() == false)) { Warn(row.id, "[Zones] spawn " + row.id + ": spot has no pos and the creature is not a scene object, skipped"); return; }
        Vector3 home = hasPos ? SpotPos(sp, tpl.transform.position) : tpl.transform.position; float yaw = sp != null ? sp.yaw : 0f;
        var go = Instantiate(tpl, home, Quaternion.Euler(0f, yaw, 0f)); go.name = Roster.Get(cid).objectName + "#" + row.id; ZoneHitch.Lap("spawn.instantiate " + cid, ref zh);  /* ZONEHITCH v1 */
        var df = go.GetComponent<DuelFighter>(); var br = go.GetComponent<AIBrain>();
        if (df == null) { Destroy(go); Warn(row.id, "[Zones] spawn " + row.id + ": clone has no DuelFighter"); return; }
        if (br != null) br.enabled = false;   // dormant until the player walks into its aggro radius
        { var zc = ZoneData.Creature(cid); if (zc != null && zc.source == "resources") AttachRuntime(go); } ZoneHitch.Lap("spawn.attachers", ref zh);  /* ZONEHITCH v1 */   // ZONES-ATTACH v1: a prefab saved in the editor has none of the AfterSceneLoad attachments
        ApplyRow(df, br, row);
        if (go.GetComponent<ArenaLimits>() == null && go.GetComponent<CharacterController>() != null) go.AddComponent<ArenaLimits>();
        go.SetActive(true);
        RagdollDeath.PrebuildOne(go);   // KILLBUG v1: a Resources clone has no rig yet: build it now (rest pose) instead of at death
        ZoneHitch.Lap("spawn.apply+activate", ref zh); var lv = new Live { row = row,  /* ZONEHITCH v1 */
         creature = cid, gen = gen, go = go, df = df, brain = br, home = home, homeYaw = yaw, zoneId = zone.id };
        { var cr = ChaseTable.Enabled ? ChaseTable.Row(cid) : null; if (cr != null) { lv.chase = go.AddComponent<ChaseAgent>(); lv.chase.Init(df, player, home, row.leash, cr, false); } }   // CHASE v1
        ZoneHitch.Lap("spawn.chase", ref zh); live[row.id] = lv;  /* ZONEHITCH v1 */
        if (GameLoop.Enemy == null) GameLoop.Enemy = df;
    }

    static void ApplyRow(DuelFighter e, AIBrain br, ZSpawn row)
    {
        float hm = row.hpMul, dm = row.damageMul; int diff = row.difficulty;
        if (row.ladderRow.Length > 0) { var rows = GameLoop.Opponents().rows; if (rows != null) foreach (var r in rows) if (r != null && r.id == row.ladderRow) { hm = r.hpMul; dm = r.damageMul; diff = r.difficulty; } }   // same numbers opponents.json already holds
        if (Mathf.Abs(hm - 1f) > 0.0005f) { e.maxHealth *= hm; var hp = e.GetComponent<Health>(); if (hp != null) hp.max = e.maxHealth; }
        e.health = e.maxHealth; var h2 = e.GetComponent<Health>(); if (h2 != null) h2.current = e.maxHealth;
        if (Mathf.Abs(dm - 1f) > 0.0005f) e.damageScale *= dm;
        if (diff > 0 && br != null) { var dr = GameLoop.DifficultyRowFor(diff); if (dr != null) br.diff = dr; }
    }

    // ------------------------------------------------------------------ fights begin when you approach
    // MULTI v1 (INT107, Dom 08:19): with engage.maxEngaged > 1 the player's TARGET (hits, lock camera, HUD bar and name, finisher victim) is the nearest engaged enemy; it switches only when another is nearer
    // by engage.retargetMarginM, or the target is gone / dead. No lock-switch input exists in CameraRig / PlayerBrain, so none is invented. With one engaged enemy this is the old behaviour exactly.
    bool TargetOk(DuelFighter f) { if (f == null || f.Dead) return false; var q = Find(f); return q != null && Fighting(q); }   // TWOFIGHT v1: Fighting() = engaged, alive and not walking home
    bool OthersEngaged(DuelFighter v) { foreach (var q in live.Values) if (q.engaged && !q.killed && q.df != null && !q.df.Dead && q.df != v) return true; return false; }
    public static bool FinisherBlocked(DuelFighter victim) { return Inst != null && !Inst.T.engage.finisherWithOthers && Inst.OthersEngaged(victim); }   // RTFinisher.TryBegin: plain death while another enemy is still on you
    // TWOFIGHT v1 (laneTWOFIGHT): the two-enemy rules. Every switch is a row of zones.json twoFight; zones off = none of this runs.
    /// <summary>Creature id of the player's target. RTFinisher.SideOf and FinisherSides.IdOf read it: Roster.EnemyId is the scene's default enemy (Pitborn), so Sobek was "the player's side" and no finisher could start on or by him.</summary>
    public static string FoeId = "";
    /// <summary>Engaged, alive and not walking home: an enemy that gave up (ChaseAgent Pause / Home, its own opponent already null) is neither a target nor a combatant.</summary>
    bool Fighting(Live q) { return q.engaged && !q.killed && !(T.twoFight.skipGivingUp && q.chase != null && q.chase.GivingUpNow); }
    bool OthersEngagedNot(DuelFighter v, DuelFighter killer) { foreach (var q in live.Values) if (q.engaged && !q.killed && q.df != null && !q.df.Dead && q.df != v && q.df != killer) return true; return false; }
    /// <summary>RTFinisher.TryBegin: for a finisher ON the player, the enemy that lands the blow is not "another enemy"; for an enemy victim the existing rule.</summary>
    public static bool FinisherBlocked(DuelFighter killer, DuelFighter victim)
    {
        if (Inst == null) return false;
        if (!Inst.T.twoFight.killerExempt || victim != Inst.player) return FinisherBlocked(victim);
        return !Inst.T.engage.finisherWithOthers && Inst.OthersEngagedNot(victim, killer);
    }
    /// <summary>FOESEP v1: the other fighting enemy beside `self`; null when self is not a fighting live enemy or it is alone. No allocation (struct enumerator over 2-3 entries).</summary>
    public static DuelFighter PeerOf(DuelFighter self)
    {
        if (Inst == null || !Inst.on || self == null) return null;
        bool mine = false; DuelFighter other = null;
        foreach (var q in Inst.live.Values) { if (q.df == null || q.df.Dead) continue; if (q.df == self) { mine = Inst.Fighting(q); continue; } if (other == null && Inst.Fighting(q)) other = q.df; }
        return mine ? other : null;
    }
    /// <summary>The second enemy that is fighting (not `target`), for the HUD; null when there is none.</summary>
    public static DuelFighter SecondFoe(DuelFighter target) { if (Inst == null || !Inst.on) return null; foreach (var q in Inst.live.Values) if (q.df != null && q.df != target && !q.df.Dead && Inst.Fighting(q)) return q.df; return null; }
    /// <summary>hub.json safe.monsters.chasers.atThreshold = stopAndGoHome: while the player is in a safe zone every engaged chaser drops aggro and walks home (nothing in code read that row; they stood at the door and kept the HUD bar and the regen lock).</summary>
    void SafeZoneGiveUp()
    {
        if (zone == null || !(zone.safe || HubSafe.PlayerInside())) return;
        foreach (var l in live.Values) if (l.engaged && !l.killed && l.df != null && !l.df.Dead && l.chase != null && l.chase.GiveUp()) Debug.Log("[Zones] safe zone: " + l.row.id + " drops aggro and walks home");
    }
    void Retarget(bool force)
    {
        Live cur = TargetOk(player.opponent) ? Find(player.opponent) : null, best = null; float bd = float.MaxValue, cd = cur != null ? Flat(player.transform.position, cur.df.transform.position) : float.MaxValue;
        foreach (var q in live.Values) { if (q.df == null || q.df.Dead || !Fighting(q)) continue; float d = Flat(player.transform.position, q.df.transform.position);   /* TWOFIGHT v1 */ if (d < bd) { bd = d; best = q; } }
        if (best == null) return;
        if (cur != null && best != cur && bd + T.engage.retargetMarginM > cd) best = cur;
        if (best.df == player.opponent && !force) return;
        player.opponent = best.df; GameLoop.Enemy = best.df; FoeId = best.creature;   // TWOFIGHT v1
        var rg = FindFirstObjectByType<CameraRig>(); if (rg != null) { rg.enemy = best.df.transform; rg.enemyHp = best.df.GetComponent<Health>(); }
        var orw = GameLoop.Opponents().rows; if (best.row.ladderRow.Length > 0 && orw != null) for (int i = 0; i < orw.Length; i++) if (orw[i] != null && orw[i].id == best.row.ladderRow) { GameLoop.OpponentIndex = i; break; }
    }
    int EngagedCount() { int n = 0; foreach (var l in live.Values) if (l.engaged && !l.killed && l.df != null && !l.df.Dead) n++; return n; }
    void Engagement()
    {
        if (phase != Phase.Play) return;
        if (T.twoFight.noEngageInFinisher && RTFinisher.Busy) return;   // TWOFIGHT v1: nobody new engages (and the target is not re-picked) while a finisher scene owns both fighters: Engage() during the scene set player.opponent, then RTFinisher.Release nulled it
        if (T.twoFight.healTarget && EngagedCount() > 0 && !TargetOk(player.opponent)) Retarget(true);   // TWOFIGHT v1: a target that is missing, dead or gave up while another enemy is engaged (Release / ChaseAgent clear player.opponent; the old retarget only ran with 2+ engaged)
        if (T.twoFight.safeZoneGiveUp) SafeZoneGiveUp();   // TWOFIGHT v1
        Vector3 pp = player.transform.position;
        foreach (var l in live.Values)
        {
            if (l.df == null || l.killed) continue;
            if (l.engaged)
            {
                if (l.chase != null) { if (l.chase.Done) { if (l.chase.foreign) Vanish(l); else Disengage(l); } continue; }   // CHASE v1: gives up by gap / time, walks home, then resets
                if (Flat(pp, l.home) > l.row.leash) Disengage(l);   // leash: it forgets you, resets to full and walks home (teleport)
                continue;
            }
            if (Time.unscaledTime < arriveGraceUntil || EngagedCount() >= T.engage.maxEngaged) continue;
            if (Flat(pp, l.df.transform.position) <= l.row.aggro) Engage(l);
        }
        if (T.engage.maxEngaged > 1 && EngagedCount() > 1) Retarget(false);   // MULTI v1 (Poll)
    }
    void Engage(Live l)
    {
        l.engaged = true; l.df.opponent = player; if (!TargetOk(player.opponent)) player.opponent = l.df;   // MULTI v1 (Engage): both brains attack the player; a later engage does not steal the target
        if (l.chase != null) l.chase.Restart();   // CHASE v1
        var rig = FindFirstObjectByType<CameraRig>(); if (rig != null) { rig.enemy = l.df.transform; rig.enemyHp = l.df.GetComponent<Health>(); }   // CHASE v1 (ZONES gap): the lock camera followed the inactive scene template
        if (l.brain != null) l.brain.enabled = true;
        GameLoop.Enemy = l.df; GameLoop.Player = player; combatTicks = 0;
        var orows = GameLoop.Opponents().rows;   // HUD name and title come from the opponents.json row the spawn names (GameLoop.EnemyName / CurrentRow)
        if (l.row.ladderRow.Length > 0 && orows != null) for (int i = 0; i < orows.Length; i++) if (orows[i] != null && orows[i].id == l.row.ladderRow) { GameLoop.OpponentIndex = i; break; }
        Retarget(true);   // MULTI v1 (Engage)
        Debug.Log("[Zones] engage " + l.row.id + " (" + l.creature + ", life " + l.gen + ")");
    }
    void Disengage(Live l)
    {
        l.engaged = false; if (l.brain != null) l.brain.enabled = false;
        if (player.opponent == l.df) player.opponent = null; l.df.opponent = null; Retarget(true);   // MULTI v1 (Disengage)
        if (l.chase == null || l.chase.row.resetOnHome) { l.df.health = l.df.maxHealth; var hp = l.df.GetComponent<Health>(); if (hp != null) hp.current = hp.max; }   // CHASE v1: resetOnHome
        Place(l.df, l.home, l.homeYaw); if (l.chase != null) l.chase.Restart(); Debug.Log("[Zones] leash: " + l.row.id + " reset");
    }

    // ---- CHASE v1: followers through a gate ----
    bool AllEngagedFollow()
    {
        if (!ChaseTable.Enabled) return false;
        foreach (var l in live.Values)
        {
            if (!l.engaged || l.killed || l.df == null || l.df.Dead) continue;
            if (l.chase == null || !l.chase.row.followThroughGate || l.chase.GivingUpNow || Flat(player.transform.position, l.df.transform.position) > l.chase.row.followGateMaxGap) return false;
        }
        return true;
    }
    List<Live> TakeFollowers()
    {
        var r = new List<Live>(); if (!ChaseTable.Enabled) return r;
        foreach (var l in live.Values) if (l.engaged && !l.killed && l.df != null && !l.df.Dead && l.chase != null && l.chase.row.followThroughGate) r.Add(l);
        foreach (var l in r) live.Remove(l.row.id);   // DespawnAll only destroys what is still in `live`
        return r;
    }
    void PlaceFollowers(List<Live> fol, Vector3 playerPos, float yaw)
    {
        Vector3 back = Quaternion.Euler(0f, yaw, 0f) * Vector3.back;
        foreach (var l in fol)
        {
            Vector3 p = playerPos + back * l.chase.row.followArriveBehindM;
            bool backHome = l.zoneId == zone.id;   // returning to the zone it came from: it is a home creature again (LookSpawns then sees it live, no duplicate)
            Place(l.df, p, yaw); if (!backHome) l.home = p;   // foreign home = where it came in (it walks back there and vanishes)
            l.chase.foreign = !backHome; l.chase.home = l.home; l.chase.Restart(); l.df.opponent = player; player.opponent = l.df;
            live[backHome ? l.row.id : l.row.id + "@" + zone.id] = l;
        }
    }
    void Vanish(Live l) { l.engaged = false; if (player.opponent == l.df) player.opponent = null; Retarget(true);   /* MULTI v1 (Vanish) */ if (l.go != null) Destroy(l.go); }   // CHASE v1: never edits `live` (callers enumerate it; Corpses() drops the dead entry); its record in the origin zone is untouched, so it is alive at home when you return

    void KillWatch()
    {
        foreach (var l in live.Values)
        {
            if (l.killed || l.df == null || !l.df.Dead) continue;
            l.killed = true; l.engaged = false; l.deadAt = Time.unscaledTime; if (player.opponent == l.df) Retarget(true);   // MULTI v1 (Kill)
            long now = ZoneCore.NowMs();
            var n = ZoneCore.Kill(ZoneSave.Rec(l.zoneId, l.row.id), l.zoneId, l.row.id, l.creature, now, l.row.respawnMinS, l.row.respawnMaxS); ZoneSave.Put(n);
            foreach (var q in T.zones) if (q != null) foreach (var g in q.gates) if (g != null && g.sticky && g.unlockKilledSpawn == l.row.id) ZoneSave.UnlockGate(g.id);
            Debug.Log("[Zones] kill " + l.row.id + ": back in " + ((n.respawnAtMs - now) / 1000L) + " s (life " + n.generation + ")");
            SaveNow(); UpdateRift(true);
        }
    }
    void Corpses()
    {
        List<string> rm = null;
        foreach (var kv in live)
        {
            var l = kv.Value; if (l.go == null) { (rm ?? (rm = new List<string>())).Add(kv.Key); continue; }
            if (l.killed && Time.unscaledTime - l.deadAt > T.engage.corpseSeconds && !RTFinisher.Busy) { Destroy(l.go); (rm ?? (rm = new List<string>())).Add(kv.Key); }
        }
        if (rm != null) foreach (var k in rm) live.Remove(k);
    }
    void DespawnAll() { foreach (var l in live.Values) if (l.go != null) Destroy(l.go); live.Clear(); }

    // ------------------------------------------------------------------ hp (counted in SIM ticks, so it is the same at 30 / 60 / 144 fps)
    void SetHp(float v) { player.health = Mathf.Clamp(v, 1f, player.maxHealth); var h = player.GetComponent<Health>(); if (h != null) h.current = player.health; }
    void TickRegen()
    {
        int t = Duel.TickCount, d = t - lastTick; lastTick = t; if (d <= 0) return;
        if (EngagedCount() > 0) { combatTicks = 0; return; }
        combatTicks += d; var H = T.hp; if (combatTicks < H.regenDelaySeconds * 60f) return;
        float ceil = zone.safe ? H.regenCeilSafeFrac : H.regenCeilDangerFrac, rate = H.regenPerSecondFrac;
        var hs = ZoneData.Service(zone, "hearth");
        if (hs != null) { var sp = ZoneData.Spot(zone, hs.spot); if (sp != null && sp.pos.Length >= 2 && Flat(player.transform.position, SpotPos(sp, player.transform.position)) <= H.hearthRadius) { ceil = 1f; rate = H.hearthRegenPerSecondFrac; } }
        float n = ZoneCore.RegenStep(player.health, player.maxHealth, d, rate, ceil); if (n != player.health) { player.health = n; var h = player.GetComponent<Health>(); if (h != null) h.current = n; }
    }

    // ------------------------------------------------------------------ player down: wake at the hearth (existing scene reload, then Rebind places us)
    void BeginDown() { if (phase == Phase.Down) return; phase = Phase.Down; phaseT = 0f; downHandled = false; coverColor = Color.black; RespawnBegin(); /* REVIVE v1 */ }
    void DownStep(float dt)
    {
        if (RespawnStep(dt)) return;   // REVIVE v1: in-world respawn (no reload); false = off or gave up, the old path below runs
        phaseT += dt; float hold = Mathf.Max(0.5f, T.hp.deathHoldSeconds); cover = Mathf.Clamp01((phaseT - hold * 0.4f) / (hold * 0.6f));
        if (phaseT < hold || downHandled) return; downHandled = true;
        var hz = ZoneData.HearthZone(); var hs = hz != null ? ZoneData.Service(hz, "hearth") : null;
        if (hz != null) ZoneSave.SetPlayer(hz.id, hs != null ? hs.spot : T.start.spot, T.hp.onDeath == "hearth_full" ? 1f : Mathf.Max(0.1f, player.health / Mathf.Max(1f, player.maxHealth)));
        ZoneSave.Flush(true); GameLoop.Retry();   // loot is already saved by LootSave; every monster timer stays in the zone file
    }

    // ------------------------------------------------------------------ REVIVE v1 (laneSEAMLESS): Joan's in-world respawn (zones.json hp.respawn). No fade, no reload, no card: a dark dip, then she stands up.
    bool reviveAsked, reviveGaveUp; float reviveAskedAt, foeResetAt = -1f; string reviveWhere = ""; readonly List<string> foeKeys = new List<string>();
    ZRespawn RespCfg { get { return T != null && T.hp != null ? T.hp.respawn : null; } }
    void RespawnBegin()
    {
        reviveAsked = false; reviveGaveUp = false; foeResetAt = -1f; foeKeys.Clear();
        var R = RespCfg; if (R == null || !R.enabled || player == null) return;
        foreach (var kv in live)   // whoever was fighting her stops (no new swing at the corpse, no re-engage the moment she stands up); the home ones go back after foeResetDelayS
        {
            var l = kv.Value; if (l.df == null || l.killed || !l.engaged || l.df.Dead) continue;
            foeKeys.Add(kv.Key); l.engaged = false; if (l.brain != null) l.brain.enabled = false; l.df.opponent = null; l.df.moveDir = Vector3.zero; if (l.chase != null) l.chase.GiveUp();
        }
        player.opponent = null;
        if (R.foeReset == "spawn" && foeKeys.Count > 0) foeResetAt = Time.unscaledTime + Mathf.Max(R.delayS, R.foeResetDelayS);
        Debug.Log("[Zones] player down in " + (zone != null ? zone.id : "?") + ": in-world respawn in " + R.delayS.ToString("F1") + " s (rule " + R.rule + "), " + foeKeys.Count + " creature(s) to reset" + (foeResetAt >= 0f ? " in " + Mathf.Max(R.delayS, R.foeResetDelayS).ToString("F1") + " s" : ""));
    }
    void RespawnGiveUp(string why) { reviveGaveUp = true; reviveAsked = false; Debug.LogWarning("[Zones] revive: " + why + ": falling back to the scene reload (old death path)"); }
    /// <summary>True = the in-world respawn owns this frame of the Down phase. False = respawn off / gave up: the old DownStep (black, GameLoop.Retry) runs.</summary>
    bool RespawnStep(float dt)
    {
        var R = RespCfg; if (R == null || !R.enabled || reviveGaveUp) return false;
        float hold = Mathf.Max(0.1f, R.delayS), cs = Mathf.Max(0.05f, R.coverS);   // hc:ok divide-by-zero guards; the values are hp.respawn.delayS / coverS
        if (!reviveAsked)
        {
            cover = Mathf.Clamp01((phaseT - (hold - cs)) / cs) * Mathf.Clamp01(R.coverMax);   // the dip: dark only for the last coverS before she stands up
            if (phaseT < hold) return true;
            if (RTFinisher.Busy || Duel.Frozen) { if (phaseT < hold + R.fallbackAfterS) return true; RespawnGiveUp("a finisher scene is still running after " + R.fallbackAfterS.ToString("F0") + " s"); return false; }
            string why; if (!ReviveTo(R, out why)) { RespawnGiveUp(why); return false; }
            reviveAsked = true; reviveAskedAt = phaseT; return true;
        }
        if (!player.Dead) { RespawnDone(R); return true; }
        if (player.ReviveFailed) { RespawnGiveUp("the fighter could not stand up (see the [Revive] warning)"); return false; }
        if (phaseT > reviveAskedAt + R.fallbackAfterS) { RespawnGiveUp("no sim tick stood her up within " + R.fallbackAfterS.ToString("F0") + " s"); return false; }
        cover = Mathf.Clamp01(R.coverMax); return true;
    }
    bool ReviveTo(ZRespawn R, out string why)
    {
        why = ""; string spotId = "";
        if (R.rule == "hearth") { var hs = ZoneData.Service(zone, "hearth"); if (hs != null) spotId = hs.spot; }
        if (spotId.Length == 0 && R.arrive != null) foreach (var a in R.arrive) if (a != null && a.zone == zone.id) { spotId = a.spot; break; }
        var sp = spotId.Length > 0 ? ZoneData.Spot(zone, spotId) : null;
        if (sp == null || sp.pos.Length < 2) { why = "zone " + zone.id + " has no respawn spot (rule " + R.rule + ", row hp.respawn.arrive)"; return false; }
        var rr = player.GetComponent<RagdollRig>(); if (rr != null && rr.Converted && !rr.CanRestore) { why = "the ragdoll cannot be put back (" + (rr.RestoreWhy.Length > 0 ? rr.RestoreWhy : "no snapshot or a finisher cut the body") + ")"; return false; }
        Vector3 np = SpotPos(sp, player.transform.position);
        Place(player, np, sp.yaw); ZoneGround.Begin(player, np); Physics.SyncTransforms();
        player.RequestRevive(R.hpFrac); reviveWhere = zone.id + " / " + spotId; return true;
    }
    void RespawnDone(ZRespawn R)
    {
        reviveAsked = false; phase = Phase.CoverOut; phaseT = 0f; coverColor = Color.black; downHandled = false;
        arriveGraceUntil = Time.unscaledTime + T.engage.graceAfterArriveSeconds; player.opponent = null; combatTicks = 0; lastTick = Duel.TickCount; crossGate = null;
        Debug.Log("[Zones] revive: player is up at " + reviveWhere + ", hp " + player.health.ToString("F0") + "/" + player.maxHealth.ToString("F0") + ", no scene reload");
        SaveNow();
    }
    void FoeResetStep()
    {
        if (foeResetAt < 0f || Time.unscaledTime < foeResetAt) return;
        foeResetAt = -1f; int n = 0; string ids = "";
        foreach (var key in foeKeys)
        {
            Live l; if (!live.TryGetValue(key, out l) || l.killed) continue;
            ids += (ids.Length > 0 ? ", " : "") + key; Vanish(l); live.Remove(key); n++;   // gone; its record is untouched = alive at home, LookSpawns brings it back at its spawn spot with full hp as soon as nobody sees the spot
        }
        foeKeys.Clear();
        if (n > 0) { Debug.Log("[Zones] reset: " + n + " creature(s) back to their spawn at full hp (" + ids + ")"); if (phase == Phase.Play) LookSpawns(false); }
    }

    // ------------------------------------------------------------------ gates
    bool GateOpen(ZGate g)
    {
        if (g.endScreen.Length > 0) return true;   // ENDALPHA v1: the end gate is always open, it shows a screen instead of crossing
        if (g.unlockKilledSpawn.Length == 0) return true;
        if (g.sticky) return ZoneSave.GateUnlocked(g.id);
        ZZone uz; var s = ZoneData.FindSpawn(g.unlockKilledSpawn, out uz); if (s == null) return false;
        return !ZoneCore.Look(ZoneSave.Rec(uz.id, s.id), ZoneCore.NowMs()).alive;
    }
    void UpdateRift(bool force)
    {
        if (rift == null || zone == null) return; bool want = false;
        foreach (var g in zone.gates) if (g != null && g.visual == "GatewayRift" && GateOpen(g)) want = true;
        if (!force && want == riftOpen) return; riftOpen = want;
        rift.SetMode(want ? GatewayRift.Mode.Rift : GatewayRift.Mode.Abyss);
    }
    // ENDALPHA v1
    void BeginEndScreen(ZGate g)
    {
        var r = ZoneEndScreen.Find(T.endScreens, g.endScreen);
        if (r == null) { Debug.LogWarning("[Zones] gate " + g.id + ": endScreen '" + g.endScreen + "' not in zones.json endScreens"); return; }
        if (r.onlyWhenNoEnemyAlive && EngagedCount() > 0) return;
        ZoneEndScreen.Begin(r); Debug.Log("[Zones] end screen " + r.id);
    }
    void EndScreenStep()
    {
        var res = ZoneEndScreen.Poll(); if (res == ZoneEndScreen.Result.None) return;
        var r = ZoneEndScreen.Row; ZoneEndScreen.End();
        var to = res == ZoneEndScreen.Result.Camp ? ZoneData.Zone(r.campZone) : null;
        if (to != null) { crossGate = new ZGate { id = "endscreen_camp", toZone = r.campZone, toSpot = r.campSpot }; phase = Phase.CoverIn; phaseT = 0f; coverColor = ColorOf(T.crossing.cover.color); StartLoad(to); return; }
        Vector3 p = player.transform.position; p.z -= r.nudgeBackM; Place(player, p, player.transform.eulerAngles.y); ZoneGround.Begin(player, p);   // Continue (or camp zone missing): step back out of the gate radius, never stuck
    }
    void CheckGates()
    {
        if (zone == null) return; if (!T.crossing.allowWhileEngaged && !T.crossing.freePassage /* ALPHAFLOW v1 */ && EngagedCount() > 0 && !AllEngagedFollow()) return;   // CHASE v1
        foreach (var g in zone.gates)
        {
            if (g == null || !GateOpen(g)) continue; var sp = ZoneData.Spot(zone, g.spot); if (sp == null || sp.pos.Length < 2) continue;
            if (ZoneSpan.Handles(g.id)) continue;   // ZONE2DUP v1: walked through the doorway, not flashed
            if (Flat(player.transform.position, SpotPos(sp, player.transform.position)) > g.radius) continue;
            if (g.endScreen.Length > 0) { if (!ZoneEndScreen.Active) BeginEndScreen(g); return; }   // ENDALPHA v1
            phase = Phase.CoverIn; phaseT = 0f; crossGate = g; coverColor = ColorOf(T.crossing.cover.color); StartLoad(ZoneData.Zone(g.toZone)); Debug.Log("[Zones] crossing " + g.id); return;
        }
    }

    // additive zone scenes: loaded in the background while the player walks, held at 90 % until the flash
    void Preload()
    {
        if (zone == null || phase != Phase.Play) return;
        foreach (var g in zone.gates)
        {
            if (g == null || !GateOpen(g)) continue; var sp = ZoneData.Spot(zone, g.spot); if (sp == null || sp.pos.Length < 2) continue;
            if (Flat(player.transform.position, SpotPos(sp, player.transform.position)) <= T.crossing.preloadDistance) StartLoad(ZoneData.Zone(g.toZone));
        }
    }
    void StartLoad(ZZone z)
    {
        if (z == null || z.mode != "additive" || z.scene.Length == 0 || loading.ContainsKey(z.id)) return;
        var sc = SceneManager.GetSceneByName(z.scene); if (sc.IsValid() && sc.isLoaded) return;
        var op = SceneManager.LoadSceneAsync(z.scene, LoadSceneMode.Additive); if (op == null) { Warn(z.id, "[Zones] scene '" + z.scene + "' cannot be loaded (not in the build?)"); return; }
        op.allowSceneActivation = false; loading[z.id] = op;
    }
    bool DestReady(ZZone z)
    {
        if (z.mode != "additive" || z.scene.Length == 0) return true;
        var sc = SceneManager.GetSceneByName(z.scene); if (sc.IsValid() && sc.isLoaded) return true;
        AsyncOperation op; if (!loading.TryGetValue(z.id, out op)) { StartLoad(z); return false; }
        if (op.progress >= 0.9f) op.allowSceneActivation = true;
        return op.isDone;
    }

    void HoldStep(float dt)
    {
        holdT += dt; var C = T.crossing.cover; var to = ZoneData.Zone(crossGate.toZone);
        if (to != null && holdT >= C.holdMinSeconds && DestReady(to)) { DoSwap(crossGate, to); phase = Phase.CoverOut; phaseT = 0f; return; }
        if (holdT >= C.holdMaxSeconds || to == null) { Debug.LogWarning("[Zones] crossing gave up: destination not ready"); phase = Phase.CoverOut; phaseT = 0f; }
    }

    // the swap itself, under the flash: no camera cut, the player object is never destroyed (so wounds, stamina, worn gear, loot all stay)
    void DoSwap(ZGate g, ZZone to)
    {
        float frac = player.maxHealth > 0f ? player.health / player.maxHealth : 1f;
        frac = ZoneCore.CarryHp(T.hp.onCross, frac, T.hp.crossFloorFrac); if (frac * player.maxHealth > player.health) SetHp(frac * player.maxHealth);
        Vector3 old = player.transform.position; float oldYaw = player.transform.eulerAngles.y;
        var fol = TakeFollowers(); DespawnAll(); player.opponent = null;   // CHASE v1: followers are kept out of the despawn
        var sp = ZoneData.Spot(to, g.toSpot); EnterZone(to, g.toSpot, true);
        Vector3 np = SpotPos(sp, old); float yaw = sp != null ? sp.yaw : oldYaw;
        Place(player, np, yaw);
        PlaceFollowers(fol, np, yaw);   // CHASE v1
        var cam = Camera.main;
        if (cam != null) { cam.transform.position += np - old; cam.transform.RotateAround(np, Vector3.up, Mathf.DeltaAngle(oldYaw, yaw)); }   // the camera moves with the player: same offset, same turn
        combatTicks = 0; lastTick = Duel.TickCount; LookSpawns(true);
        ZoneSave.SetPlayer(to.id, g.toSpot, player.health / Mathf.Max(1f, player.maxHealth)); ZoneSave.Flush(true);
        Debug.Log("[Zones] arrived " + to.id + " / " + g.toSpot + " hp " + (player.health / Mathf.Max(1f, player.maxHealth)).ToString("F2"));
    }

    void SaveNow()
    {
        if (!on || zone == null || player == null) return;
        ZoneSave.SetPlayer(zone.id, lastSpot, player.health / Mathf.Max(1f, player.maxHealth)); ZoneSave.Flush(true);
    }

    // ------------------------------------------------------------------ helpers
    static float Flat(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return (a - b).magnitude; }
    static Color ColorOf(float[] c) { return c != null && c.Length >= 3 ? new Color(c[0], c[1], c[2], 1f) : Color.white; }
    void Warn(string key, string msg) { if (warned.Add(key)) Debug.LogWarning(msg); }
    /// <summary>[x, z] spot to a world point; ground height from a raycast down from just above the reference point, else the reference height.</summary>
    static Vector3 SpotPos(ZSpot s, Vector3 refPos)
    {
        if (s == null || s.pos == null || s.pos.Length < 2) return refPos;
        Vector3 o = new Vector3(s.pos[0], refPos.y + 1.5f, s.pos[1]); RaycastHit hit;
        if (Physics.Raycast(o, Vector3.down, out hit, 8f, ~0, QueryTriggerInteraction.Ignore)) return new Vector3(s.pos[0], hit.point.y, s.pos[1]);
        return new Vector3(s.pos[0], refPos.y, s.pos[1]);
    }
    /// <summary>ROBUST v1: a fighter that keeps falling at the same spot is put on the zone's last arrive spot (data: zones.json spots). False = zones off / not the player / no spot.</summary>
    public static bool RescuePlayer(DuelFighter f)
    {
        var z = Inst; if (z == null || !z.on || z.zone == null || f != z.player) return false;
        var sp = ZoneData.Spot(z.zone, z.lastSpot.Length > 0 ? z.lastSpot : z.T.start.spot); if (sp == null || sp.pos == null || sp.pos.Length < 2) return false;
        Place(f, SpotPos(sp, f.transform.position), sp.yaw); return true;
    }
    static void Place(DuelFighter f, Vector3 p, float yaw)
    {
        var cc = f.GetComponent<CharacterController>(); bool en = cc != null && cc.enabled; if (en) cc.enabled = false;
        f.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, yaw, 0f)); if (en) cc.enabled = true; Physics.SyncTransforms();
    }

    void OnGUI()
    {
        ZoneEndScreen.Draw();   // ENDALPHA v1
        if (!on || cover <= 0.002f) return;
        GUI.depth = -10000; var c = coverColor; c.a = cover;
        GameLoop.Fill(new Rect(0f, 0f, Screen.width, Screen.height), c);
    }
}

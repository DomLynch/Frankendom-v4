using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// GEAR DATA LAYER (lane GEAR v79). Every number and name lives in Assets/Data/gear.json (copy in StreamingAssets for the player).
// Lookup order (same as AttackClipTable): Resources/gear, Editor Data/gear.json, StreamingAssets/gear.json, Application.dataPath/Data/gear.json.
// A missing / unparsable file or "enabled": false = no gear at all (every hook returns the neutral value, the fight plays exactly as before).
//  slots[]      the 8 slots: head / chest / arms / legs (armour, skinned on the shared 106-joint rig), mainHand / offHand / remnant1 / remnant2 (data only for now: visual false).
//               An item's "slot" is a slot id or a slot "group" (remnant -> remnant1 or remnant2).
//  zones[]      body zones (bit i of a ulong mask). boneZones[] maps a bone name to a zone (first matching row wins; names are compared without the "mixamorig:" prefix, lower case).
//  items[]      id, slot, glb (project path) | resource | layer (a layer mesh that already sits on the wearer, e.g. Layer_Helmet) + part (one node of a multi-mesh glb),
//               coversZones[] (body zones hidden under it), hidesLayers[] (wearer layer meshes it hides), stats{armor,poise,weight}, dropFrom[] (character ids), dropChance.
//  GEAR2 v79-80 (lane GEAR2): items[].glbMirror = a second glb for the other side (pauldrons / vambraces / greaves come as _R / _L): ONE slot holds the pair, both pieces are equipped and removed together
//               (partMirror = the node filter for the mirror glb, "" = same as part). items[].materials {matName: {smoothness, metallic, normalScale, sheen}} = per-item material rows applied AFTER the
//               HDRP/Lit swap (GearMaterials.cs); the key "*" = every material of the item that has no row of its own. materials.byName {matName: row} = the same rows for every item / wearer layer mesh
//               (Joan Layer_* materials Steel / Leather / Heraldry). Free-key objects are read by GearJson.cs (JsonUtility cannot); -1 or a missing key = leave the value alone.
//  Loadouts     are the "loadout": ["item id", ...] arrays of the rows in roster.json (read here, Roster.cs is untouched).
public static class GearTable
{
    [Serializable] public class SlotDef { public string id = "", group = ""; public bool visual = true; }
    [Serializable] public class BoneZone { public string zone = "", prefix = "", suffix = ""; public string[] has = new string[0]; }
    [Serializable] public class Stats { public float armor, poise, weight; }
    [Serializable]
    public class Item
    {
        public string id = "", name = "", slot = "", glb = "", resource = "", layer = "", part = "", notes = "";
        public string glbMirror = "", partMirror = "";   // GEAR2: the other side's glb (same slot, equipped as a pair)
        [NonSerialized] public Dictionary<string, MatRow> materialRows = new Dictionary<string, MatRow>(StringComparer.OrdinalIgnoreCase);   // GEAR2: items[].materials, read by GearJson
        public string[] coversZones = new string[0], hidesLayers = new string[0], dropFrom = new string[0];
        public float dropChance = 0f;
        public Stats stats = new Stats();
        public WeaponDef weapon = new WeaponDef();   // WEAPONSWAP v88: set (weapon.id) on mainHand weapon items
        public ShieldDef shield = new ShieldDef();   // SHIELD v1: set (shield.id) on offHand shield items (ShieldMount.cs)
        public bool HasVisualSource { get { return layer.Length > 0 || glb.Length > 0 || resource.Length > 0; } }
        public bool IsPair { get { return glbMirror.Length > 0; } }
    }
    // WEAPONSWAP v88: items[].weapon = the weapon block of a mainHand item (WeaponSwap.cs): the numbers are multipliers on the wearer's DEFAULT weapon (the sword = 1 / 1 / 1 / 1)
    [Serializable]
    public class WeaponDef
    {
        public string id = "";                         // weapon id ("" = not a weapon item). WeaponGrip.cs / GripPose.cs rows carry the same id in "weapon"
        public float damageMul = 1f, speedMul = 1f, reachMul = 1f, postureMul = 1f;   // damage dealt, move speed (0.75 = every windup / active / recovery 1.33x as many ticks), hit reach, posture put on the defender
        public bool twoHanded = false;                 // left-arm IK (TwoHandIK.cs) onto offHandNode
        public string cueWeapon = "";                  // cues.json weapon key while it is held ("" = the roster weapon's)
        public string gripNode = "grip", tipNode = "tip", offHandNode = "grip_off";   // glb nodes: grip (socket origin), tip (swing-trail tip), off-hand target on the shaft axis
        public float ikWeight = 1f, ikBlendSeconds = 0.15f;   // two-hand IK weight (0..1) and the seconds it takes to blend in / out
        public bool unarmed = false;                   // SOBEKCLAWS: no weapon glb, the wearer's own claws are the weapon (WeaponSwap hides the template weapon, the swing trail follows tipBone + tipOffsetM)
        public string tipBone = ""; public float[] tipOffsetM = new float[0];   // unarmed: the claw tip = this bone + this offset in metres in the bone frame (like weapon_grip socketOffset: divided by the bone's lossy scale)
        public void Fix() { if (tipBone == null) tipBone = ""; if (tipOffsetM == null) tipOffsetM = new float[0]; if (id == null) id = ""; if (cueWeapon == null) cueWeapon = ""; if (gripNode == null) gripNode = ""; if (tipNode == null) tipNode = ""; if (offHandNode == null) offHandNode = ""; }
    }
    // SHIELD v1: items[].shield = the shield block of an offHand item (ShieldMount.cs, ShieldHooks). Absent / id "" = not a shield = no rule at all (today's guard).
    // cover rows: while the wearer holds guard side `guard` (guard side names of controls.json guard.sides + centre), a swing whose MoveDef.direction is in `attacks` counts as COVERED even if controls.json guard.match says
    // that guard does not cover it. Covered-by-shield is a block only: never a parry, no parry window, no extra reward. The other guard sides keep exactly today's cover.
    [Serializable] public class ShieldCover { public string guard = ""; public string[] attacks = new string[0]; }
    [Serializable]
    public class ShieldDef
    {
        public string id = "";                          // "" = not a shield item
        public string sideGuard = "";                   // the guard side the shield arm raises (blockPostureMul applies to blocks made with it)
        public ShieldCover[] cover = new ShieldCover[0];  // extra cover rows
        public float blockStaminaMul = 1f;               // x the block stamina cost of a block ONLY the shield covered (the price of the wider cover)
        public float blockPostureMul = 1f;               // x the posture a block with the shield-side guard puts on the wearer (< 1 = the shield takes the blow)
        public string cueEvent = "block_shield";         // cues.json evt played on top of the normal block cue for a shield-covered block ("" = none)
        public void Fix() { if (id == null) id = ""; if (sideGuard == null) sideGuard = ""; if (cover == null) cover = new ShieldCover[0]; if (cueEvent == null) cueEvent = ""; }
    }
    [Serializable]
    public class WeaponIkCfg   // TwoHandIK.cs (gear.json weaponSwap.ik)
    {
        public string arm = "mixamorig:LeftArm", forearm = "mixamorig:LeftForeArm", hand = "mixamorig:LeftHand", middle = "mixamorig:LeftHandMiddle1";
        public float[] curlAxis = new[] { 0f, 0f, 1f };   // finger-curl axis in the finger bone's frame (grip_pose.json fingerAxis)
        public float palmFrac = 0.55f, palmToShaftM = 0.035f, rollDeg = 0f;   // palm point = this share of wrist->middle finger; palm centre to the shaft axis; extra roll of the hand about the shaft
        public bool flipPalm = false, keepAnimatedRotation = false;   // flipPalm = the fingers curl away from the shaft (turn the hand over); keepAnimatedRotation = move the wrist only
        public float reachFadeM = 0.15f;               // the IK weight falls to 0 over this much of out-of-reach distance
        public float[] poleHintLocal = new[] { -0.5f, -1f, -0.3f };   // elbow direction hint in the fighter's frame (left, down, back)
        public float poleHintMix = 0.3f;               // 0 = keep the animated elbow side, 1 = the hint
        public string[] states = new string[0], releaseStates = new string[0];   // Animator states the IK is limited to (empty = all) / released in
        public bool releaseWhenFrozen = true, debugLog = false; public int debugEvery = 30;
        public void Fix() { if (curlAxis == null || curlAxis.Length < 3) curlAxis = new[] { 0f, 0f, 1f }; if (poleHintLocal == null || poleHintLocal.Length < 3) poleHintLocal = new[] { -0.5f, -1f, -0.3f }; if (states == null) states = new string[0]; if (releaseStates == null) releaseStates = new string[0]; }
    }
    [Serializable]
    public class WeaponCfg   // WeaponSwap.cs (gear.json weaponSwap)
    {
        public bool enabled = true;
        public string[] slots = new[] { "mainHand" };   // slot ids whose items are weapons
        public string socket = "WeaponSocket_R";        // where the weapon glb goes (the node WeaponGrip.cs moves)
        public string[] keepChildren = new[] { "TrailTip" };   // socket children that stay visible when the default weapon is hidden
        public string trailNode = "TrailTip";           // re-parented under the weapon's tip node
        public WeaponIkCfg ik = new WeaponIkCfg();
        public void Fix() { if (slots == null) slots = new string[0]; if (socket == null) socket = ""; if (keepChildren == null) keepChildren = new string[0]; if (trailNode == null) trailNode = ""; if (ik == null) ik = new WeaponIkCfg(); ik.Fix(); }
    }
    // one material row (gear.json): -1 = not set, leave the material's own value
    [Serializable]
    public class MatRow
    {
        public float smoothness = -1f, metallic = -1f, normalScale = -1f, sheen = -1f;
        public bool Any { get { return smoothness >= 0f || metallic >= 0f || normalScale >= 0f || sheen >= 0f; } }
        public void Over(MatRow o) { if (o == null) return; if (o.smoothness >= 0f) smoothness = o.smoothness; if (o.metallic >= 0f) metallic = o.metallic; if (o.normalScale >= 0f) normalScale = o.normalScale; if (o.sheen >= 0f) sheen = o.sheen; }
    }

    // stat -> gameplay conversion (all numbers here, none in code)
    [Serializable]
    public class StatRules
    {
        public float armorK = 100f, maxReduction = 0.8f;                  // damage taken = 1 - min(maxReduction, armor / (armor + armorK))
        public float poisePerMeterHit = 10f;                              // +1 poise-meter hit (poise.json meter.hits) per this much poise
        public float posturePerPoise = 0.01f, minPostureMul = 0.5f;       // posture taken = max(min, 1 - poise * posturePerPoise)
        public float weightFree = 6f, regenPerWeight = 0.01f, minRegenMul = 0.5f;   // stamina regen = max(min, 1 - max(0, weight - weightFree) * regenPerWeight)
    }
    [Serializable]
    public class MaskCfg
    {
        public string mode = "triangles";        // "triangles" = drop body triangles inside the covered zones | "alpha" = write the zone bit mask to shaderProp (needs a shader that clips on it) | "off"
        public float vertexThreshold = 0.5f;     // a vertex is inside the zones when this much of its skin weight sits on bones of the zones
        public string triangleRule = "all";      // "all" | "majority" | "any": how many of a triangle's 3 vertices must be inside to drop it
        public string shaderProp = "_GearHideMask";
    }
    [Serializable]
    public class DropCfg
    {
        public bool enabled = true, autoCollect = false, skipIfOwned = true;
        public int maxPerKill = 1, seed = 0; public MaxRow[] maxPerKillBy = new MaxRow[0]; [Serializable] public class MaxRow { public string victim = ""; public int max = 1; } public int MaxFor(string victimId) { if (maxPerKillBy != null && victimId != null) for (int i = 0; i < maxPerKillBy.Length; i++) if (maxPerKillBy[i] != null && maxPerKillBy[i].victim == victimId) return maxPerKillBy[i].max; return maxPerKill; } /* WEAPONSWAP v88: per-victim cap (Pitborn 3) */     // seed 0 = time based, else a fixed roll sequence (balance bots)
        public float spawnHeight = 0.3f, lifetimeSeconds = 0f;   // 0 = the marker stays
    }
    [Serializable]
    public class MaterialCfg   // GearMaterials.cs: runtime-loaded glb gear, glTFast materials -> HDRP/Lit
    {
        public bool convertGltf = true, doubleSided = true;
        public bool styleLayers = true;   // GEAR2: the wearer Layer_* meshes (Steel / Leather / Heraldry) get the rows too (cloned materials); false = leave them exactly as the scene has them
        public string shader = "HDRP/Lit";
        public string[] matchShaderContains = new[] { "glTF", "InternalError" };
        public string[] baseColorTextures = new[] { "baseColorTexture", "_BaseColorMap", "_MainTex" }, normalTextures = new[] { "normalTexture", "_NormalMap", "_BumpMap" }, baseColorProps = new[] { "baseColorFactor", "_BaseColor", "_Color" };
        public float metallic = 0f, smoothness = 0.3f, normalScale = 1f;   // GEAR2: neutral for a material without any row (was 0.85 / 0.5 = a metal pauldron look for everything); metal items get rows
        public string[] shaderFallbacks = new[] { "Universal Render Pipeline/Lit", "Standard" };   // tried in order when `shader` is not in the build (so nothing renders pink)
        public string sheenShader = "Shader Graphs/CharFabric";   // used for rows with sheen > 0 when it exists (same shader as hdrp_char_mats.json fabric); else the material stays HDRP/Lit, no sheen
        public float[] sheenColor = new[] { 0.9f, 0.55f, 0.55f };  // _SheenColor = this x row.sheen
        [NonSerialized] public Dictionary<string, MatRow> byName = new Dictionary<string, MatRow>(StringComparer.OrdinalIgnoreCase);   // GEAR2: materials.byName, read by GearJson
    }
    [Serializable]
    public class Data
    {
        public bool enabled = true, debugLog = false, statsWithoutAsset = true, hideUnequippedLayers = true;
        public float maxMissingBoneFrac = 0.25f;     // a gear mesh with more unmatched bones than this is rejected (no visual, no masking)
        public SlotDef[] slots = new SlotDef[0];
        public string[] zones = new string[0];
        public BoneZone[] boneZones = new BoneZone[0];
        public StatRules stats = new StatRules(); public MaskCfg mask = new MaskCfg(); public DropCfg drop = new DropCfg(); public MaterialCfg materials = new MaterialCfg();
        public WeaponCfg weaponSwap = new WeaponCfg();   // WEAPONSWAP v88
        public Item[] items = new Item[0];
    }
    [Serializable] class RosterRow { public string id = ""; public string[] loadout; }
    [Serializable] class RosterFile { public RosterRow[] characters; }

    static Data data; static bool loaded;
    static readonly Dictionary<string, int> boneZoneCache = new Dictionary<string, int>();
    public static Data Get() { if (!loaded) Load(); return data; }
    public static void Reload() { loaded = false; Load(); }

    public static Item Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var its = Get().items; for (int i = 0; i < its.Length; i++) if (its[i] != null && its[i].id == id) return its[i];
        return null;
    }
    // GEAR2: the effective row for a material of an item (item may be null = wearer layer mesh): per value, item[name] > global[name] > item["*"] > global["*"]; unset values stay -1.
    // matName may carry a Blender ".001" suffix: tried as is, then without it.
    public static MatRow RowFor(Item it, string matName)
    {
        var r = new MatRow(); if (string.IsNullOrEmpty(matName)) matName = "";
        var G = Get().materials;
        string alt = StripNumSuffix(matName);
        r.Over(Lookup(G.byName, "*"));
        if (it != null) r.Over(Lookup(it.materialRows, "*"));
        r.Over(Lookup(G.byName, alt)); r.Over(Lookup(G.byName, matName));
        if (it != null) { r.Over(Lookup(it.materialRows, alt)); r.Over(Lookup(it.materialRows, matName)); }
        return r;
    }
    static MatRow Lookup(Dictionary<string, MatRow> d, string k) { MatRow r; return d != null && k.Length > 0 && d.TryGetValue(k, out r) ? r : null; }
    static string StripNumSuffix(string n)
    {
        int d = n.LastIndexOf('.'); if (d <= 0 || d == n.Length - 1) return n;
        for (int i = d + 1; i < n.Length; i++) if (n[i] < '0' || n[i] > '9') return n;
        return n.Substring(0, d);
    }

    public static SlotDef SlotOf(string slotId)
    {
        var ss = Get().slots; for (int i = 0; i < ss.Length; i++) if (ss[i] != null && ss[i].id == slotId) return ss[i];
        return null;
    }
    // "head" -> head ; "remnant" (a group) -> the slot ids of that group in file order
    public static List<string> SlotsFor(string slotOrGroup)
    {
        var l = new List<string>(); if (string.IsNullOrEmpty(slotOrGroup)) return l; var ss = Get().slots;
        for (int i = 0; i < ss.Length; i++) if (ss[i] != null && ss[i].id == slotOrGroup) { l.Add(ss[i].id); return l; }
        for (int i = 0; i < ss.Length; i++) if (ss[i] != null && ss[i].group == slotOrGroup) l.Add(ss[i].id);
        return l;
    }

    // zone name -> bit index (-1 unknown). Max 64 zones (ulong mask).
    public static int ZoneIndex(string zone)
    {
        var z = Get().zones; for (int i = 0; i < z.Length && i < 64; i++) if (z[i] == zone) return i;
        return -1;
    }
    public static ulong MaskOf(Item it)
    {
        ulong m = 0UL; if (it == null || it.coversZones == null) return m;
        for (int i = 0; i < it.coversZones.Length; i++)
        {
            int b = ZoneIndex(it.coversZones[i]);
            if (b < 0) { Warn("item '" + it.id + "' covers unknown zone '" + it.coversZones[i] + "'"); continue; }
            m |= 1UL << b;
        }
        return m;
    }

    // bone name -> zone index (-1 = no zone). Lower case, without the "mixamorig:" prefix, so "mixamorig:LeftArm" and a re-imported "LeftArm" match the same row.
    public static string Normalize(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        int c = n.LastIndexOf(':'); if (c >= 0) n = n.Substring(c + 1);
        else if (n.StartsWith("mixamorig", StringComparison.OrdinalIgnoreCase)) n = n.Substring(9).TrimStart('_', '.', '-');
        return n.ToLowerInvariant();
    }
    public static int ZoneOfBone(string boneName)
    {
        string k = Normalize(boneName); int z;
        if (boneZoneCache.TryGetValue(k, out z)) return z;
        z = -1; var rows = Get().boneZones;
        for (int i = 0; i < rows.Length && z < 0; i++)
        {
            var r = rows[i]; if (r == null) continue;
            if (r.prefix.Length > 0 && !k.StartsWith(r.prefix, StringComparison.Ordinal)) continue;
            if (r.suffix.Length > 0 && !k.EndsWith(r.suffix, StringComparison.Ordinal)) continue;
            bool ok = true; if (r.has != null) for (int h = 0; h < r.has.Length && ok; h++) if (r.has[h].Length > 0 && k.IndexOf(r.has[h], StringComparison.Ordinal) < 0) ok = false;
            if (!ok) continue;
            z = ZoneIndex(r.zone);
        }
        boneZoneCache[k] = z; return z;
    }

    // starting gear of a character: roster.json row "loadout" (null = the row has none, so the wearer's layers are left alone)
    public static string[] Loadout(string characterId)
    {
        if (string.IsNullOrEmpty(characterId)) return null;
        try
        {
            string json = ReadText("roster"); if (string.IsNullOrEmpty(json)) return null;
            var f = JsonUtility.FromJson<RosterFile>(json); if (f == null || f.characters == null) return null;
            for (int i = 0; i < f.characters.Length; i++)
            {
                var r = f.characters[i]; if (r == null || r.id != characterId) continue;
                return r.loadout != null && r.loadout.Length > 0 ? r.loadout : null;
            }
        }
        catch (Exception e) { Warn("roster loadout read failed: " + e.Message); }
        return null;
    }

    static readonly HashSet<string> warned = new HashSet<string>();
    public static void Warn(string msg) { if (warned.Add(msg)) Debug.LogWarning("[Gear] " + msg); }
    public static void Log(string msg) { if (Get().debugLog) Debug.Log("[Gear] " + msg); }

    static string ReadText(string name)
    {
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>(name); if (ta != null) json = ta.text;
#if UNITY_EDITOR
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/" + name + ".json"); if (File.Exists(p)) json = File.ReadAllText(p); }
#endif
            if (json == null) { string p = Path.Combine(Application.streamingAssetsPath, name + ".json"); if (File.Exists(p)) json = File.ReadAllText(p); }
            if (json == null) { string p = Path.Combine(Application.dataPath, "Data/" + name + ".json"); if (File.Exists(p)) json = File.ReadAllText(p); }
        }
        catch (Exception ex) { Debug.LogWarning("[Gear] read " + name + " failed: " + ex.Message); }
        return json;
    }

    static void Load()
    {
        loaded = true; data = new Data(); boneZoneCache.Clear();
        string json = ReadText("gear");
        if (string.IsNullOrEmpty(json)) { data.enabled = false; Debug.LogWarning("[Gear] gear.json not found: gear system off."); return; }
        try
        {
            var d = JsonUtility.FromJson<Data>(json); if (d == null) { data.enabled = false; return; }
            if (d.slots == null) d.slots = new SlotDef[0]; if (d.zones == null) d.zones = new string[0]; if (d.boneZones == null) d.boneZones = new BoneZone[0];
            if (d.items == null) d.items = new Item[0]; if (d.weaponSwap == null) d.weaponSwap = new WeaponCfg(); d.weaponSwap.Fix(); if (d.drop != null && d.drop.maxPerKillBy == null) d.drop.maxPerKillBy = new DropCfg.MaxRow[0]; /* WEAPONSWAP v88 */ if (d.stats == null) d.stats = new StatRules(); if (d.mask == null) d.mask = new MaskCfg(); if (d.drop == null) d.drop = new DropCfg();
            foreach (var s in d.slots) if (s != null) { if (s.id == null) s.id = ""; if (s.group == null) s.group = ""; }
            foreach (var b in d.boneZones) if (b != null) { if (b.zone == null) b.zone = ""; b.prefix = b.prefix == null ? "" : b.prefix.ToLowerInvariant(); b.suffix = b.suffix == null ? "" : b.suffix.ToLowerInvariant(); if (b.has == null) b.has = new string[0]; else for (int i = 0; i < b.has.Length; i++) b.has[i] = b.has[i] == null ? "" : b.has[i].ToLowerInvariant(); }
            foreach (var it in d.items) Fill(it);
            if (d.materials == null) d.materials = new MaterialCfg(); else { if (d.materials.shaderFallbacks == null) d.materials.shaderFallbacks = new string[0]; if (d.materials.sheenColor == null || d.materials.sheenColor.Length < 3) d.materials.sheenColor = new[] { 0.9f, 0.55f, 0.55f }; }
            if (d.materials.byName == null) d.materials.byName = new Dictionary<string, MatRow>(StringComparer.OrdinalIgnoreCase);
            ReadMaterialRows(json, d);
            if (d.zones.Length > 64) Debug.LogWarning("[Gear] more than 64 zones: the extra ones are ignored.");
            data = d;
        }
        catch (Exception ex) { Debug.LogError("[Gear] gear.json parse failed: " + ex.Message); data = new Data(); data.enabled = false; }
    }
    // GEAR2: materials.byName and items[].materials (free-key objects) through GearJson. A parse problem = no rows (one warning), never an exception.
    static void ReadMaterialRows(string json, Data d)
    {
        object root = GearJson.Parse(json);
        if (root == null) { Warn("gear.json material rows not read (" + GearJson.LastError + "): materials use the constants"); return; }
        var by = GearJson.Obj(GearJson.Obj(root, "materials"), "byName");
        if (by != null) foreach (var kv in by) { var r = ToRow(kv.Value as Dictionary<string, object>); if (r != null && !kv.Key.StartsWith("_")) d.materials.byName[kv.Key] = r; }
        var items = GearJson.Arr(root, "items"); if (items == null) return;
        foreach (var o in items)
        {
            var od = o as Dictionary<string, object>; if (od == null) continue;
            var it = FindIn(d, GearJson.Str(od, "id")); if (it == null) continue;
            var mm = GearJson.Obj(od, "materials"); if (mm == null) continue;
            foreach (var kv in mm) { var r = ToRow(kv.Value as Dictionary<string, object>); if (r != null && !kv.Key.StartsWith("_")) it.materialRows[kv.Key] = r; }
        }
    }
    static MatRow ToRow(Dictionary<string, object> o)
    {
        if (o == null) return null;
        return new MatRow { smoothness = GearJson.Num(o, "smoothness", -1f), metallic = GearJson.Num(o, "metallic", -1f), normalScale = GearJson.Num(o, "normalScale", -1f), sheen = GearJson.Num(o, "sheen", -1f) };
    }
    static Item FindIn(Data d, string id) { if (id.Length == 0) return null; for (int i = 0; i < d.items.Length; i++) if (d.items[i] != null && d.items[i].id == id) return d.items[i]; return null; }

    static void Fill(Item it)
    {
        if (it == null) return;
        if (it.id == null) it.id = ""; if (it.name == null) it.name = ""; if (it.slot == null) it.slot = ""; if (it.glb == null) it.glb = ""; if (it.resource == null) it.resource = "";
        if (it.layer == null) it.layer = ""; if (it.part == null) it.part = ""; if (it.notes == null) it.notes = "";
        if (it.glbMirror == null) it.glbMirror = ""; if (it.partMirror == null) it.partMirror = ""; if (it.materialRows == null) it.materialRows = new Dictionary<string, MatRow>(StringComparer.OrdinalIgnoreCase);
        if (it.coversZones == null) it.coversZones = new string[0]; if (it.hidesLayers == null) it.hidesLayers = new string[0]; if (it.dropFrom == null) it.dropFrom = new string[0];
        if (it.stats == null) it.stats = new Stats(); if (it.weapon == null) it.weapon = new WeaponDef(); it.weapon.Fix(); /* WEAPONSWAP v88 */ if (it.shield == null) it.shield = new ShieldDef(); it.shield.Fix(); /* SHIELD v1 */
    }
}

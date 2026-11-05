using System;
using System.IO;
using UnityEngine;

// LOOTUI v85: all loot / equip UI data. Assets/Data/loot_ui.json (copy in StreamingAssets, read first). Layout, keys, colours, text, slot list, stat labels, rarity colours,
// marker look and the demo drops are DATA; every field has a default so a missing file keeps a working UI, "enabled": false switches the whole feature off.
// Sizes are px at referenceHeight (1080) and scale with Screen.height like the HUD (hud.json). Colours are [r,g,b(,a)] 0..1.
public static class LootCfg
{
    [Serializable] public class SlotC { public string id = "", label = "", group = "", sub = ""; }
    [Serializable] public class StatC { public string stat = "", label = ""; public int decimals = 0; public bool higherIsBetter = true; }
    [Serializable] public class RarityC { public string id = "", label = ""; public float[] color = { 0.93f, 0.90f, 0.84f, 1f }; }
    [Serializable]
    public class ColorsC
    {
        public float[] ink = { 0.93f, 0.90f, 0.84f, 1f }, dim = { 0.62f, 0.60f, 0.56f, 0.9f }, charcoal = { 0.035f, 0.035f, 0.04f, 0.78f }, crimson = { 0.62f, 0.07f, 0.09f, 1f }, gold = { 0.86f, 0.69f, 0.30f, 1f };
        public float[] frame = { 0.62f, 0.49f, 0.26f, 0.95f }, frameDim = { 0.34f, 0.29f, 0.22f, 0.85f }, shadow = { 0f, 0f, 0f, 0.85f };
        public float[] panel = { 0.018f, 0.018f, 0.022f, 0.94f }, rowSelected = { 0.11f, 0.075f, 0.075f, 0.95f }, rowHover = { 0.08f, 0.07f, 0.07f, 0.9f }, dimmer = { 0f, 0f, 0f, 0.55f };
        public float[] better = { 0.45f, 0.78f, 0.38f, 1f }, worse = { 0.82f, 0.22f, 0.20f, 1f }, even = { 0.55f, 0.53f, 0.50f, 0.9f };
    }
    [Serializable] public class FontsC { public string[] serif = { "Palatino Linotype", "Book Antiqua", "Georgia", "Times New Roman" }, ui = { "Palatino Linotype", "Georgia", "Times New Roman" }; public int osSize = 40; }
    [Serializable]
    public class KeysC
    {
        public string[] pickup = { "E", "JoystickButton2" }, toggle = { "Tab", "JoystickButton7" }, close = { "Escape", "JoystickButton1" }, confirm = { "E", "JoystickButton0" }, unequip = { "U", "Delete", "JoystickButton2" };
        public string[] up = { "UpArrow", "W" }, down = { "DownArrow", "S" }, left = { "LeftArrow", "A" }, right = { "RightArrow", "D" };
        public string axisX = "Horizontal", axisY = "Vertical"; public float stickThreshold = 0.6f, repeatDelayS = 0.35f, repeatS = 0.11f;
    }
    [Serializable]
    public class PillarC
    {
        public string shader = "Furnace/Particle"; public int planes = 3, texW = 64, texH = 128, groundTex = 64;
        public float height = 2.6f, width = 0.55f, groundRadius = 0.7f, groundLift = 0.03f, intensity = 0.7f, groundIntensity = 0.6f, softDistance = 0.35f, alpha = 0.9f;
        public float widthEdgePower = 1.7f, fadeTopPower = 1.35f, fadeBottom = 0.04f, groundEdgePower = 2.2f;
        public float pulseHz = 0.5f, pulseDepth = 0.18f, fadeInS = 0.9f, riseFrom = 0.25f, spinDegPerS = 0f;
        public float flashS = 0.45f, flashHeightMul = 1.8f, flashWidthMul = 2.2f;
        public float groundRayUp = 1.5f, groundRayDown = 4f; public int renderQueue = 3000;
    }
    [Serializable]
    public class LabelC
    {
        public float height = 1.4f, fadeStartM = 14f, fadeEndM = 24f, nearAlpha = 1f, minScreenMargin = 24f;
        public int nameSize = 26, subSize = 13, promptSize = 15; public float lineGap = 4f, ruleW = 120f, promptGap = 8f, promptHeight = 24f;
        public bool showSub = true;
        public float stackGapY = 8f, boxW = 300f, cullMargin = 140f;   // HUDFIX v92: min vertical gap between stacked labels (px), label box width used for the overlap test, px past the viewport edge where a label is dropped instead of pinned to the edge
    }
    [Serializable]
    public class PickupC
    {
        public bool requireVictory = true, autoPickup = false; public float radius = 1.7f, promptRadius = 3.2f, spawnDelayS = 1.4f, spread = 1.1f, fallbackAfterS = 0.8f, toastS = 3.2f, toastFadeS = 0.6f, toastBottom = 150f;
        public int toastSize = 22, toastSubSize = 13; public bool fallbackDrops = true; public string[] fallbackItems = { "pitborn_pauldron" };
    }
    [Serializable]
    public class ScreenC
    {
        public float panelW = 1220f, panelH = 720f, pad = 28f, colGap = 22f, slotColW = 310f, candColW = 330f, frameW = 2f, accentBar = 3f;
        public int titleSize = 38, subtitleSize = 14, headerSize = 14, labelSize = 12, nameSize = 22, rowNameSize = 20, bodySize = 15, statSize = 17, hintSize = 14, badgeSize = 11, closeSize = 20;
        public float titleY = 18f, headerY = 92f, bodyTop = 124f, bodyBottom = 70f, slotRowH = 62f, candRowH = 52f, rowGap = 5f, statRowH = 30f, statValueW = 70f, arrowSize = 16f, deltaW = 64f;
        public float descGap = 14f, detailNameGap = 6f, openFadeS = 0.18f, hintY = 40f, closeBox = 34f;
        public bool openInFight = false, showEquipHint = true; public float equipHintBottom = 96f; public int equipHintSize = 15;
        public int triTex = 32; public float triPad = 4f;
    }
    [Serializable]
    public class TextC
    {
        public string title = "Equipment", subtitle = "What you carry and what you wear", slotsHeader = "Worn", candHeader = "Carried", detailHeader = "Details", emptySlot = "Empty", emptyRow = "Unequip", nothing = "Nothing carried for this slot",
            equipped = "Worn", elsewhere = "In {0}", comparedWith = "Compared with {0}", comparedEmpty = "Compared with an empty slot", removing = "Taking off", noStats = "No stats", selectItem = "Select an item",
            pickup = "Take", pickupKeys = "E / X", equipHint = "Tab / Start  Equipment", acquired = "Acquired", acquiredSub = "Tab / Start to equip", equipKeys = "E / A  Equip", unequipKeys = "U / X  Unequip", moveKeys = "Arrows / Stick  Move", closeKeys = "Tab / Start / Esc  Close",
            slotLine = "{0}  -  {1}", close = "X", cannotEquip = "That does not fit there";
    }
    [Serializable] public class SaveC { public string file = "loot_save.json"; public bool enabled = true; public int applyAfterFrames = 20; }
    [Serializable] public class CatalogC { public string gearFile = "gear.json"; public string[] gearStatKeys = { "armor", "poise", "weight" }; }
    [Serializable] public class DemoC { public string flag = "-lootdemo"; public float spawnAfterS = 2f, distance = 3f; public string[] items = { "pitborn_pauldron", "gate_warden_helm" }; }
    [Serializable]
    public class Data
    {
        public int version = 1; public bool enabled = true, debugLog = false; public float referenceHeight = 1080f, minScale = 0.6f, maxScale = 3f; public int guiDepth = -1100;
        public ColorsC colors = new ColorsC(); public FontsC fonts = new FontsC(); public KeysC keys = new KeysC(); public PillarC pillar = new PillarC(); public LabelC label = new LabelC();
        public PickupC pickup = new PickupC(); public ScreenC screen = new ScreenC(); public TextC text = new TextC(); public SaveC save = new SaveC(); public CatalogC catalog = new CatalogC(); public DemoC demo = new DemoC();
        public SlotC[] slots = new SlotC[0]; public StatC[] stats = new StatC[0]; public RarityC[] rarities = new RarityC[0]; public GearItem[] fallbackItems = new GearItem[0];
    }

    static Data data; static bool loaded;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { data = null; loaded = false; }

    public static Data Get()
    {
        if (loaded && data != null) return data;
        loaded = true; data = new Data();
        string txt = ReadData("loot_ui.json");
        if (txt != null)
        {
            try { var d = JsonUtility.FromJson<Data>(txt); if (d != null) { data = d; Fix(data); } else data = Fixed(new Data()); }
            catch (Exception e) { Debug.LogWarning("[LootUI] loot_ui.json parse failed, defaults kept: " + e.Message); data = Fixed(new Data()); }
        }
        else Fixed(data);
        return data;
    }
    public static void Reload() { loaded = false; LootDataCatalog.Reset(); }

    static Data Fixed(Data d) { Fix(d); return d; }
    static void Fix(Data d)
    {
        if (d.colors == null) d.colors = new ColorsC(); if (d.fonts == null) d.fonts = new FontsC(); if (d.keys == null) d.keys = new KeysC(); if (d.pillar == null) d.pillar = new PillarC();
        if (d.label == null) d.label = new LabelC(); if (d.pickup == null) d.pickup = new PickupC(); if (d.screen == null) d.screen = new ScreenC(); if (d.text == null) d.text = new TextC();
        if (d.save == null) d.save = new SaveC(); if (d.catalog == null) d.catalog = new CatalogC(); if (d.demo == null) d.demo = new DemoC();
        if (d.slots == null) d.slots = new SlotC[0]; if (d.stats == null) d.stats = new StatC[0]; if (d.rarities == null) d.rarities = new RarityC[0]; if (d.fallbackItems == null) d.fallbackItems = new GearItem[0];
        if (d.catalog.gearStatKeys == null) d.catalog.gearStatKeys = new string[0]; if (d.pickup.fallbackItems == null) d.pickup.fallbackItems = new string[0]; if (d.demo.items == null) d.demo.items = new string[0];
        for (int i = 0; i < d.slots.Length; i++) { var s = d.slots[i]; if (s == null) { d.slots[i] = s = new SlotC(); } if (s.id == null) s.id = ""; if (s.label == null) s.label = ""; if (s.group == null) s.group = ""; if (s.sub == null) s.sub = ""; }
        d.referenceHeight = Mathf.Max(240f, d.referenceHeight); d.minScale = Mathf.Max(0.2f, d.minScale); d.maxScale = Mathf.Max(d.minScale, d.maxScale);
        d.pillar.planes = Mathf.Clamp(d.pillar.planes, 1, 4); d.pillar.texW = Mathf.Clamp(d.pillar.texW, 8, 512); d.pillar.texH = Mathf.Clamp(d.pillar.texH, 8, 1024); d.pillar.groundTex = Mathf.Clamp(d.pillar.groundTex, 8, 512);
        d.screen.triTex = Mathf.Clamp(d.screen.triTex, 8, 128);
    }

    // ---- lookups ----
    public static SlotC Slot(string id) { var s = Get().slots; for (int i = 0; i < s.Length; i++) if (s[i] != null && s[i].id == id) return s[i]; return null; }
    public static string SlotLabel(string id) { var s = Slot(id); return s != null && s.label.Length > 0 ? s.label : Pretty(id); }
    /// <summary>An item's "slot" is a slot id or a slot group (gear.json rule).</summary>
    public static bool Fits(GearItem it, SlotC s) { return it != null && s != null && !string.IsNullOrEmpty(it.slot) && (it.slot == s.id || (s.group.Length > 0 && it.slot == s.group)); }
    public static StatC StatRow(string stat) { var s = Get().stats; for (int i = 0; i < s.Length; i++) if (s[i] != null && s[i].stat == stat) return s[i]; return null; }
    public static RarityC Rarity(string id) { var r = Get().rarities; for (int i = 0; i < r.Length; i++) if (r[i] != null && r[i].id == id) return r[i]; return r.Length > 0 ? r[0] : null; }
    public static Color RarityColor(GearItem it)
    {
        var r = it != null ? Rarity(it.rarity) : null; return r != null ? Col(r.color) : Col(Get().colors.ink);
    }
    public static string Pretty(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var parts = id.Replace('-', '_').Split('_'); var sb = new System.Text.StringBuilder();
        for (int i = 0; i < parts.Length; i++) { if (parts[i].Length == 0) continue; if (sb.Length > 0) sb.Append(' '); sb.Append(char.ToUpperInvariant(parts[i][0])).Append(parts[i].Substring(1)); }
        return sb.ToString();
    }

    // ---- io / parse ----
    public static string ReadData(string name)
    {
        foreach (var p in new[] { Path.Combine(Application.streamingAssetsPath, name), Path.Combine(Application.dataPath, "Data/" + name) })
        {
            try { if (File.Exists(p)) return File.ReadAllText(p).TrimStart((char)0xFEFF); } catch { }
        }
        return null;
    }
    public static Color Col(float[] a, float alpha = -1f)
    {
        if (a == null || a.Length < 3) return Color.white;
        return new Color(a[0], a[1], a[2], alpha >= 0f ? alpha : (a.Length > 3 ? a[3] : 1f));
    }
    public static KeyCode[] Keys(string[] names)
    {
        var list = new System.Collections.Generic.List<KeyCode>();
        if (names != null) for (int i = 0; i < names.Length; i++) { KeyCode k; if (!string.IsNullOrEmpty(names[i]) && Enum.TryParse(names[i], true, out k)) list.Add(k); }
        return list.ToArray();
    }
    public static bool AnyDown(KeyCode[] ks) { if (ks != null) for (int i = 0; i < ks.Length; i++) if (Input.GetKeyDown(ks[i])) return true; return false; }
    public static bool AnyHeld(KeyCode[] ks) { if (ks != null) for (int i = 0; i < ks.Length; i++) if (Input.GetKey(ks[i])) return true; return false; }
    public static float Scale() { var d = Get(); return Mathf.Clamp(Screen.height / d.referenceHeight, d.minScale, d.maxScale); }
}

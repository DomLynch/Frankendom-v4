using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

// V3 blood-edge port (src/fight/blood-edge.ts + blood-rows.json), tuned from Assets/Data/blood_edge.json.
// When the PLAYER takes a Hit, a dark blood strip flashes on the screen edge the blow came from (480 ms, peak held ~150 ms; ease-out fade).
// The move direction is the attacker's side, so it lands on the mirrored edge: right -> left edge, left -> right edge; front/other -> BOTH edges at headOn strength.
// Each hit shows the next strip in order (smear, bleed, streak...); the count restarts when the sim tick goes backwards (new fight). Strength scales with the damage taken.
// Drawn with IMGUI (GUI.DrawTexture, depth in data) because this project has no uGUI package (GameHUD is IMGUI too). Strip textures are generated in code.
// Presentation only: it reads ContactRelay.OnEvent and DuelFighter.health, never writes the sim. Dark, alpha-blended, non-glowing. Runs on unscaled time.
public class BloodEdge : MonoBehaviour
{
    [Serializable] public class Data
    {
        public bool enabled = true, playerOnly = true, symmetric = true;
        public float edgeMs = 480f, peakStart = 0.12f, peakEnd = 0.43f, headOn = 0.6f, widthFrac = 0.06f, spanFrac = 0.68f, damageRef = 20f, minScale = 0.55f;
        public int guiDepth = -1000, texW = 48, texH = 128, seed = 17;
        public float colorR = 0.14f, colorG = 0f, colorB = 0.01f;
        public string[] strips = new string[] { "wet-smear", "soft-bleed", "dragged-streak" };
    }

    class Flash { public int edge; public int strip; public float age, peak; }   // edge 0 left, 1 right

    static BloodEdge instance;
    Data d;
    Texture2D[,] tex;   // [edge, strip]
    readonly List<Flash> flashes = new List<Flash>();
    readonly Dictionary<DuelFighter, float> lastHealth = new Dictionary<DuelFighter, float>();
    int hits, lastTick = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var data = LoadData();
        if (data == null || !data.enabled) return;
        var go = new GameObject("BloodEdge");
        DontDestroyOnLoad(go);
        var be = go.AddComponent<BloodEdge>();
        be.d = data;
        instance = be;
        be.Build();
    }

    static Data LoadData()
    {
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("blood_edge");
            if (ta != null) json = ta.text;
            if (json == null)
            {
                string p = Path.Combine(Application.streamingAssetsPath, "blood_edge.json");
                if (File.Exists(p)) json = File.ReadAllText(p);
            }
            if (json == null)
            {
                string p = Path.Combine(Application.dataPath, "Data/blood_edge.json");
                if (File.Exists(p)) json = File.ReadAllText(p);
            }
        }
        catch (Exception ex) { Debug.LogWarning("BloodEdge: read failed: " + ex.Message); }
        if (string.IsNullOrEmpty(json)) { Debug.LogWarning("BloodEdge: blood_edge.json not found; blood edge disabled."); return null; }
        try
        {
            var data = JsonUtility.FromJson<Data>(json);
            if (data == null) return null;
            if (data.strips == null || data.strips.Length == 0) data.strips = new string[] { "wet-smear" };
            data.texW = Mathf.Clamp(data.texW, 8, 256); data.texH = Mathf.Clamp(data.texH, 8, 512);
            data.edgeMs = Mathf.Max(50f, data.edgeMs);
            return data;
        }
        catch (Exception ex) { Debug.LogError("BloodEdge: parse failed: " + ex.Message); return null; }
    }

    void Build()
    {
        int n = d.strips.Length;
        tex = new Texture2D[2, n];
        for (int s = 0; s < n; s++)
        {
            tex[0, s] = MakeStrip(s % 3, s, false);
            tex[1, s] = MakeStrip(s % 3, s, true);
        }
    }

    static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }

    // kind 0 smear (wobbling soft reach), 1 soft bleed (wide soft falloff), 2 dragged streak (horizontal streaks). u = 0 at the screen edge.
    Texture2D MakeStrip(int kind, int index, bool mirror)
    {
        int w = d.texW, h = d.texH;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
        t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear; t.name = "BloodEdge_" + index + (mirror ? "_R" : "_L");
        var px = new Color[w * h];
        var E = BloodShapes.Get().edge; var K = E.kinds[Mathf.Clamp(kind, 0, E.kinds.Length - 1)];   // HCKIT6 v100: strip curves = blood_shapes.json edge{}
        float off = d.seed * E.seedMul + index * E.indexMul;
        for (int y = 0; y < h; y++)
        {
            float v = (float)y / (h - 1);
            float vf = Smooth(0f, E.vFade, v) * Smooth(0f, E.vFade, 1f - v);
            float reach = K.reachBase;
            if (K.reachAmp != 0f) { float nn = Mathf.PerlinNoise(v * K.reachFreq + off, off + K.reachY); reach += K.reachSquare ? K.reachAmp * nn * nn : K.reachAmp * nn; }
            float rowMod = K.rowBase;
            if (K.rowAmp != 0f) rowMod += K.rowAmp * Mathf.PerlinNoise(v * K.rowFreq + off, off + K.rowY);
            for (int x = 0; x < w; x++)
            {
                float u = (float)x / (w - 1);
                float a = Mathf.Clamp01(1f - u / reach);
                a = K.pow == 1f ? a : Mathf.Pow(a, K.pow);
                a = Mathf.Clamp01(a * rowMod * vf * E.gain);
                float shade = E.shadeBase + E.shadeAmp * Mathf.PerlinNoise(u * E.shadeFreq + off, v * E.shadeFreq);
                int xi = mirror ? (w - 1 - x) : x;
                px[y * w + xi] = new Color(d.colorR * shade, d.colorG * shade, d.colorB * shade, a);
            }
        }
        t.SetPixels(px); t.Apply(false, true);
        return t;
    }

    void OnEnable() { ContactRelay.OnEvent += OnEvent; SceneManager.sceneLoaded += OnSceneLoaded; }
    void OnDisable() { ContactRelay.OnEvent -= OnEvent; SceneManager.sceneLoaded -= OnSceneLoaded; }
    void OnSceneLoaded(Scene s, LoadSceneMode m) { flashes.Clear(); lastHealth.Clear(); hits = 0; lastTick = -1; }

    void OnEvent(CombatEvent e)
    {
        if (d == null || tex == null || e.type != "Hit" || e.target == null) return;
        // damage taken since the previous hit on this fighter (the sim has already subtracted it)
        float dmg = 0f; float prev;
        if (lastHealth.TryGetValue(e.target, out prev)) dmg = prev - e.target.health;
        lastHealth[e.target] = e.target.health;
        if (d.playerOnly && e.target.GetComponent<PlayerBrain>() == null) return;

        if (e.tick < lastTick) hits = 0;
        lastTick = e.tick;
        int pick = hits % d.strips.Length; hits++;

        float scale = dmg > 0f ? Mathf.Lerp(d.minScale, 1f, Mathf.Clamp01(dmg / Mathf.Max(0.01f, d.damageRef))) : 1f;
        bool left = e.direction == "right", right = e.direction == "left";   // V3 EDGE: right -> left edge, left -> right edge, else both
        if (d.symmetric) { left = right = false; }   // v92 (Goose): one red band on one edge only read as a bug; symmetric = both edges every hit (data: blood_edge.json symmetric)
        bool both = !left && !right;
        float peak = (both ? d.headOn : 1f) * scale;
        if (left || both) flashes.Add(new Flash { edge = 0, strip = pick, peak = peak });
        if (right || both) flashes.Add(new Flash { edge = 1, strip = pick, peak = peak });
    }

    void Update()
    {
        if (flashes.Count == 0) return;
        float dt = Time.unscaledDeltaTime * 1000f;
        for (int i = flashes.Count - 1; i >= 0; i--)
        {
            flashes[i].age += dt;
            if (flashes[i].age >= d.edgeMs) flashes.RemoveAt(i);
        }
    }

    float Alpha(Flash f)
    {
        float t = Mathf.Clamp01(f.age / d.edgeMs);
        if (t < d.peakStart) return f.peak * (t / Mathf.Max(0.0001f, d.peakStart));
        if (t < d.peakEnd) return f.peak;
        float k = Mathf.Clamp01((t - d.peakEnd) / Mathf.Max(0.0001f, 1f - d.peakEnd));
        float eased = 1f - (1f - k) * (1f - k);   // ease-out on the fade keyframe only (V3)
        return f.peak * (1f - eased);
    }

    void OnGUI()
    {
        if (flashes.Count == 0 || Event.current.type != EventType.Repaint) return;
        GUI.depth = d.guiDepth;
        float w = Screen.width * d.widthFrac, h = Screen.height * d.spanFrac, y = (Screen.height - h) * 0.5f;
        Color keep = GUI.color;
        for (int i = 0; i < flashes.Count; i++)
        {
            var f = flashes[i]; float a = Alpha(f);
            if (a <= 0.001f) continue;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
            float x = f.edge == 0 ? 0f : Screen.width - w;
            GUI.DrawTexture(new Rect(x, y, w, h), tex[f.edge, f.strip], ScaleMode.StretchToFill, true);
        }
        GUI.color = keep;
    }
}

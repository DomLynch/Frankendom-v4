using System;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

// SWINGFIX v103: four Goose call-outs, every switch in Assets/Data/swingfix.json (StreamingAssets copy first in the player). Defaults = today's behaviour.
//   swingSeparation  padM > 0 widens the root-to-root separation clamp (and the lunge stop distance) while either fighter is in one of `states`. padM 0 = off. padM > 0 changes positions: SIM CHANGE.
//   finisherPreStagger  presentation only (the victim already has HP 0 at the contact): plays a stagger clip on the victim before its finisher clip. Look change, dark (on=false).
//   gripCombatInLoco   drops the listed locomotion grip rows (weapon_grip.json rows with `states`) so those states use the character's combat grip row. Look change, dark.
//   faceCast           removes the character light-layer bit from the named lights at runtime (the Editor-time HdrpCharLayer only strips lights listed in hdrp_char_layer.json). Look change, dark.
public static class SwingFix
{
    [Serializable] public class Sep { public float padM; public string[] states; }
    [Serializable] public class PreRow { public string entry = "", killer = "*", victim = "*", clip = ""; public float atS, fade = 0.15f, speed = 1f; }
    [Serializable] public class Pre { public bool on; public PreRow[] rows; }
    [Serializable] public class Grip { public bool on; public string[] names; public string[] states; }
    [Serializable] public class Face { public bool on; public string[] lightNames; }
    [Serializable] public class Root { public Sep swingSeparation = new Sep(); public Pre finisherPreStagger = new Pre(); public Grip gripCombatInLoco = new Grip(); public Face faceCast = new Face(); }
    [Serializable] class CharLayer { public int charBit = 7; }

    static Root cfg; static bool[] swingMask;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; swingMask = null; }

    public static Root Cfg()
    {
        if (cfg != null) return cfg;
        cfg = new Root();
        string txt = DataPath.ReadText("swingfix.json");
        if (!string.IsNullOrEmpty(txt)) { try { var r = JsonUtility.FromJson<Root>(txt); if (r != null) cfg = r; } catch (Exception e) { Debug.LogWarning("[SwingFix] swingfix.json: " + e.Message); } }
        if (cfg.swingSeparation == null) cfg.swingSeparation = new Sep();
        if (cfg.finisherPreStagger == null) cfg.finisherPreStagger = new Pre();
        if (cfg.gripCombatInLoco == null) cfg.gripCombatInLoco = new Grip();
        if (cfg.faceCast == null) cfg.faceCast = new Face();
        return cfg;
    }

    // ---- 1. swing overlap ----
    public static float PadM() { return Mathf.Max(0f, Cfg().swingSeparation.padM); }

    static bool Swinging(DuelFighter f)
    {
        if (f == null) return false;
        if (swingMask == null)
        {
            swingMask = new bool[Enum.GetValues(typeof(FState)).Length];
            var names = Cfg().swingSeparation.states; if (names == null) names = new string[0];
            for (int i = 0; i < names.Length; i++) { FState s; if (Enum.TryParse(names[i], out s)) swingMask[(int)s] = true; }
        }
        return swingMask[(int)f.state];
    }

    /// <summary>Extra metres on the minimum root separation while either fighter swings (0 when padM is 0). Symmetric, so both fighters yield the same half.</summary>
    public static float ExtraSep(DuelFighter a, DuelFighter b) { float p = PadM(); return p > 0f && (Swinging(a) || Swinging(b)) ? p : 0f; }

    // ---- 2. split-crown stagger ----
    public static PreRow PreStagger(string entryId, string killerId, string victimId)
    {
        var p = Cfg().finisherPreStagger; if (!p.on || p.rows == null) return null;
        PreRow best = null; int bestScore = -1;
        for (int i = 0; i < p.rows.Length; i++)
        {
            var r = p.rows[i]; if (r == null || r.entry != entryId || string.IsNullOrEmpty(r.clip)) continue;
            bool kx = r.killer == killerId, vx = r.victim == victimId;
            if (!(kx || r.killer == "*") || !(vx || r.victim == "*")) continue;
            int score = (kx ? 2 : 0) + (vx ? 1 : 0);
            if (score > bestScore) { bestScore = score; best = r; }
        }
        return best;
    }

    // ---- 3. sword grip ----
    public static bool DropGripRow(string rowName, string[] rowStates)
    {
        var g = Cfg().gripCombatInLoco; if (!g.on || g.names == null || g.states == null || rowStates == null || rowStates.Length == 0) return false;
        if (Array.IndexOf(g.names, rowName) < 0) return false;
        for (int i = 0; i < rowStates.Length; i++) if (Array.IndexOf(g.states, rowStates[i]) >= 0) return true;
        return false;
    }

    // ---- 4. face cast ----
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!Cfg().faceCast.on) return;
        var go = new GameObject("SwingFixApply"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<Applier>();
    }

    class Applier : MonoBehaviour
    {
        int frames; float t; bool second;
        void OnEnable() { UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnLoaded; }
        void OnDisable() { UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnLoaded; }
        void OnLoaded(UnityEngine.SceneManagement.Scene s, UnityEngine.SceneManagement.LoadSceneMode m) { frames = 0; t = 0f; second = false; }
        void Update()
        {
            frames++; t += Time.unscaledDeltaTime;
            if (frames == 5) FaceCastApply();
            else if (!second && t > 2f) { second = true; FaceCastApply(); }
        }
    }

    static void FaceCastApply()
    {
        var f = Cfg().faceCast; if (!f.on || f.lightNames == null) return;
        int bit = 7; string lt = DataPath.ReadText("hdrp_char_layer.json");
        if (!string.IsNullOrEmpty(lt)) { try { var cl = JsonUtility.FromJson<CharLayer>(lt); if (cl != null) bit = cl.charBit; } catch { } }
        uint cb = 1u << bit;
        var lights = UnityEngine.Object.FindObjectsByType<HDAdditionalLightData>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < lights.Length; i++)
        {
            var hd = lights[i]; if (hd == null || Array.IndexOf(f.lightNames, hd.name) < 0) continue;
            uint m = (uint)hd.lightlayersMask; if ((m & cb) == 0u) continue;
            hd.lightlayersMask = (UnityEngine.Rendering.HighDefinition.RenderingLayerMask)(m & ~cb);
            Debug.Log("[SwingFix] faceCast: light " + hd.name + " no longer lights the character layer (bit " + bit + ")");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

// V64F: "the victim's corpse looks bleached" diagnostic + guard (-finisherauto and the finisher itself).
// Findings from the code (nothing in the finisher code writes an albedo): GoreFx decals / FinisherCleave quads are separate premultiplied dark-red quads (Furnace/Particle, never touch the body material);
// MeshCut.CapMaterial clones a material only for the cut faces; HitFlash is the ONLY system that writes _BaseColor / _EmissiveColor through a MaterialPropertyBlock (player row disabled, restores on death).
// So the cause cannot be proven statically: this class measures it. Sample() = mean luma of the victim's base colours as the renderer will SHOW them (property block value when one overrides it, else the shared material),
// the mean of the shared materials, how many slots carry a property block, the emissive peak, and how many decal renderers (Furnace/Particle) hang on the victim.
// Scrub() (data rt.victimLook.scrub) clears a property block whose shown base luma is more than `rise` above its material's luma (or that adds emissive): a stuck flash. Off = measure only.
[Serializable]
public class VictimLookCfg
{
    public bool enabled = true;
    public bool scrub = true;          // clear property blocks that lighten a material (stuck flash) at the contact, at the death and in the final hold
    public float rise = 0.25f;         // shown luma more than this fraction above the material's own (+ 0.02) = bleached
    public float emissiveNits = 0.5f;  // a property-block emissive above this = glowing
    public float maxRise = 0.25f;      // -finisherauto: mean shown luma after the scene may exceed the value before the scene by at most this fraction, else ERROR (0 = no check)
}

public static class FinisherVictimLook
{
    public struct Stat
    {
        public int slots, withBlock, decals, lightened; public float shared, shown, emissiveMax; public bool any;
        public string Line()
        {
            if (!any) return "no renderers";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return "mean base luma shared " + shared.ToString("0.000", ic) + " shown " + shown.ToString("0.000", ic) + " (" + slots + " slots, " + withBlock + " with a property block, " + lightened + " lightened, emissive peak " + emissiveMax.ToString("0.00", ic) + ", decal renderers " + decals + ")";
        }
    }
    public static Stat Before, After; public static int Scrubbed;
    static readonly MaterialPropertyBlock mpb = null;
    static MaterialPropertyBlock Mpb { get { return mpb ?? (tmp ?? (tmp = new MaterialPropertyBlock())); } }
    static MaterialPropertyBlock tmp;

    static VictimLookCfg Cfg() { var f = FinisherConfig.Get(); return f != null && f.rt != null ? f.rt.victimLook : null; }
    static float Luma(Color c) { return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; }
    static string BaseProp(Material m) { return m.HasProperty("_BaseColor") ? "_BaseColor" : m.HasProperty("_Color") ? "_Color" : null; }

    public static void Reset() { Before = default(Stat); After = default(Stat); Scrubbed = 0; }

    public static Stat Sample(DuelFighter v)
    {
        var st = new Stat(); if (v == null) return st;
        double sh = 0, sn = 0;
        foreach (var r in v.GetComponentsInChildren<Renderer>(false))
        {
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer || !r.enabled) continue;
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i]; if (m == null) continue;
                if (m.shader != null && m.shader.name == "Furnace/Particle") { st.decals++; continue; }
                string p = BaseProp(m); if (p == null) continue;
                Color c = m.GetColor(p), cur = c; bool blk = false; float em = 0f;
                r.GetPropertyBlock(Mpb, i);
                if (!Mpb.isEmpty)
                {
                    if (Mpb.HasColor(p)) { cur = Mpb.GetColor(p); blk = true; }
                    if (Mpb.HasColor("_EmissiveColor")) { var e = Mpb.GetColor("_EmissiveColor"); em = Mathf.Max(e.r, Mathf.Max(e.g, e.b)); }
                }
                st.slots++; if (blk) st.withBlock++; sh += Luma(c.linear); sn += Luma(cur.linear);
                if (Luma(cur.linear) > Luma(c.linear) * 1.25f + 0.02f) st.lightened++;
                if (em > st.emissiveMax) st.emissiveMax = em;
            }
        }
        st.any = st.slots > 0; if (st.any) { st.shared = (float)(sh / st.slots); st.shown = (float)(sn / st.slots); }
        return st;
    }

    // clears the property block of every slot that lightens its material beyond cfg.rise (or glows); returns the number of slots cleared
    public static int Scrub(DuelFighter v, string why)
    {
        var C = Cfg(); if (v == null || C == null || !C.enabled || !C.scrub) return 0;
        int n = 0;
        foreach (var r in v.GetComponentsInChildren<Renderer>(false))
        {
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i]; if (m == null) continue; string p = BaseProp(m); if (p == null) continue;
                r.GetPropertyBlock(Mpb, i); if (Mpb.isEmpty) continue;
                bool bad = false;
                if (Mpb.HasColor(p)) { float a = Luma(m.GetColor(p).linear), b = Luma(Mpb.GetColor(p).linear); if (b > a * (1f + C.rise) + 0.02f) bad = true; }
                if (!bad && Mpb.HasColor("_EmissiveColor")) { var e = Mpb.GetColor("_EmissiveColor"); if (Mathf.Max(e.r, Mathf.Max(e.g, e.b)) > C.emissiveNits) bad = true; }
                if (bad) { r.SetPropertyBlock(null, i); n++; }
            }
        }
        if (n > 0) { Scrubbed += n; Debug.Log("[finisher] victim look: cleared " + n + " lightening / glowing property blocks on " + v.name + " (" + why + ")"); }
        return n;
    }

    public static void Verify(string id, List<string> errors, List<string> warns, out string line)
    {
        line = "";
        var C = Cfg(); if (C == null || !C.enabled) return;
        if (!Before.any || !After.any) { line = "victim material luma: no sample"; return; }
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        float rise = Before.shown > 1e-4f ? After.shown / Before.shown - 1f : 0f;
        line = "victim material luma: before " + Before.Line() + " | after " + After.Line() + " | change " + (rise * 100f).ToString("+0;-0", ic) + "% (limit +" + (C.maxRise * 100f).ToString("0", ic) + "%), property blocks scrubbed x" + Scrubbed;
        if (C.maxRise > 0f && rise > C.maxRise) errors.Add("victim bleached: mean material base luma rose " + (rise * 100f).ToString("0", ic) + "% over the scene (limit " + (C.maxRise * 100f).ToString("0", ic) + "%): a property block / material override lightens it, see the victim material luma line");
        if (After.lightened > 0) warns.Add("victim has " + After.lightened + " material slots lightened by a property block at the end of the scene");
        if (After.emissiveMax > C.emissiveNits) warns.Add("victim emissive property-block peak " + After.emissiveMax.ToString("0.00", ic) + " at the end of the scene");
    }
}

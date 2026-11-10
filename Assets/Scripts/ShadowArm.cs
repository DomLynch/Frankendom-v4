#if !FRANKEN_NO_HARNESS
using System;
using System.Reflection;
using UnityEngine;

// SHADOWARM (laneFPSLEVERS): harness-only same-build pair for the static-prop shadow-caster lever (ShadowProxy, data-driven stand-in casters from existing lower LODs).
//   -shadowarm:0  ShadowProxy forced OFF  (every caster as today, even if shadow_proxy.json enabled=true)   [control]
//   -shadowarm:1  ShadowProxy forced ON   (visible LOD chain untouched; casters swap to the data-chosen lower LOD where the error stays under maxErrTexels)
// No argument = this class does nothing (no object, no reflection). Compiled out with FRANKEN_NO_HARNESS.
// How: ShadowProxy keeps its CLI switches in private statics (forceOn / none) that are parsed lazily from Environment.GetCommandLineArgs. This runs BeforeSceneLoad (after the statics
// reset, before ShadowProxy.Boot at AfterSceneLoad), forces that parse through the public ShadowProxy.C getter, then sets the flags. Nothing else is touched: no renderer, no asset, no sim.
// If the reflection fails (field renamed) it logs "[ShadowArm] FAILED" and changes nothing: the arm then equals the plain build, and arms_shadow.json also carries the native -shadowproxy:on|none rows.
// The sun-cascade cache (ShadowCache S1) is NOT part of this arm: it needs HDRP asset flag allowDirectionalMixedCachedShadows=1, i.e. a different asset, so it cannot be a same-build pair.
public static class ShadowArm
{
    const string Arg = "-shadowarm:";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot()
    {
        int arm = -1;
        foreach (var a in Environment.GetCommandLineArgs())
            if (a.StartsWith(Arg, StringComparison.OrdinalIgnoreCase)) arm = a.Substring(Arg.Length).Trim() == "0" ? 0 : 1;
        if (arm < 0) return;
        try
        {
            var cfg = ShadowProxy.C;                                   // forces ParseArgs once (it resets both flags), so set ours after it
            var t = typeof(ShadowProxy); const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Static;
            var fOn = t.GetField("forceOn", F); var fNone = t.GetField("none", F); var fFar = t.GetField("farOnly", F);
            if (fOn == null || fNone == null || fFar == null) { Debug.LogError("[ShadowArm] FAILED: ShadowProxy flag fields not found, arm ignored"); return; }
            fOn.SetValue(null, arm == 1); fNone.SetValue(null, arm == 0); if (arm == 0) fFar.SetValue(null, false);
            Debug.Log("[ShadowArm] arm " + (arm == 1 ? "ON" : "OFF") + ": ShadowProxy.On=" + ShadowProxy.On + " (data enabled=" + cfg.enabled + ", rows=" + (cfg.assets != null ? cfg.assets.Length : 0) + ")");
        }
        catch (Exception e) { Debug.LogError("[ShadowArm] FAILED: " + e.Message + ", arm ignored"); }
    }
}
#endif

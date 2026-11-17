using System;
using UnityEngine;

// QALEAN v1 (QA players only: nothing runs without -qalean:<mip> on the command line). Why: a Frankendom QA player takes 12.7-15 GB of working set whatever the quality tier (Unity-managed is only ~2.6 GB; the rest is native: the 6.2 GB
// sharedassets0.assets.resS texture / mesh data, the runtime-loaded glTF (Gear, Npc, Mount, MetaHumanHead, MonsterLook, CharTex ~1.5 GB of files, decoded much bigger) and the GPU buffers of an iGPU, which Windows counts as system RAM).
// What it does: QualitySettings.globalTextureMipmapLimit = <mip> (0 = full, 1 = half width/height = 1/4 of the texture memory, 2 = 1/16) and texture streaming with a small budget, before the first scene loads. It changes nothing in the sim.
// Log: "[QaLean] ..." lines. Usage: gateproof FK_QA_EXTRA='-qalean:2'. Not for Dom's builds (a flag nobody passes = no effect).
public static class QaLean
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Boot() { Apply(); }
    // also called from ZoneGroundMat.Boot: a compile-only build keeps the OLD RuntimeInitializeOnLoadMethods table, so a new attribute-only hook never runs there
    public static void Apply()
    {
        try
        {
            string a = null; foreach (var x in Environment.GetCommandLineArgs()) if (x.StartsWith("-qalean:", StringComparison.Ordinal)) a = x.Substring(8);
            if (a == null) return; int mip; if (!int.TryParse(a, out mip)) mip = 2; mip = Mathf.Clamp(mip, 0, 4);
            QualitySettings.globalTextureMipmapLimit = mip;
            string budget = null; foreach (var x in Environment.GetCommandLineArgs()) if (x.StartsWith("-qaleanbudget:", StringComparison.Ordinal)) budget = x.Substring(14);
            if (budget != null) { float mb; if (float.TryParse(budget, out mb)) { QualitySettings.streamingMipmapsActive = true; QualitySettings.streamingMipmapsMemoryBudget = mb; } }
            Debug.Log("[QaLean] globalTextureMipmapLimit " + mip + (budget != null ? ", streaming mip budget " + budget + " MB" : "") + " (QA only)");
        }
        catch (Exception e) { Debug.LogWarning("[QaLean] failed: " + e.Message); }
    }
}

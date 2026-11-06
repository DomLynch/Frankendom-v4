using UnityEngine;

// Deterministic HDRP rebuild: URP material export (Tools/export_mats.py, always from the frozen URP project) -> HdrpConvert -> data-driven lighting/colours (hdrp_lighting_*.json) -> scene fixes (hdrp_fixes.json).
// Idempotent: a re-run lands on the same state, because every input is either the URP source or a data file.
public static class HdrpRebuild
{
    public static void Run()
    {
        HdrpConvert.Run();
        HdrpScene.Run();
        HdrpFixes.Run();
        Debug.Log("HDRPREBUILD done");
    }
}

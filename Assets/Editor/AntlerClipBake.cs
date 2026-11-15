using UnityEditor;
using UnityEngine;

// FK/COO: bake the NpcAntlerKnight sword attack clips only (ClipSets.Only filters the bake entries to one roster id; AttackClipSetup.Run is the existing baker).
// Batch: -executeMethod AntlerClipBake.Run
public static class AntlerClipBake
{
    [MenuItem("Tools/Combat/Bake Antler Knight sword clips")]
    public static void Run() { ClipSets.Only = "NpcAntlerKnight"; try { AttackClipSetup.Run(); } finally { ClipSets.Only = ""; } Debug.Log("AntlerClipBake: done"); }
}
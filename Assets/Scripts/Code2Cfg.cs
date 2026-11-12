using System;
using UnityEngine;

// CODE2 v1 (laneCODE2): ONE data file, three separately switchable blocks. Assets/Data/code2.json (twin in StreamingAssets). File missing / block enabled=false = the old behaviour exactly.
//   tether    : a boss that leaves its arena (centre + radius per spawn id or creature, data) is put back at its home spot. Used by ZoneRuntime.TetherStep.
//   rigClone  : RagdollRig.Heal on a ZoneRuntime clone also resets the copied state (Converted / Frozen / SimTime) and logs only the first logMax times.
//   npcSnap   : HubNpcs.Place lowers the NPC feet from the ground-ray hit to the visible floor (per-zone yOffset, optional lowest hit inside a band).
public static class Code2Cfg
{
    [Serializable] public class TetherRow { public string id = "", creature = ""; public bool enabled = true; public float[] centre = new float[0]; public float radius = 15.5f, minY = -999f; }
    [Serializable] public class TetherBlock
    {
        public bool enabled = false, log = true, healOnReturn = false; public float checkEveryS = 0.25f, returnUpM = 0.05f, logEveryS = 5f; public TetherRow[] rows = new TetherRow[0];
        public TetherRow Find(string spawnId, string creature)
        {
            if (rows == null) return null;
            for (int i = 0; i < rows.Length; i++) { var r = rows[i]; if (r != null && r.enabled && ((r.id.Length > 0 && r.id == spawnId) || (r.id.Length == 0 && r.creature.Length > 0 && r.creature == creature))) return r; }
            return null;
        }
    }
    [Serializable] public class RigCloneBlock { public bool enabled = false; public int logMax = 3; }
    [Serializable] public class SnapRow { public string zone = ""; public bool enabled = true; public float yOffset = 0f, lowestHitBandM = 0f, maxDropM = 0.2f; }
    [Serializable] public class SnapBlock
    {
        public bool enabled = false, log = true; public SnapRow[] rows = new SnapRow[0];
        public SnapRow Find(string zone) { if (rows == null) return null; for (int i = 0; i < rows.Length; i++) { var r = rows[i]; if (r != null && r.enabled && r.zone == zone) return r; } return null; }
    }
    [Serializable] public class File { public bool enabled = true; public TetherBlock tether = new TetherBlock(); public RigCloneBlock rigClone = new RigCloneBlock(); public SnapBlock npcSnap = new SnapBlock(); }

    static File f; static bool loaded;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { f = null; loaded = false; }
    public static File Get()
    {
        if (loaded) return f; loaded = true;
        string txt = DataPath.ReadText("code2.json");
        if (txt != null) { try { f = JsonUtility.FromJson<File>(txt); } catch (Exception e) { Debug.LogWarning("[code2] code2.json parse failed: " + e.Message); } }
        if (f != null && !f.enabled) f = null;
        return f;
    }

    /// <summary>npcSnap: y for an NPC whose ground ray found 'y'. hits/cnt are the ray's hits; colliders under skipRoot (the player) are ignored.</summary>
    public static float SnapNpcY(string zone, string npcId, float y, RaycastHit[] hits, int cnt, Transform skipRoot)
    {
        var c = Get(); if (c == null || !c.npcSnap.enabled) return y;
        var r = c.npcSnap.Find(zone); if (r == null) return y;
        float ny = y;
        if (r.lowestHitBandM > 0f)
            for (int i = 0; i < cnt; i++) { var col = hits[i].collider; if (col == null || (skipRoot != null && col.transform.IsChildOf(skipRoot))) continue; float hy = hits[i].point.y; if (hy < ny && hy >= y - r.lowestHitBandM) ny = hy; }
        ny += r.yOffset;
        ny = Mathf.Max(ny, y - Mathf.Max(0f, r.maxDropM)); ny = Mathf.Min(ny, y);   // only ever lowers, never more than maxDropM
        if (c.npcSnap.log) Debug.Log("[code2] npcSnap " + npcId + " (" + zone + "): ground hit y " + y.ToString("F3") + " -> " + ny.ToString("F3"));
        return ny;
    }
}

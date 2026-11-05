using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// KIT GEAR v79. glTFast (6.20) does not expose node extras, so the glTF "hidden" flag (extras {"hidden": true}, written by joan_chef_v2.glb and by the kitGEAR build_joan_combat.py on Layer_Tunic / Helmet / Bracers / Greaves)
// is read here straight from the .glb JSON chunk. JoanSwap menu 2 calls Apply() on the freshly instantiated body: every node the file marks hidden is deactivated at import time, so no gear layer shows until GearSlots
// equips it (GearSlots.FindLayer finds inactive renderers and SetActive(true)s the worn ones). Editor only (Assets/Editor), no UnityEditor API: it also compiles in the plain Roslyn check.
public static class GearGlbHidden
{
    [Serializable] class Ex { public bool hidden = false; }
    [Serializable] class Nd { public string name = ""; public Ex extras = new Ex(); }
    [Serializable] class Doc { public Nd[] nodes = new Nd[0]; }

    // names of the nodes flagged hidden in the .glb (empty on any problem: never throws)
    public static HashSet<string> Read(string glbPath)
    {
        var set = new HashSet<string>();
        try
        {
            using (var fs = new FileStream(glbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(fs))
            {
                if (br.ReadUInt32() != 0x46546C67u) return set;   // "glTF"
                br.ReadUInt32(); br.ReadUInt32();                 // version, total length
                uint len = br.ReadUInt32(); uint type = br.ReadUInt32();
                if (type != 0x4E4F534Au) return set;              // first chunk must be JSON
                var doc = JsonUtility.FromJson<Doc>(Encoding.UTF8.GetString(br.ReadBytes((int)len)));
                if (doc != null && doc.nodes != null) foreach (var n in doc.nodes) if (n != null && n.extras != null && n.extras.hidden && !string.IsNullOrEmpty(n.name)) set.Add(n.name);
            }
        }
        catch (Exception e) { Debug.LogWarning("[GearGlbHidden] " + glbPath + ": " + e.Message); }
        return set;
    }

    // deactivate every transform of instance whose name the glb flags hidden; returns how many were flagged
    public static int Apply(GameObject instance, string glbPath)
    {
        if (instance == null || string.IsNullOrEmpty(glbPath)) return 0;
        var hidden = Read(glbPath); int flagged = 0, off = 0;
        foreach (var t in instance.GetComponentsInChildren<Transform>(true))
            if (hidden.Contains(t.name)) { flagged++; if (t.gameObject.activeSelf) { t.gameObject.SetActive(false); off++; } }
        Debug.Log("GEAR hidden-at-import: " + flagged + " node(s) flagged hidden in the glTF extras of " + glbPath + " (" + off + " newly deactivated, the rest were already off by the Layer_ prefix rule): " + string.Join(",", new List<string>(hidden).ToArray()));
        return flagged;
    }
}

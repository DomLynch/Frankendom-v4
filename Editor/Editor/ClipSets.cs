using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// HCKIT5 v95 (HC4 K5): shared clip sets for AttackClipSetup / DodgeClipSetup.
// attack_clips.json / dodge_clips.json hold  "clipSets": [ { "id": "mixamo_sword", "referenceState": "guard_idle", "outGlb": "{folder}/{id}_attacks_v2.glb", "outClipDir": "{folder}/AttackClips", "clips": [ {state,...} ] } ]
// and a roster row names its set with  "clipSet": "mixamo_sword"  (roster.json; Roster.cs ignores the key, this Editor class reads it itself).
// A roster character with a clipSet and NO explicit "bake" row gets a bake entry built from its roster row: controller, baseGlb = modelGlb, outGlb / outClipDir from the patterns
// ({id} = roster id, {folder} = the folder of modelGlb, RosterEditor.Folder). Explicit "bake" rows (Dracula, Joan) are kept exactly as they are and win.
// ExpandJson returns the SAME json shape the two setup tools already parse, so their code only changes at the one line that reads the file.
// Only = one roster id (set by CharacterSetup for a single-character run); "" = every entry (today's menu behaviour).
public static class ClipSets
{
    public static string Only = "";

    [Serializable] class Clip { public string state = ""; }
    [Serializable] class Entry { public string character = "", controller = "", baseGlb = "", outGlb = "", outClipDir = "", referenceState = ""; public Clip[] clips = new Clip[0]; }
    [Serializable] class Set { public string id = "", referenceState = "guard_idle", outGlb = "", outClipDir = ""; public Clip[] clips = new Clip[0]; }
    [Serializable] class Doc { public Entry[] bake = new Entry[0]; public Set[] clipSets = new Set[0]; }
    [Serializable] class RowExt { public string id = "", clipSet = ""; }
    [Serializable] class RosterExt { public RowExt[] characters = new RowExt[0]; }

    // roster id -> clipSet id (roster.json "clipSet"); empty when the key is absent
    public static Dictionary<string, string> RosterSets()
    {
        var o = new Dictionary<string, string>();
        string p = "Assets/Data/roster.json";
        if (!File.Exists(p)) return o;
        var r = JsonUtility.FromJson<RosterExt>(File.ReadAllText(p));
        if (r != null && r.characters != null) foreach (var c in r.characters) if (c != null && c.id.Length > 0 && c.clipSet.Length > 0) o[c.id] = c.clipSet;
        return o;
    }

    static string Pattern(string pat, RosterCharacter r) { return pat.Replace("{id}", r.id).Replace("{folder}", RosterEditor.Folder(r)); }

    // kind = "attack" | "dodge" (only used in log lines). Returns json with bake[] = explicit rows + rows built from clipSets (filtered to Only when set).
    public static string ExpandJson(string json, string kind)
    {
        var doc = JsonUtility.FromJson<Doc>(json);
        if (doc == null) return json;
        var list = new List<Entry>();
        var have = new HashSet<string>();
        if (doc.bake != null) foreach (var e in doc.bake) { if (e == null) continue; list.Add(e); have.Add(e.character); }
        Roster.Reload();
        var sets = RosterSets();
        int built = 0;
        foreach (var kv in sets)
        {
            if (have.Contains(kv.Key)) continue;
            Set s = null; if (doc.clipSets != null) foreach (var c in doc.clipSets) if (c != null && c.id == kv.Value) s = c;
            if (s == null) { Debug.LogWarning("ClipSets(" + kind + "): roster '" + kv.Key + "' names clipSet '" + kv.Value + "' but " + kind + "_clips.json has no such set"); continue; }
            var r = Roster.Get(kv.Key);
            if (string.IsNullOrEmpty(r.controller) || string.IsNullOrEmpty(r.modelGlb)) { Debug.LogWarning("ClipSets(" + kind + "): roster '" + kv.Key + "' needs controller + modelGlb"); continue; }
            list.Add(new Entry { character = r.id, controller = r.controller, baseGlb = r.modelGlb, outGlb = Pattern(s.outGlb, r), outClipDir = Pattern(s.outClipDir, r), referenceState = s.referenceState, clips = s.clips });
            built++;
        }
        if (!string.IsNullOrEmpty(Only)) list = list.FindAll(e => e.character == Only);
        if (built > 0) Debug.Log("ClipSets(" + kind + "): " + built + " bake entr" + (built == 1 ? "y" : "ies") + " built from clipSets (roster clipSet), " + list.Count + " to run");
        return JsonUtility.ToJson(new Doc { bake = list.ToArray(), clipSets = new Set[0] });
    }
}

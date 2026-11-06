using System;
using System.IO;
using UnityEngine;

// Camera impact numbers as DATA: loads Assets/Data/camera_impact.json (V3 hit-stop-defs.json contacts + camera-kick.ts table + ROLL_TUMBLE).
// Lookup order: Resources/camera_impact (TextAsset), StreamingAssets/camera_impact.json, then Application.dataPath/Data/camera_impact.json (editor and dev runs).
// There are no numeric fallbacks on purpose: if the file is missing the camera impact layer is off and an error is logged once.
public static class ImpactTable
{
    [Serializable] public class Contact { public string id; public float knock, push, drop; }
    [Serializable] public class Feel { public float knockSettle, specialKickAlong, specialKickDrop; }
    [Serializable] public class BaseKick { public string id; public float along, drop, side, holdFrames, settle; }
    [Serializable] public class MoveWeight { public string id; public float mul = 1f; }   // camera kick scale per move id (hit weight): light small, heavy big
    [Serializable] public class RollTumble { public float angleDeg, seconds, shift, dip; }
    [Serializable] public class Data
    {
        public Contact[] contacts = new Contact[0];
        public Feel feel = new Feel();
        public BaseKick[] baseKick = new BaseKick[0];
        public RollTumble rollTumble = new RollTumble();
        public float simHz = 60f;
        public MoveWeight[] moveWeights = new MoveWeight[0];   // P1: scales the whole camera kick of a landed hit by the move that caused it (1 when absent)
    }

    static Data data;
    static readonly Contact emptyContact = new Contact();
    static readonly BaseKick emptyKick = new BaseKick();

    public static Data Get() { if (data == null) Load(); return data; }

    public static Contact ContactRow(string id)
    {
        var d = Get();
        for (int i = 0; i < d.contacts.Length; i++) if (d.contacts[i] != null && d.contacts[i].id == id) return d.contacts[i];
        return emptyContact;
    }

    public static BaseKick KickRow(string id)
    {
        var d = Get();
        for (int i = 0; i < d.baseKick.Length; i++) if (d.baseKick[i] != null && d.baseKick[i].id == id) return d.baseKick[i];
        return emptyKick;
    }

    // camera kick scale for a landed move (moveWeights row by id, then by the "skill_" prefix row "skill_*"); 1 when no row
    public static float MoveMul(string moveId)
    {
        var w = Get().moveWeights; if (w == null || string.IsNullOrEmpty(moveId)) return 1f;
        float pre = 1f;
        for (int i = 0; i < w.Length; i++)
        {
            var r = w[i]; if (r == null || string.IsNullOrEmpty(r.id)) continue;
            if (r.id == moveId) return r.mul;
            if (r.id.EndsWith("*") && moveId.StartsWith(r.id.Substring(0, r.id.Length - 1))) pre = r.mul;
        }
        return pre;
    }

    public static void Reload() { data = null; Load(); }

    static void Load()
    {
        string json = null;
        try
        {
            var ta = Resources.Load<TextAsset>("camera_impact");
            if (ta != null) json = ta.text;
            if (json == null)
            {
                string p = Path.Combine(Application.streamingAssetsPath, "camera_impact.json");
                if (File.Exists(p)) json = File.ReadAllText(p);
            }
            if (json == null)
            {
                string p = Path.Combine(Application.dataPath, "Data/camera_impact.json");
                if (File.Exists(p)) json = File.ReadAllText(p);
            }
        }
        catch (Exception ex) { Debug.LogWarning("ImpactTable: read failed: " + ex.Message); }

        if (string.IsNullOrEmpty(json)) { Debug.LogError("ImpactTable: camera_impact.json not found; camera impact layer disabled."); data = new Data(); return; }
        try
        {
            data = JsonUtility.FromJson<Data>(json);
            if (data == null) data = new Data();
            if (data.contacts == null) data.contacts = new Contact[0];
            if (data.baseKick == null) data.baseKick = new BaseKick[0];
            if (data.feel == null) data.feel = new Feel();
            if (data.rollTumble == null) data.rollTumble = new RollTumble();
            if (data.moveWeights == null) data.moveWeights = new MoveWeight[0];
        }
        catch (Exception ex) { Debug.LogError("ImpactTable: parse failed: " + ex.Message); data = new Data(); }
    }
}

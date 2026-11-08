using System;
using System.IO;
using UnityEngine;

// HITPHYS v1 (lane HITPHYS): live hit physics rows = per HIT TYPE (slash / thrust / stab / maul / claw / bash / kick) x per FIGHTER (mass, reaction, attacker punch) x ZONE (the HitReact zone rows).
// PRESENTATION ONLY. This file only READS: a CombatEvent copy (struct), DuelFighter.CharacterId, and its own json (hit_physics.json). It writes nothing but its own static config.
// The scaling is applied inside HitReact (Scripts/Physics/HitReactPhys.cs) to the proxy bodies / bone ROTATIONS, never to DuelFighter, Fighter, CharacterController or an Animator parameter. No UnityEngine.Random, no colliders.
// Data: hit_physics.json (StreamingAssets + Assets/Data, identical). enabled=false / file missing = HitReact exactly as before.

[Serializable] public class HpSplit { public string part = ""; public float share = 1f, blend = 0.5f; }   // extra push (share of the base impulse) + how much of that part's proxy pose shows
[Serializable]
public class HpType
{
    public string id = "", about = "";
    public float impulse = 1f, twist = 1f, lift = 1f, dip = 1f, blend = 0.35f, holdS = 0.08f, backS = 0.17f, pointLateralM = 0f, stepWeight = 1f, extraLift = 0.1f;
    public bool atPoint; public HpSplit[] extra = new HpSplit[0];
}
[Serializable] public class HpMove { public string id = "", type = ""; }
[Serializable] public class HpContains { public string contains = "", type = ""; }
[Serializable] public class HpClass { public string name = ""; public float impulse = 1f, blend = 1f, hold = 1f; }
[Serializable] public class HpClashKind { public string name = ""; public float mul = 1f; }
[Serializable]
public class HpFighter
{
    public string id = "", forceType = "", about = "";
    public string[] forceOverrides = new string[0];   // exact-move types the forceType replaces (claw users: every cut/thrust/stab is a claw; bash and kick stay)
    public float massKg = 0f, reactMul = 1f, blendMul = 1f, punch = 1f, clashMul = 1f;
    public bool step; public float stepThreshold = 0.5f, stepImpulse = 20f, stepLift = 2f, stepBlend = 0.5f, stepDurS = 0.3f;
    public string stepPart = "thigh$far", stepShin = "shin$far", stepFoot = "foot$far";
    public float stepEaseS = 0.04f, stepHoldFrac = 0.4f, stepThighDip = 0.3f, stepShinDip = 0.4f, stepFootDip = 0.5f, stepShinShare = 0.8f, stepFootShare = 0.5f;
}
[Serializable]
public class HpCfg
{
    public bool enabled, zoneRows = true;
    public float refMassKg = 66.7f, massExponent = 0.75f, blendExponent = 0.5f, minMassScale = 0.05f, maxMassScale = 1.6f, blendFloor = 0.05f, blendMax = 0.5f, durMinS = 0.15f, durMaxS = 0.35f, maxImpulseMul = 3f, dipMax = 1.5f;
    public string defaultType = "slash", defaultFighter = "*";
    public HpType[] types = new HpType[0]; public HpMove[] moves = new HpMove[0]; public HpContains[] moveContains = new HpContains[0];
    public HpClass[] classes = new HpClass[0]; public HpFighter[] fighters = new HpFighter[0]; public HpClashKind[] clash = new HpClashKind[0]; public KbCfg knockback = new KbCfg();   // KNOCKBACK v1 (HitKnockback.cs)
}
[Serializable] class HpFile { public HpCfg hitPhysics = new HpCfg(); }

public static class HitPhysics
{
    const string FileName = "hit_physics.json", DataDir = "Data";
    static HpCfg cfg;
    public static HpCfg Cfg { get { if (cfg == null) cfg = Load(); return cfg; } }
    public static bool On { get { return Cfg.enabled; } }
    public static void Reload() { cfg = null; }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetStatics() { cfg = null; }

    static HpCfg Load()
    {
        var d = new HpCfg();   // class defaults = disabled
        string json = null;
        string[] paths = { Path.Combine(Application.streamingAssetsPath, FileName), Path.Combine(Path.Combine(Application.dataPath, DataDir), FileName) };
        for (int i = 0; i < paths.Length && json == null; i++)
        {
            try { if (File.Exists(paths[i])) json = File.ReadAllText(paths[i]).TrimStart('﻿'); }
            catch (Exception ex) { Debug.LogWarning("[HitPhysics] cannot read " + paths[i] + ": " + ex.Message); }
        }
        if (string.IsNullOrEmpty(json)) return d;
        try
        {
            var f = JsonUtility.FromJson<HpFile>(json); if (f == null || f.hitPhysics == null) return d;
            var c = f.hitPhysics;
            if (c.fighters != null) for (int i = 0; i < c.fighters.Length; i++) if (c.fighters[i] != null && c.fighters[i].forceOverrides == null) c.fighters[i].forceOverrides = new string[0];
            if (c.types == null) c.types = new HpType[0]; if (c.moves == null) c.moves = new HpMove[0]; if (c.moveContains == null) c.moveContains = new HpContains[0];
            if (c.knockback == null) c.knockback = new KbCfg();   // KNOCKBACK v1
            if (c.classes == null) c.classes = new HpClass[0]; if (c.fighters == null) c.fighters = new HpFighter[0]; if (c.clash == null) c.clash = new HpClashKind[0];
            for (int i = 0; i < c.types.Length; i++) { if (c.types[i] == null) c.types[i] = new HpType(); if (c.types[i].extra == null) c.types[i].extra = new HpSplit[0]; }
            return c;
        }
        catch (Exception ex) { Debug.LogError("[HitPhysics] parse failed: " + ex.Message); return d; }
    }

    public static HpType Type(string id)
    {
        var t = Cfg.types;
        for (int i = 0; i < t.Length; i++) if (t[i] != null && t[i].id == id) return t[i];
        return null;
    }

    public static HpFighter Fighter(string id)
    {
        var c = Cfg; HpFighter dflt = null;
        for (int i = 0; i < c.fighters.Length; i++)
        {
            var f = c.fighters[i]; if (f == null) continue;
            if (!string.IsNullOrEmpty(id) && f.id == id) return f;
            if (f.id == c.defaultFighter) dflt = f;
        }
        return dflt ?? new HpFighter();   // only reached with a data file that has no default row (once per hit, not per frame)
    }

    public static HpClass Class(string name)
    {
        var cs = Cfg.classes;
        for (int i = 0; i < cs.Length; i++) if (cs[i] != null && cs[i].name == name) return cs[i];
        return null;
    }

    /// <summary>Hit type of a landed blow: exact move id, then the attacker's forced type (Pitborn = maul, Sobek = claw), then a move-id substring, then the default.</summary>
    public static HpType TypeFor(string moveId, HpFighter attacker)
    {
        var c = Cfg;
        if (!string.IsNullOrEmpty(moveId))
            for (int i = 0; i < c.moves.Length; i++) if (c.moves[i] != null && c.moves[i].id == moveId)
            {
                if (attacker != null && !string.IsNullOrEmpty(attacker.forceType) && attacker.forceOverrides != null)
                    for (int k = 0; k < attacker.forceOverrides.Length; k++) if (attacker.forceOverrides[k] == c.moves[i].type) { var fo = Type(attacker.forceType); if (fo != null) return fo; }
                return Type(c.moves[i].type);
            }
        if (attacker != null && !string.IsNullOrEmpty(attacker.forceType)) { var ft = Type(attacker.forceType); if (ft != null) return ft; }
        if (!string.IsNullOrEmpty(moveId))
            for (int i = 0; i < c.moveContains.Length; i++) if (c.moveContains[i] != null && !string.IsNullOrEmpty(c.moveContains[i].contains) && moveId.IndexOf(c.moveContains[i].contains, StringComparison.Ordinal) >= 0) return Type(c.moveContains[i].type);
        return Type(c.defaultType);
    }

    /// <summary>(refMass / mass)^massExponent, clamped: Joan (66.7 kg) 1, Sobek (900 kg) barely moves.</summary>
    public static float MassScale(HpFighter f)
    {
        var c = Cfg; float m = f != null && f.massKg > 0f ? f.massKg : c.refMassKg;
        return Mathf.Clamp(Mathf.Pow(c.refMassKg / Mathf.Max(Mathf.Epsilon, m), c.massExponent), c.minMassScale, c.maxMassScale);
    }

    public static float ClashKind(string kind)
    {
        var k = Cfg.clash;
        for (int i = 0; i < k.Length; i++) if (k[i] != null && k[i].name == kind) return k[i].mul;
        return 1f;
    }
}

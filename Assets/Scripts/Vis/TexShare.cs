using System;
using UnityEngine;

// TEXSHARE v1 RETIRED (C6, fk-small items): TEXDEDUPE v2 (TexDedupe.cs, data texture_dedupe.json) replaced this per-load GPU-copy share. The class stays as a no-op stub so the five call sites
// (GearAssetLoader, HubNpcs, AmbientHorses, MountRider, MemLog) keep compiling; Apply / Tick do nothing and On is always false, so texture_share.json is never read and nothing runs.
// To delete the stub later: remove the TexShare.* lines in those five files (7 lines), then this file and texture_share.json. Revert of this stub = copy orig\TexShare.cs back.
public static class TexShare
{
    public static void LoadCfg() { }
    public static bool On { get { return false; } }
    public static void Apply(GameObject root, string file) { }
    public static void Tick() { }
}

using System;
using System.Collections;
using System.IO;
using UnityEngine;

// TEXSTREAM v1 (Strategy GO 17 Nov, unitydev). World / prop / terrain textures carry the importer flag streamingMipmaps (TexStreamBatch); this switches the runtime side on from DATA (texture_streaming.json, revert = "enabled": false, no rebake).
// Why: a Frankendom player holds ~9 GB of textures, all resident. With mip streaming the resident mips follow the camera and the memory budget.
// Safety (Lead 15:00): nothing near Joan may sit below full mip. So streaming starts with Texture.streamingTextureForceLoadAll for forceLoadAllS seconds at boot and again after every zone crossing / scene load
// (the lowest mips arrive first on a fresh load, which would show as flat colour for a moment), and maxLevelReduction is 1.
// Log: "[TexStream] ..." lines (settings, force-load windows, texNow / streaming counters every logEveryS).
public sealed class TexStream : MonoBehaviour
{
    [Serializable] public class Cfg
    {
        public bool enabled = false;
        public float budgetMB = 6144f;
        public int maxLevelReduction = 1;
        public float forceLoadAllS = 8f, forceLoadAfterZoneS = 8f;
        public float logEveryS = 20f, hitchForceMs = 150f;   // a frame this long = a zone load / respawn / fast travel: force full mips again
        public bool addAllCameras = true;
    }
    static Cfg cfg; static TexStream inst; float forceUntil, nextLog;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try
        {
            if (inst != null) return;
            foreach (var p in DataPath.Candidates("texture_streaming.json"))
            {
                if (!File.Exists(p)) continue;
                try { cfg = JsonUtility.FromJson<Cfg>(DataPath.ReadFile(p).TrimStart('﻿')); } catch (Exception e) { Debug.LogWarning("[TexStream] " + p + ": " + e.Message); }
                break;
            }
            foreach (var a in Environment.GetCommandLineArgs()) { if (a == "-texstream:off") { if (cfg != null) cfg.enabled = false; } else if (a == "-texstream:on" && cfg != null) cfg.enabled = true; }
            if (cfg == null || !cfg.enabled) { Debug.Log("[TexStream] off (data)"); return; }
            QualitySettings.streamingMipmapsActive = true;
            QualitySettings.streamingMipmapsMemoryBudget = cfg.budgetMB;
            QualitySettings.streamingMipmapsMaxLevelReduction = Mathf.Max(0, cfg.maxLevelReduction);
            QualitySettings.streamingMipmapsAddAllCameras = cfg.addAllCameras;
            var go = new GameObject("TexStream"); DontDestroyOnLoad(go); inst = go.AddComponent<TexStream>();
            inst.Force(cfg.forceLoadAllS, "boot");
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) => { if (inst != null) inst.Force(cfg.forceLoadAfterZoneS, "scene " + s.name); };
            Debug.Log("[TexStream] on: budget " + cfg.budgetMB + " MB, maxLevelReduction " + cfg.maxLevelReduction + ", forceLoadAll " + cfg.forceLoadAllS + " s at boot / " + cfg.forceLoadAfterZoneS + " s after a zone crossing, addAllCameras " + cfg.addAllCameras);
        }
        catch (Exception e) { Debug.LogWarning("[TexStream] failed: " + e.Message); }
    }

    void Force(float s, string why) { forceUntil = Mathf.Max(forceUntil, Time.realtimeSinceStartup + s); Texture.streamingTextureForceLoadAll = true; Debug.Log("[TexStream] force load all mips " + s.ToString("F0") + " s (" + why + ")"); }

    void Update()
    {
        if (cfg.hitchForceMs > 0f && Time.unscaledDeltaTime * 1000f >= cfg.hitchForceMs && Time.realtimeSinceStartup > 5f) Force(cfg.forceLoadAfterZoneS, "frame " + (Time.unscaledDeltaTime * 1000f).ToString("F0") + " ms (zone load / respawn)");
        if (Texture.streamingTextureForceLoadAll && Time.realtimeSinceStartup >= forceUntil) { Texture.streamingTextureForceLoadAll = false; Debug.Log("[TexStream] force load released"); }
        if (cfg.logEveryS > 0f && Time.realtimeSinceStartup >= nextLog)
        {
            nextLog = Time.realtimeSinceStartup + cfg.logEveryS;
            Debug.Log("[TexStream] t=" + Time.realtimeSinceStartup.ToString("F0") + "s desired " + (Texture.desiredTextureMemory / 1048576L) + " MB target " + (Texture.targetTextureMemory / 1048576L) + " MB current " + (Texture.currentTextureMemory / 1048576L) + " MB nonStreaming " + (Texture.nonStreamingTextureMemory / 1048576L) + " MB loading " + Texture.streamingTextureLoadingCount + " pending " + Texture.streamingTexturePendingLoadCount);
        }
    }
}

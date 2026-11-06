// laneTEXCOMP v1 (laneOPT2 v104 kit: BC5 normals, skipped textures made non-readable again): block-compress the textures of a glTF/GLB at (design-time) import so the player ships BC7 instead of RGBA32.
// Called from GltfImporter.OnImportAsset (patched copy) after m_Gltf.Load and before the textures are added as sub-assets.
// ALL numbers/names are data: Assets/Data/gltf_texture_compress.json (enabled=false by default -> import is unchanged).
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GLTFast.Editor
{
    static class GltfTextureCompress
    {
        const string ConfigPath = "Assets/Data/gltf_texture_compress.json";

        [Serializable]
        class Config
        {
            public bool enabled;
            public string quality;                 // Fast | Normal | Best  (TextureCompressionQuality)
            public string colorFormat;             // BC7 | BC1 | BC3 ... any TextureFormat name; "" = leave
            public string normalFormat;            // BC7 (default, 1 B/px, no unpack risk) | BC5 (RG only, needs the HDRP RG/AG unpack)
            public string maskFormat;              // BC7 | BC1 | BC4 (single-channel only)
            public int minSize;                    // skip textures with min(w,h) below this
            public string[] normalProps;           // material properties that hold normal maps
            public string[] excludePathContains;   // GLB asset paths containing any of these are left untouched
            public string[] includePathContains;   // if non-empty, ONLY GLB paths containing one of these are processed
            public string[] excludeTextureNameContains;
            public bool logEach;
        }

        static Config s_Cfg;
        static DateTime s_CfgStamp;

        static Config Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return null;
                var stamp = File.GetLastWriteTimeUtc(ConfigPath);
                if (s_Cfg == null || stamp != s_CfgStamp)
                {
                    s_Cfg = JsonUtility.FromJson<Config>(File.ReadAllText(ConfigPath));
                    s_CfgStamp = stamp;
                }
                return s_Cfg;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[GltfTexComp] cannot read " + ConfigPath + ": " + e.Message);
                return null;
            }
        }

        static bool Contains(string path, string[] list)
        {
            if (list == null || string.IsNullOrEmpty(path)) return false;
            foreach (var s in list)
                if (!string.IsNullOrEmpty(s) && path.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // True when this GLB will be compressed: the importer then loads its textures readable (needed by CompressTexture).
        internal static bool Wants(string assetPath)
        {
            var c = Load();
            if (c == null || !c.enabled) return false;
            if (Contains(assetPath, c.excludePathContains)) return false;
            if (c.includePathContains != null && c.includePathContains.Length > 0 && !Contains(assetPath, c.includePathContains)) return false;
            return true;
        }

        static bool TryFormat(string name, out TextureFormat f)
        {
            f = TextureFormat.BC7;
            return !string.IsNullOrEmpty(name) && Enum.TryParse(name, out f);
        }

        internal static void Process(GltfImport gltf, string assetPath)
        {
            if (!Wants(assetPath)) return;
            var c = Load();
            TextureCompressionQuality q;
            if (!Enum.TryParse(c.quality, out q)) q = TextureCompressionQuality.Normal;

            // role per texture object, from the materials that use it (normal maps are found by property name)
            var normals = new HashSet<Texture>();
            if (c.normalProps != null)
            {
                for (var m = 0; m < gltf.MaterialCount; m++)
                {
                    var mat = gltf.GetMaterial(m);
                    if (mat == null) continue;
                    foreach (var p in c.normalProps)
                    {
                        if (string.IsNullOrEmpty(p) || !mat.HasProperty(p)) continue;
                        var t = mat.GetTexture(p);
                        if (t != null) normals.Add(t);
                    }
                }
            }

            double before = 0, after = 0; int done = 0, skipped = 0;
            for (var i = 0; i < gltf.TextureCount; i++)
            {
                var tex = gltf.GetTexture(i);
                if (tex == null) continue;
                if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(tex))) continue;     // external file: has its own TextureImporter
                if (Contains(tex.name, c.excludeTextureNameContains)) { if (tex.isReadable) tex.Apply(false, true); skipped++; continue; }
                if (GraphicsFormatUtility.IsCompressedFormat(tex.format)) continue;        // already block-compressed
                if (!tex.isReadable) { Debug.LogWarning("[GltfTexComp] " + assetPath + " texture " + tex.name + " not readable, left RGBA32"); skipped++; continue; }
                if (tex.width % 4 != 0 || tex.height % 4 != 0 || Math.Min(tex.width, tex.height) < Math.Max(4, c.minSize)) { tex.Apply(false, true); skipped++; continue; }

                var isNormal = normals.Contains(tex);
                var fmtName = isNormal ? c.normalFormat : (tex.isDataSRGB ? c.colorFormat : c.maskFormat);
                TextureFormat fmt;
                if (!TryFormat(fmtName, out fmt)) { tex.Apply(false, true); skipped++; continue; }

                var bytesBefore = (double)tex.GetRawTextureData<byte>().Length;   // TEXCHECK v1: stored mip-chain bytes (what the player build ships), not editor runtime memory
                EditorUtility.CompressTexture(tex, fmt, q);       // keeps mips, sRGB flag, wrap and filter
                var bytesAfter = (double)tex.GetRawTextureData<byte>().Length;    // TEXCHECK v1: measured while still readable, before Apply
                tex.Apply(false, true);                           // not readable in the player (glTFast default)
                before += bytesBefore; after += bytesAfter; done++;
                if (c.logEach) Debug.Log("[GltfTexComp] " + assetPath + " " + tex.name + " " + tex.width + "x" + tex.height + (tex.isDataSRGB ? " sRGB" : " linear") + (isNormal ? " normal" : "") + " -> " + fmt);
            }
            if (done + skipped > 0)
                Debug.Log("[GltfTexComp] " + assetPath + ": compressed " + done + ", skipped " + skipped + ", " + (before / 1048576.0).ToString("F1") + " MB -> " + (after / 1048576.0).ToString("F1") + " MB");
        }
    }
}

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// Import settings for the gateway art (Assets/VFX/Gateway/*) from Assets/Data/gateway_import.json (data layer: no numbers here). Same pattern as FurnaceFlipbookImport:
// first rule whose "match" is contained in the asset path wins; applied in OnPreprocessTexture, so a reimport (right click > Reimport) applies changed rules.
public class GatewayImport : AssetPostprocessor
{
    const string RulesPath = "Assets/Data/gateway_import.json";
    const string Prefix = "Assets/VFX/Gateway/";

    [Serializable] public class Rule { public string match, compression, wrapU, wrapV; public bool sRGB, mips, alphaIsTransparency, readable; public int maxSize, aniso; }
    [Serializable] public class Rules { public Rule[] rules; }

    void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith(Prefix, StringComparison.Ordinal) || !File.Exists(RulesPath)) return;
        var R = JsonUtility.FromJson<Rules>(File.ReadAllText(RulesPath));
        var ti = assetImporter as TextureImporter;
        if (R == null || R.rules == null || ti == null) return;
        foreach (var r in R.rules)
        {
            if (string.IsNullOrEmpty(r.match) || !assetPath.Contains(r.match)) continue;
            ti.textureType = TextureImporterType.Default;
            ti.textureShape = TextureImporterShape.Texture2D;
            ti.sRGBTexture = r.sRGB;
            ti.mipmapEnabled = r.mips;
            ti.alphaIsTransparency = r.alphaIsTransparency;
            ti.isReadable = r.readable;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = r.aniso;
            ti.wrapModeU = r.wrapU == "Clamp" ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            ti.wrapModeV = r.wrapV == "Repeat" ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            ti.maxTextureSize = r.maxSize > 0 ? r.maxSize : 4096;
            TextureImporterCompression comp;
            ti.textureCompression = Enum.TryParse(r.compression, out comp) ? comp : TextureImporterCompression.Uncompressed;
            return;
        }
    }
}

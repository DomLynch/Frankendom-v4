using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// Applies import settings to the Unreal-baked furnace flipbooks (Assets/VFX/Flipbooks/ue_*) from Assets/Data/furnace_flipbook_import.json (data layer: no numbers here).
// Rule match = first rule whose "match" string is contained in the asset path. Drop this file into Assets/Editor/ (new file, edits nothing else).
// Needs a reimport of the ue_* textures once after it compiles (right click > Reimport, or delete nothing: the settings are applied in OnPreprocessTexture).
public class FurnaceFlipbookImport : AssetPostprocessor
{
    const string RulesPath = "Assets/Data/furnace_flipbook_import.json";
    const string Prefix = "Assets/VFX/Flipbooks/ue_";

    [Serializable] public class Rule { public string match, compression, wrap; public bool sRGB, mips, alphaIsTransparency; public int maxSize; }
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
            ti.sRGBTexture = r.sRGB;
            ti.mipmapEnabled = r.mips;
            ti.alphaIsTransparency = r.alphaIsTransparency;
            ti.isReadable = false;
            ti.filterMode = FilterMode.Bilinear;
            ti.wrapMode = r.wrap == "Repeat" ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            ti.maxTextureSize = r.maxSize > 0 ? r.maxSize : 4096;
            TextureImporterCompression comp;
            ti.textureCompression = Enum.TryParse(r.compression, out comp) ? comp : TextureImporterCompression.Uncompressed;
            return;
        }
    }
}

using UnityEditor;
using UnityEngine;

// v69 (B1 tier a): HDRP/Decal must be in Always Included Shaders or the player has no decal shader (WoundDecals fell back to floating quads in v67).
// Idempotent; called from HdrpBatch.V51 before the build. Logs "[AlwaysInclude] ...".
public static class AlwaysIncludeShaders
{
    static readonly string[] Names = { "HDRP/Decal" };

    public static int RunBatch()
    {
        int added = 0;
        try
        {
            var gs = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
            if (gs == null) { Debug.LogWarning("[AlwaysInclude] GraphicsSettings.asset not loadable"); return 0; }
            var so = new SerializedObject(gs);
            var arr = so.FindProperty("m_AlwaysIncludedShaders");
            if (arr == null) { Debug.LogWarning("[AlwaysInclude] m_AlwaysIncludedShaders not found"); return 0; }
            foreach (var n in Names)
            {
                var sh = Shader.Find(n);
                if (sh == null) { Debug.LogWarning("[AlwaysInclude] shader not found: " + n); continue; }
                bool have = false;
                for (int i = 0; i < arr.arraySize; i++) if (arr.GetArrayElementAtIndex(i).objectReferenceValue == sh) { have = true; break; }
                if (have) continue;
                arr.arraySize++; arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh; added++;
            }
            if (added > 0) { so.ApplyModifiedProperties(); AssetDatabase.SaveAssets(); }
            Debug.Log("[AlwaysInclude] added " + added + " shader(s); list size " + arr.arraySize);
        }
        catch (System.Exception e) { Debug.LogWarning("[AlwaysInclude] failed: " + e.Message); }
        return added;
    }
}

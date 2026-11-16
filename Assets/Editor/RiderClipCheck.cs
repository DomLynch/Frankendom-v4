using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

// RIDERCHECK (unity-dev, 2026-11-16): verify a rider clip IN UNITY in about a minute, with no scene and no player build.
// Batch: -executeMethod RiderClipCheck.Run  (no -quit needed beyond the usual). Instantiates Joan_combat.glb (the same skeleton the game uses), samples each rider clip at 12 points of its loop and logs the
// world position of both hands, the hips and the head RELATIVE TO THE ROOT, so the hand height / reach can be compared with what the clip author measured (OpenTrident: (0.10, 1.10, 0.34) root-relative).
// Clips checked: every .anim under Assets/Resources/RiderClips (new bake path) and Assets/Characters/Joan/RiderClips (old controller-state bake).
public static class RiderClipCheck
{
    static Transform Find(Transform t, string n) { if (t.name == n) return t; foreach (Transform c in t) { var r = Find(c, n); if (r != null) return r; } return null; }
    static string V(Vector3 v) { return "(" + v.x.ToString("0.00") + ", " + v.y.ToString("0.00") + ", " + v.z.ToString("0.00") + ")"; }
    public static void Run()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Characters/Joan/Joan_combat.glb"); if (model == null) { Debug.LogError("[RiderClipCheck] no Joan_combat.glb"); return; }
        var go = (GameObject)Object.Instantiate(model); go.transform.position = Vector3.zero; go.transform.rotation = Quaternion.identity;
        var lh = Find(go.transform, "mixamorig:LeftHand"); var rh = Find(go.transform, "mixamorig:RightHand"); var hips = Find(go.transform, "mixamorig:Hips"); var head = Find(go.transform, "mixamorig:Head");
        if (lh == null || rh == null) { Debug.LogError("[RiderClipCheck] hand bones not found under the model"); return; }
        var paths = new List<string>();
        foreach (var dir in new[] { "Assets/Resources/RiderClips", "Assets/Characters/Joan/RiderClips" })
            foreach (var g in AssetDatabase.FindAssets("t:AnimationClip", new[] { dir })) paths.Add(AssetDatabase.GUIDToAssetPath(g));
        Debug.Log("[RiderClipCheck] clips: " + paths.Count);
        foreach (var p in paths)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(p); if (clip == null) continue; var sb = new StringBuilder("[RiderClipCheck] " + p + " len " + clip.length.ToString("0.00") + " s\n");
            float minL = 9f, maxL = -9f, minR = 9f, maxR = -9f;
            for (int i = 0; i < 12; i++)
            {
                float t = clip.length * i / 12f; clip.SampleAnimation(go, t);
                Vector3 L = go.transform.InverseTransformPoint(lh.position), R = go.transform.InverseTransformPoint(rh.position);
                minL = Mathf.Min(minL, L.y); maxL = Mathf.Max(maxL, L.y); minR = Mathf.Min(minR, R.y); maxR = Mathf.Max(maxR, R.y);
                sb.Append("  t=" + t.ToString("0.00") + " LeftHand " + V(L) + " RightHand " + V(R) + (hips != null ? " Hips " + V(go.transform.InverseTransformPoint(hips.position)) : "") + (head != null ? " Head " + V(go.transform.InverseTransformPoint(head.position)) : "") + "\n");
            }
            sb.Append("  LEFT hand height range " + minL.ToString("0.00") + " .. " + maxL.ToString("0.00") + " m, RIGHT " + minR.ToString("0.00") + " .. " + maxR.ToString("0.00") + " m"); Debug.Log(sb.ToString());
        }
        Object.DestroyImmediate(go);
    }
}

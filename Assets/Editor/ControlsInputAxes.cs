using UnityEditor;
using UnityEngine;

// Lane CTRL (v68): adds the gamepad axes the new controls need to the old Input Manager (ProjectSettings/InputManager.asset).
//  - controls.json pad.stickX / pad.stickY (default RightStickX / RightStickY): XInput on Windows, right stick X = 4th axis (stored 3), Y = 5th axis (stored 4, inverted so up is positive).
//  - DPadX (lane PARRY StanceLayer, stances.json padAxisX): XInput D-pad horizontal = 6th axis (stored 5).
// Menu Tools > Controls > Add Right Stick Axes (manual), or RunBatch() from HdrpBatch.V51 (no click, idempotent: an axis that is already there is left alone).
public static class ControlsInputAxes
{
    [MenuItem("Tools/Controls/Add Right Stick Axes")]
    public static void AddAxes() { RunBatch(); }

    // Idempotent. Safe to call on every build. Returns how many axis rows were added.
    public static int RunBatch()
    {
        string sx = "RightStickX", sy = "RightStickY";
        try { var C = ControlsTable.Get(); if (C != null && C.pad != null) { if (!string.IsNullOrEmpty(C.pad.stickX)) sx = C.pad.stickX; if (!string.IsNullOrEmpty(C.pad.stickY)) sy = C.pad.stickY; } }
        catch (System.Exception e) { Debug.LogWarning("[ControlsInputAxes] controls.json not read, default axis names: " + e.Message); }
        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/InputManager.asset");
        if (assets == null || assets.Length == 0) { Debug.LogError("[ControlsInputAxes] InputManager.asset not found"); return 0; }
        var so = new SerializedObject(assets[0]);
        var axes = so.FindProperty("m_Axes");
        int added = 0;
        if (Add(axes, sx, 3, false)) added++;
        if (Add(axes, sy, 4, true)) added++;
        if (Add(axes, "DPadX", 5, false)) added++;   // lane PARRY: StanceLayer D-pad switching (stances.json padAxisX)
        if (added > 0)
        {
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }
        Debug.Log("[ControlsInputAxes] RunBatch: added " + added + " axis rows (" + sx + ", " + sy + ", DPadX); total axes " + axes.arraySize);
        return added;
    }

    static bool Add(SerializedProperty axes, string name, int axis, bool invert)
    {
        if (string.IsNullOrEmpty(name)) return false;
        for (int i = 0; i < axes.arraySize; i++) if (axes.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name").stringValue == name) return false;
        axes.arraySize++;
        var e = axes.GetArrayElementAtIndex(axes.arraySize - 1);
        e.FindPropertyRelative("m_Name").stringValue = name;
        e.FindPropertyRelative("descriptiveName").stringValue = "";
        e.FindPropertyRelative("descriptiveNegativeName").stringValue = "";
        e.FindPropertyRelative("negativeButton").stringValue = "";
        e.FindPropertyRelative("positiveButton").stringValue = "";
        e.FindPropertyRelative("altNegativeButton").stringValue = "";
        e.FindPropertyRelative("altPositiveButton").stringValue = "";
        e.FindPropertyRelative("gravity").floatValue = 0f;
        e.FindPropertyRelative("dead").floatValue = 0.05f;
        e.FindPropertyRelative("sensitivity").floatValue = 1f;
        e.FindPropertyRelative("snap").boolValue = false;
        e.FindPropertyRelative("invert").boolValue = invert;
        e.FindPropertyRelative("type").intValue = 2;   // joystick axis
        e.FindPropertyRelative("axis").intValue = axis;
        e.FindPropertyRelative("joyNum").intValue = 0;  // all joysticks
        return true;
    }
}

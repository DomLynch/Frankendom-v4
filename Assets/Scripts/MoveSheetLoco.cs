using UnityEngine;

// Locomotion-sheet helper (lane C): with -sheet:<id> and -hide:<substring,substring> hides the matching SkinnedMeshRenderers of the sheet subject
// (e.g. -hide:cloak,coat so the legs are visible). Does nothing without both flags. Camera rows are data (a -sheetdata json), not code.
public class MoveSheetLoco : MonoBehaviour
{
    string[] names; string subject = "Dracula";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string hide = null; bool sheet = false;
        foreach (var a in System.Environment.GetCommandLineArgs())
        {
            if (a.StartsWith("-hide:")) hide = a.Substring(6);
            if (a.StartsWith("-sheet:")) sheet = true;
        }
        if (!sheet || string.IsNullOrEmpty(hide)) return;
        var g = new GameObject("MoveSheetLoco"); DontDestroyOnLoad(g);
        var c = g.AddComponent<MoveSheetLoco>(); c.names = hide.Split(',');
    }

    bool logged;
    void LateUpdate()
    {
        foreach (var r in FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include))
        {
            if (!r.transform.root.name.Contains(subject)) continue;
            string n = r.name.ToLowerInvariant();
            foreach (var h in names) if (!string.IsNullOrEmpty(h) && n.Contains(h.ToLowerInvariant()))
            { if (r.enabled && !logged) Debug.Log("[MoveSheetLoco] hiding " + r.transform.root.name + "/" + r.name); r.enabled = false; }
        }
        logged = true;
    }
}

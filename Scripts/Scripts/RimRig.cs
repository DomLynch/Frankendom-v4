using UnityEngine;

// Character rim light that stays behind and above a roster character as seen from the main camera (lane LOOK, fix 2).
// Built by HdrpFixes from hdrp_fixes.json "rimRigs" (who = "player" | "enemy" | a roster id; no character names in code).
// The light only reaches the character's renderers through its light layer (HdrpFixes sets the renderer bits), shadows off.
public class RimRig : MonoBehaviour
{
    public string who = "player";
    public float behind = 2.5f, above = 2.6f, side = 0.6f, aimHeight = 1.4f;
    Transform target; Camera cam; float nextFind;

    /// <summary>"player" / "enemy" -> the roster id; anything else is taken as a roster id.</summary>
    public static string ResolveId(string w)
    {
        if (string.IsNullOrEmpty(w) || w == "player") return Roster.PlayerId;
        if (w == "enemy") return Roster.EnemyId;
        return w;
    }

    void LateUpdate()
    {
        if (target == null || cam == null)
        {
            if (Time.unscaledTime < nextFind) return; nextFind = Time.unscaledTime + 1f;
            if (target == null) { var g = GameObject.Find(Roster.Get(ResolveId(who)).objectName); if (g != null) target = g.transform; }
            if (cam == null) cam = Camera.main;
            if (target == null || cam == null) return;
        }
        Vector3 fwd = cam.transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) { fwd = target.forward; fwd.y = 0f; }
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        Vector3 aim = target.position + Vector3.up * aimHeight;
        Vector3 pos = target.position + fwd * behind + Vector3.up * above + right * side;   // far side of the character from the camera, raised
        transform.position = pos;
        Vector3 d = aim - pos; if (d.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
    }
}

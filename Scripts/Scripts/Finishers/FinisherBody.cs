using System;
using UnityEngine;

// V61B: live body bounds of a fighter (union of its SkinnedMeshRenderers), smoothed. Cameras that must FRAME A BODY use this instead of the root transform:
// after a death clip / ragdoll the root stays where the fighter stood while the body lies elsewhere, so a root-based look-at filmed the floor.
public sealed class FinisherBody
{
    Transform root; SkinnedMeshRenderer[] smr; float refreshAt; Bounds b; bool have;
    public bool Valid { get { return have; } }
    public Bounds Bounds { get { return b; } }
    public Transform Root { get { return root; } }

    public void Set(Transform t) { if (root == t) return; root = t; smr = null; have = false; refreshAt = 0f; cfg = FinisherBodyCfg.For(t); }
    FinisherBodyCfg.Row cfg = FinisherBodyCfg.Default;   // HCKIT4 v95: per-character body bounds rows (finishers.json bodyBounds)

    // rate = smoothing per second (unscaled dt); the first call snaps
    public bool Update(float dt, float rate)
    {
        if (root == null) { have = false; return false; }
        if (smr == null || Time.unscaledTime >= refreshAt) { smr = root.GetComponentsInChildren<SkinnedMeshRenderer>(false); refreshAt = Time.unscaledTime + cfg.refreshS; }   // HCKIT4 v95
        bool any = false; Bounds raw = new Bounds(root.position, Vector3.zero);
        for (int i = 0; i < smr.Length; i++)
        {
            var r = smr[i]; if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (!any) { raw = r.bounds; any = true; } else raw.Encapsulate(r.bounds);
        }
        float sc = Mathf.Max(cfg.scaleFloor, root.lossyScale.y);   // HCKIT4 v95
        if (!any) raw = new Bounds(root.position + Vector3.up * cfg.centerYM * sc, FinisherBodyCfg.V(cfg.sizeM) * sc);   // HCKIT4 v95
        Vector3 maxSize = FinisherBodyCfg.V(cfg.maxSizeM) * sc;   // a stray huge skinned bound (cloth, a flying piece) must not drag the camera away   // HCKIT4 v95
        raw.size = Vector3.Min(raw.size, maxSize);
        if (!have) { b = raw; have = true; return true; }
        float k = 1f - Mathf.Exp(-Mathf.Max(0.1f, rate) * Mathf.Max(0f, dt));
        b.center = Vector3.Lerp(b.center, raw.center, k); b.size = Vector3.Lerp(b.size, raw.size, k);
        return true;
    }
}

// V61B: one frame of a -finisherauto contact sheet (data: finishers.json rt.auto.sheetFrames). rel: start | contact | kill | hold (t = fraction of the entry holdAfter after Die()) | release (after RTFinisher.End); t in unscaled seconds
[Serializable]
public class ContactFrame
{
    public string rel = "start"; public float t = 0f; public string label = "";
    static ContactFrame F(string rel, float t, string label) { return new ContactFrame { rel = rel, t = t, label = label }; }
    public static ContactFrame[] Defaults()
    {
        return new[] { F("start", 0.35f, "start"), F("contact", 0.03f, "contact"), F("contact", 0.5f, "afterhit"), F("contact", 1.2f, "fall"), F("kill", 0.3f, "kill"), F("hold", 0.85f, "end"), F("release", 0.8f, "after_a"), F("release", 2.5f, "after_b"), F("release", 4.0f, "after_c") };
    }
}

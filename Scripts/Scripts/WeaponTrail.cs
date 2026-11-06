using UnityEngine;

// Swing trail: starts at the release half of the windup and runs through the active arc. Ember palette (#FFD0A0 -> #FF4A1A -> #7A1200, additive) for the maul, V3 cream for the sword.
[RequireComponent(typeof(TrailRenderer))]
public class WeaponTrail : MonoBehaviour
{
    public DuelFighter owner;
    public bool ember;
    // Dom 2026-11-04: weapon trails removed (cheesy). Code stays behind the -trails launch flag; nothing renders by default.
    public static readonly bool Enabled = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-trails") >= 0;
    TrailRenderer tr;
    static Color C(string h) { ColorUtility.TryParseHtmlString(h, out var c); return c; }
    void Awake() { tr = GetComponent<TrailRenderer>(); tr.emitting = false; if (!Enabled) tr.enabled = false; }
    void Start() { Apply(); }
    public void Apply()
    {
        if (tr == null) tr = GetComponent<TrailRenderer>();
        var g = new Gradient();
        if (ember) g.SetKeys(new[] { new GradientColorKey(C("#FFD0A0"), 0f), new GradientColorKey(C("#FF4A1A"), 0.4f), new GradientColorKey(C("#7A1200"), 1f) }, new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.5f, 0.4f), new GradientAlphaKey(0f, 1f) });
        else g.SetKeys(new[] { new GradientColorKey(new Color(0.91f, 0.875f, 0.78f), 0f), new GradientColorKey(new Color(0.91f, 0.875f, 0.78f), 1f) }, new[] { new GradientAlphaKey(0.6f, 0f), new GradientAlphaKey(0f, 1f) });
        tr.colorGradient = g;
    }
    void LateUpdate()
    {
        if (!Enabled) return;
        if (!owner) { owner = GetComponentInParent<DuelFighter>(); if (!owner) return; }
        bool strike = owner.state == FState.Active || (owner.state == FState.Windup && owner.t >= 0.5f * (owner.t + owner.WindupLeft)) || (owner.state == FState.Recovery && owner.t <= 4);
        if (strike && !tr.emitting) tr.Clear();
        tr.emitting = strike;
    }
}



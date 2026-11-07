using UnityEngine;

// Swing trail: starts at the release half of the windup and runs through the active arc. Ember palette (#FFD0A0 -> #FF4A1A -> #7A1200, additive) for the maul, V3 cream for the sword.
[RequireComponent(typeof(TrailRenderer))]
public class WeaponTrail : MonoBehaviour
{
    public DuelFighter owner;
    public bool ember;
    // Dom 2026-11-04: weapon trails removed (cheesy). Code stays behind the -trails launch flag; nothing renders by default.
    static readonly bool EnabledFlag = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-trails") >= 0; public static bool Enabled { get { return EnabledFlag || ArcFx.WeaponOn; } }   // ARCS v105: arc_fx.json weapon.on also enables the soft arc
    TrailRenderer tr;
    static Color C(string h) { ColorUtility.TryParseHtmlString(h, out var c); return c; }
    void Awake() { tr = GetComponent<TrailRenderer>(); tr.emitting = false; if (!Enabled) tr.enabled = false; }
    void Start() { Apply(); }
    public void Apply()
    {
        if (tr == null) tr = GetComponent<TrailRenderer>();
        var g = new Gradient();
        var wc = ArcFx.Cfg().weapon; /* DATALAYER b12 */
        if (ember) g.SetKeys(new[] { new GradientColorKey(C(wc.emberColors[0]), wc.emberAt[0]), new GradientColorKey(C(wc.emberColors[1]), wc.emberAt[1]), new GradientColorKey(C(wc.emberColors[2]), wc.emberAt[2]) }, new[] { new GradientAlphaKey(wc.emberAlpha[0], wc.emberAt[0]), new GradientAlphaKey(wc.emberAlpha[1], wc.emberAt[1]), new GradientAlphaKey(wc.emberAlpha[2], wc.emberAt[2]) }); /* DATALAYER b12 */
        else { var pc = new Color(wc.plainColor[0], wc.plainColor[1], wc.plainColor[2]); g.SetKeys(new[] { new GradientColorKey(pc, 0f), new GradientColorKey(pc, 1f) }, new[] { new GradientAlphaKey(wc.plainAlpha[0], 0f), new GradientAlphaKey(wc.plainAlpha[1], 1f) }); } /* DATALAYER b12 */
        tr.colorGradient = g; ArcFx.StyleTrail(tr);   // ARCS v105
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



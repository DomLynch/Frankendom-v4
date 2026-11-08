using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// BOW v2 (laneBOW, STAGED DARK, UNPROVEN). Presentation of the bow mode: the bow glb on the left hand, the sword dark (WeaponSwap's own trick: the sockets' children except keepChildren), auto-aim brackets and a mode chip.
// Nothing here is read by the sim. The glb is gear.json item bow.json gearItemId (StreamingAssets/Gear/joan_bow_LOD0.glb), loaded once on the first Show(true) with the same loader and material pass the gear weapons use.
public class BowVisual : MonoBehaviour
{
    GameObject inst; bool loading, want; int serial; Transform hand, sock;
    readonly List<GameObject> hidden = new List<GameObject>();

    public void Show(bool on) { want = on; if (on && inst == null && !loading) StartCoroutine(Load(++serial)); Apply(); }
    void OnDestroy() { RestoreSword(); }

    static Transform FindNamed(Transform top, string n)
    {
        if (top == null || string.IsNullOrEmpty(n)) return null;
        foreach (var t in top.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t;
        return null;
    }

    Transform Skel() { var gs = GetComponent<GearSlots>(); return gs != null && gs.Skeleton != null ? gs.Skeleton : transform; }

    IEnumerator Load(int my)
    {
        loading = true;
        var C = BowCfg.Get(); var it = GearTable.Find(C.gearItemId);
        if (it == null) { loading = false; GearTable.Warn("[bow] gear item '" + C.gearItemId + "' is not in gear.json: the bow has no visual (shots still work)"); yield break; }
        Transform sk = Skel(); hand = FindNamed(sk, Roster.BoneName(this, C.visual.handRole));
        sock = FindNamed(sk, GearTable.Get().weaponSwap.socket);
        if (hand == null) { loading = false; GearTable.Warn("[bow] no hand bone '" + Roster.BoneName(this, C.visual.handRole) + "' on " + name); yield break; }
        GameObject g = null; IDisposable res = null; string info = "";
        yield return GearAssetLoader.Load(it, it.glb, true, hand, (o, r, i) => { g = o; res = r; info = i; });
        loading = false;
        if (this == null || my != serial && !want) { if (res != null) res.Dispose(); if (g != null) Destroy(g); yield break; }
        if (g == null) { GearTable.Warn("[bow] bow glb did not load (" + info + "): shots still work, no model"); yield break; }
        Renderer refR = null; if (sock != null) foreach (var r in sock.GetComponentsInChildren<Renderer>(true)) if (!(r is TrailRenderer)) { refR = r; break; }
        g.name = "Weapon_" + it.id; var t = g.transform; t.SetParent(hand, false);
        t.localRotation = Quaternion.Euler(C.visual.holdRxDeg, C.visual.holdRyDeg, C.visual.holdRzDeg); t.localPosition = new Vector3(C.visual.holdPx, C.visual.holdPy, C.visual.holdPz); t.localScale = Vector3.one;
        foreach (var c in g.GetComponentsInChildren<Collider>(true)) Destroy(c);
        var tag = g.GetComponent<GearPiece>(); if (tag == null) tag = g.AddComponent<GearPiece>();
        tag.itemId = it.id; tag.slotId = "weapon"; tag.resources = res;
        var rends = new List<Renderer>(g.GetComponentsInChildren<Renderer>(true));
        tag.madeMaterials = GearMaterials.Convert(rends, it);
        int layer = refR != null ? refR.gameObject.layer : hand.gameObject.layer;
        foreach (var x in g.GetComponentsInChildren<Transform>(true)) x.gameObject.layer = layer;
        foreach (var r in rends) { r.shadowCastingMode = refR != null ? refR.shadowCastingMode : UnityEngine.Rendering.ShadowCastingMode.On; r.receiveShadows = refR != null ? refR.receiveShadows : true; if (refR != null) r.renderingLayerMask = refR.renderingLayerMask; }
        inst = g; Apply();
        if (BowCfg.Log) Debug.Log("[bow] bow visual on " + name + " (" + info + ")");
    }

    void Apply()
    {
        if (inst != null) inst.SetActive(want);
        if (want) HideSword(); else RestoreSword();
    }

    void HideSword()
    {
        if (hidden.Count > 0 || sock == null) { if (sock == null) sock = FindNamed(Skel(), GearTable.Get().weaponSwap.socket); if (hidden.Count > 0 || sock == null) return; }
        var keep = GearTable.Get().weaponSwap.keepChildren;
        for (int i = 0; i < sock.childCount; i++)
        {
            var ch = sock.GetChild(i); if (Array.IndexOf(keep, ch.name) >= 0) continue;
            if (ch.gameObject.activeSelf) { ch.gameObject.SetActive(false); hidden.Add(ch.gameObject); }
        }
    }

    void RestoreSword() { for (int i = 0; i < hidden.Count; i++) if (hidden[i] != null) hidden[i].SetActive(true); hidden.Clear(); }
}

// Auto-aim brackets on the target while the bow is up, and a small mode chip. Own OnGUI host (created at boot when the master is on), so GameHUD is untouched.
public class BowHud : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { if (!BowCfg.Master || !BowCfg.Get().hud.enabled || FindFirstObjectByType<BowHud>() != null) return; new GameObject("BowHud").AddComponent<BowHud>(); }

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint || !BowCfg.Master) return;
        var H = BowCfg.Get().hud; var f = BowHooks.HudFighter(); bool bow; int ammo; DuelFighter foe;
        if (f == null || !BowHooks.TryHud(f, out bow, out ammo, out foe)) return;
        Color c = BowCfg.Col(H.colour, Color.white);
        if (bow && foe != null && !foe.Dead && Camera.main != null)
        {
            Vector3 sp = Camera.main.WorldToScreenPoint(foe.transform.position + Vector3.up * (BowCfg.Get().arrow.aimHeightM * foe.scale));
            if (sp.z > 0f)
            {
                float x = sp.x, y = Screen.height - sp.y, b = H.bracketPx, t = H.thickPx, g = H.gapPx;
                HudDraw.Fill(new Rect(x - g - b, y - g - b, b, t), c); HudDraw.Fill(new Rect(x - g - b, y - g - b, t, b), c);
                HudDraw.Fill(new Rect(x + g, y - g - b, b, t), c); HudDraw.Fill(new Rect(x + g + b - t, y - g - b, t, b), c);
                HudDraw.Fill(new Rect(x - g - b, y + g + b - t, b, t), c); HudDraw.Fill(new Rect(x - g - b, y + g, t, b), c);
                HudDraw.Fill(new Rect(x + g, y + g + b - t, b, t), c); HudDraw.Fill(new Rect(x + g + b - t, y + g, t, b), c);
            }
        }
        string chip = bow ? (BowCfg.Get().quiverOn ? "BOW  " + ammo : "BOW") : "SWORD";
        HudDraw.Text(new Rect(Screen.width - H.chipRightPx - H.chipWidthPx, Screen.height - H.chipBottomPx, H.chipWidthPx, H.chipFontPx * 1.6f), chip, "serif", H.chipFontPx, TextAnchor.MiddleRight, false, c);
    }
}

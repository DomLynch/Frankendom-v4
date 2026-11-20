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
        inst = g; PrepRig(); if (!rigOn) PrepString(); Apply();
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

    // K24 carry: the bow is rigid on the hand bone, and every idle / walk pose of the rig has the hand at the hip, so the bow hung from it there. Outside the draw / loose states (FState Windup / Active / Recovery, plus
    // visual.carry.releaseDelayTicks after) it rests on visual.carry.parentRole (a back / shoulder bone) with the carry row; on the draw it goes back to the hand with the hold row. carry.enabled false = old behaviour (always the hand).
    DuelFighter fighter; Transform carryBone; bool onHand = true, carryWarned; float lastAim = -99f;
    void LateUpdate()
    {
        if (inst == null || !want || hand == null) return;
        var K = BowCfg.Get().visual.carry; if (K == null || !K.enabled) { if (!onHand) Seat(true, K); return; }
        if (fighter == null) fighter = GetComponent<DuelFighter>(); if (fighter == null) return;
        if (archer == null) archer = GetComponent<BanditArcher>();
        if (fighter.state == FState.Windup || fighter.state == FState.Active || fighter.state == FState.Recovery) lastAim = Time.time; else if (archer != null && archer.Aiming) lastAim = Time.time;
        bool toHand = Time.time - lastAim < K.releaseDelayTicks / 60f;
        if (toHand != onHand) Seat(toHand, K);
        NpcDrawHandIk(); Nock(); if (rigOn) DrawRig(); else DrawString();
    }

    // DRAW v1: the bow glb is ONE static mesh with the string baked in. At load the thin triangles along the chord between the two limb tips are removed (bow.json visual.draw.stringRadiusM)
    // and the string is redrawn as a LineRenderer: tip -> nock point -> tip. The nock point rests at the chord middle and follows the right hand bone while the fighter draws (FState Windup),
    // and snaps back on the loose. No limb bend: the mesh is not skinned. The nocked arrow rides the nock point. Nothing here is read by the sim.
    // AIM v1: the nocked arrow points at the target (flattened: foe position, else the fighter's forward) with its tail at the nock point, whatever the bow hand does (the grip hand sits low: the shaft used to follow the bow and point at the floor).
    void PlaceNocked(Vector3 nockW)
    {
        if (nocked == null || fighter == null) return; var A = BowCfg.Get().arrow; var D = BowCfg.Get().visual.draw;
        Vector3 dir = fighter.transform.forward; dir.y = 0f;
        if (fighter.opponent != null)
        {   // toward the foe's chest, flattened but with the pitch clamped to +-maxPitchDeg (a close or high foe must not send the shaft into the floor or the sky)
            Vector3 to = fighter.opponent.transform.position + Vector3.up * (A.aimHeightM * fighter.opponent.scale) - nockW; Vector3 flat = to; flat.y = 0f;
            if (flat.sqrMagnitude > 1e-4f) { float pitch = Mathf.Clamp(Mathf.Atan2(to.y, flat.magnitude) * Mathf.Rad2Deg, -D.maxPitchDeg, D.maxPitchDeg); dir = Quaternion.AngleAxis(-pitch, Vector3.Cross(Vector3.up, flat.normalized)) * flat.normalized; }
        }
        if (dir.sqrMagnitude < 1e-4f) { dir = fighter.transform.forward; dir.y = 0f; } dir.Normalize();
        nocked.transform.rotation = Quaternion.LookRotation(dir, Vector3.up); nocked.transform.position = nockW + dir * (A.lengthM * 0.5f - D.arrowBackM);
        if (BowCfg.Log && Time.time >= nextAimLog && fighter.opponent != null)
        {   // AIMLOG v1: the body-yaw test (Strategy): dot of the fighter's forward and the flat direction to its foe (1 = facing it)
            nextAimLog = Time.time + 0.5f; Vector3 tf = fighter.opponent.transform.position - fighter.transform.position; tf.y = 0f; Vector3 ff = fighter.transform.forward; ff.y = 0f;
            Debug.Log("[bow] aim dot=" + (tf.sqrMagnitude > 1e-4f && ff.sqrMagnitude > 1e-4f ? Vector3.Dot(ff.normalized, tf.normalized).ToString("F2") : "n/a") + " arrowDot=" + Vector3.Dot(dir, tf.sqrMagnitude > 1e-4f ? tf.normalized : dir).ToString("F2") + " foe=" + fighter.opponent.name + " gap=" + tf.magnitude.ToString("F1"));
        }
    }

    // RIG v1 (OpenTrident kits_out\3d\bow_rigged\joan_bow_rigged.glb): when the bow glb is skinned (bones bow_grip, bow_up_1-3, bow_low_1-3, bow_nock; the string is its own mesh skinned to the tip bones and bow_nock)
    // the string is not touched: bow_nock goes to the right hand while the fighter draws, and the six limb bones bend about the bow's X axis by draw.bendDeg / 3 each x the draw fraction (upper +, lower -), cumulative along the limb.
    LineRenderer rigStr; bool rigStrHidden; Vector3 rigLowOff, rigUpOff;
    bool rigOn; Transform nockB; Vector3 nockRestLocal; Transform[] upB = new Transform[3], lowB = new Transform[3]; Quaternion[] upRest = new Quaternion[3], lowRest = new Quaternion[3];
    void PrepRig()
    {
        if (inst == null) return; var D = BowCfg.Get().visual.draw; if (D == null || !D.enabled) return;
        var sk = inst.GetComponentInChildren<SkinnedMeshRenderer>(true); if (sk == null) return;
        nockB = FindNamed(inst.transform, "bow_nock"); if (nockB == null) { GearTable.Warn("[bow] rig: skinned bow without a bow_nock bone: no draw"); return; }
        for (int i = 0; i < 3; i++)
        {
            upB[i] = FindNamed(inst.transform, "bow_up_" + (i + 1)); lowB[i] = FindNamed(inst.transform, "bow_low_" + (i + 1));
            if (upB[i] == null || lowB[i] == null) GearTable.Warn("[bow] rig: limb bone " + (i + 1) + " missing: no limb bend for it");
            else { upRest[i] = Quaternion.Inverse(inst.transform.rotation) * upB[i].rotation; lowRest[i] = Quaternion.Inverse(inst.transform.rotation) * lowB[i].rotation; }
        }
        nockRestLocal = nockB.localPosition; foreach (var r in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true)) r.updateWhenOffscreen = true;
        // taut string: the rig's own string mesh (skinned to the tips and bow_nock) shows a slack curve and a ribbon at the top tip when the limbs bend; hide it and draw a V line from the two string attach points (rest points of the glb string, carried by the tip bones) to the nock
        if (D.stringA != null && D.stringA.Length >= 3 && D.stringB != null && D.stringB.Length >= 3 && upB[2] != null && lowB[2] != null)
        {
            SkinnedMeshRenderer rigBiggest = null; int rigBigT = -1; var rigAll = inst.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var r in rigAll) { int tc = r.sharedMesh != null ? (int)(r.sharedMesh.GetIndexCount(0) / 3) : 0; if (tc > rigBigT) { rigBigT = tc; rigBiggest = r; } }
            foreach (var r in rigAll)
            {   // keep only the biggest skinned mesh (the limbs); every other skinned mesh of the rigged bow (the string, its strands, knots) is replaced by the drawn line
                bool hide = r != rigBiggest || r.gameObject.name.IndexOf("string", StringComparison.OrdinalIgnoreCase) >= 0;
                if (BowCfg.Log) Debug.Log("[bow] rig: renderer '" + r.gameObject.name + "' " + (r.sharedMesh != null ? (r.sharedMesh.GetIndexCount(0) / 3) : 0) + " tris -> " + (hide ? "hidden" : "kept"));
                if (hide) { r.enabled = false; rigStrHidden = true; }
            }
            // ghost strands: thin leftover string triangles in the LIMB mesh (rest pose along the string line) follow the nock to the hand and show as faint dotted lines: remove them from a copy of the limb mesh
            foreach (var r in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r.gameObject.name.IndexOf("string", StringComparison.OrdinalIgnoreCase) >= 0 || r.sharedMesh == null) continue;
                var gm = r.sharedMesh; if (!gm.isReadable) { if (BowCfg.Log) Debug.Log("[bow] rig: limb mesh not readable, ghost strands not removed"); continue; }
                var ga = new Vector3(D.stringA[0], D.stringA[1], D.stringA[2]); var gb = new Vector3(D.stringB[0], D.stringB[1], D.stringB[2]); var gv = gm.vertices; var gt = gm.triangles; var gk = new List<int>(gt.Length); int gr = 0;
                Vector3 gd = gb - ga; float gl = gd.magnitude; if (gl < 0.05f) continue; gd /= gl;
                for (int i = 0; i + 2 < gt.Length; i += 3)
                {
                    bool thin = true; for (int k = 0; k < 3 && thin; k++) { Vector3 w = gv[gt[i + k]] - ga; float t = Vector3.Dot(w, gd) / gl; float d = (w - gd * Vector3.Dot(w, gd)).magnitude; if (d > D.stringRadiusM || t < D.tipClampT || t > 1f - D.tipClampT) thin = false; }
                    if (thin) gr++; else { gk.Add(gt[i]); gk.Add(gt[i + 1]); gk.Add(gt[i + 2]); }
                }
                if (BowCfg.Log) Debug.Log("[bow] rig: ghost string triangles removed from " + r.gameObject.name + ": " + gr + " of " + (gt.Length / 3));
                if (gr > 0) { var g2 = Instantiate(gm); g2.SetTriangles(gk, 0); r.sharedMesh = g2; }
            }
            if (rigStrHidden)
            {
                var T0 = inst.transform; var pa = new Vector3(D.stringA[0], D.stringA[1], D.stringA[2]); var pb = new Vector3(D.stringB[0], D.stringB[1], D.stringB[2]);
                if (pa.y > pb.y) { var tmp = pa; pa = pb; pb = tmp; }   // pa = the lower attach point, pb = the upper
                rigLowOff = lowB[2].InverseTransformPoint(T0.TransformPoint(pa)); rigUpOff = upB[2].InverseTransformPoint(T0.TransformPoint(pb));
                rigStr = new GameObject("BowStringRig").AddComponent<LineRenderer>(); rigStr.transform.SetParent(inst.transform, false);
                rigStr.positionCount = 3; rigStr.useWorldSpace = true; rigStr.startWidth = rigStr.endWidth = D.widthM; rigStr.numCapVertices = 2; rigStr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; rigStr.alignment = LineAlignment.View;
                Shader sh = null; var shs = BowCfg.Get().arrow.shaders; for (int i = 0; shs != null && i < shs.Length && sh == null; i++) sh = Shader.Find(shs[i]);
                if (sh != null) { var mat = new Material(sh) { name = "BowStringRig" }; Color col = BowCfg.Col(D.color, Color.white); if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", col); if (mat.HasProperty("_Color")) mat.SetColor("_Color", col); if (mat.HasProperty("_UnlitColor")) mat.SetColor("_UnlitColor", col); if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.2f); rigStr.sharedMaterial = mat; }
            }
        }
        rigOn = true; if (BowCfg.Log) Debug.Log("[bow] rig: skinned bow, nock + limb bones found");
    }
    void DrawRig()
    {
        var D = BowCfg.Get().visual.draw;
        bool draw = Drawing();
        if (!draw) drawK = 0f; else drawK = Mathf.MoveTowards(drawK, 1f, Time.deltaTime / Mathf.Max(0.02f, D.drawSeconds));
        if (rHand == null) rHand = FindNamed(Skel(), Roster.BoneName(this, D.handRole));
        var T = inst.transform; float k = Mathf.SmoothStep(0f, 1f, drawK);
        Vector3 restW = nockB.parent.TransformPoint(nockRestLocal), tgtW = restW;
        if (drawK > 0f && rHand != null) tgtW = Vector3.Lerp(restW, rHand.TransformPoint(new Vector3(D.handPx, D.handPy, D.handPz)) + Vector3.down * D.dropM, k);
        nockB.position = tgtW;
        float per = D.bendDeg / 3f * k;
        for (int i = 0; i < 3; i++)
        {
            if (upB[i] != null) upB[i].rotation = T.rotation * Quaternion.AngleAxis(per * (i + 1), Vector3.right) * upRest[i];
            if (lowB[i] != null) lowB[i].rotation = T.rotation * Quaternion.AngleAxis(-per * (i + 1), Vector3.right) * lowRest[i];
        }
        if (rigStr != null) { rigStr.SetPosition(0, lowB[2].TransformPoint(rigLowOff)); rigStr.SetPosition(1, tgtW); rigStr.SetPosition(2, upB[2].TransformPoint(rigUpOff)); }
        if (nocked != null && nocked.activeSelf) PlaceNocked(tgtW);
    }

    /// <summary>NPCARCHER v2: world position of the string's nock point (follows the right hand while drawing); false when the skinned rig is not up.</summary>
    public bool NockWorld(out Vector3 p) { p = Vector3.zero; if (!rigOn || nockB == null) return false; p = nockB.position; return true; }

    // NPCARCHER v4 (COO 21:10): the drawing hand of an NPC archer ended above his head (the clip was authored on Joan's arm length). Two-bone IK on the right arm: while drawing, the hand goes to a cheek anchor in front of the right side of the face
    // (bow.json visual.draw.npcIk / cheekRightM / cheekDownM / cheekFwdM); the string and the nocked arrow follow that hand. Weighted in and out with the draw. Never runs for the player.
    Transform ikUp, ikFore, ikHand, ikHead, lUp, lFore, lHand; float ikK, lastDrawT; bool ikTried, wasHold;
    string bodyId; string BodyId() { if (bodyId == null) { string n = fighter != null ? fighter.name : ""; int h = n.IndexOf('#'); bodyId = h > 0 ? n.Substring(0, h) : n; } return bodyId; }   // K18: NPC id for the per-body draw row
    void NpcDrawHandIk()
    {
        if (fighter == null || fighter.IsPlayer) return; var D = BowCfg.DrawFor(BodyId()); if (D == null || !D.npcIk) return;
        if (!ikTried) { ikTried = true; var sk = Skel(); string pre = Roster.BoneName(this, ""); ikUp = FindNamed(sk, pre + "RightArm"); ikFore = FindNamed(sk, pre + "RightForeArm"); ikHand = FindNamed(sk, pre + "RightHand"); ikHead = FindNamed(sk, pre + "Head"); if (BowCfg.Log) Debug.Log("[bow] npc ik bones " + (ikUp != null) + "/" + (ikFore != null) + "/" + (ikHand != null) + "/" + (ikHead != null) + " on " + name); }
        if (ikUp == null || ikFore == null || ikHand == null || ikHead == null) return;
        bool drawNow = Drawing(); if (drawNow) lastDrawT = Time.time;   // RELEASEHOLD v1 (W5): keep the weight for D.holdAfterReleaseS after the loose, then ease out over D.easeOutS (defaults 0 / 0.2 = the old fixed 0.2 s ramp)
        bool holdIk = drawNow || (D.holdAfterReleaseS > 0f && lastDrawT > 0f && Time.time - lastDrawT < D.holdAfterReleaseS);
        ikK = Mathf.MoveTowards(ikK, holdIk ? 1f : 0f, Time.deltaTime / (holdIk ? 0.2f : Mathf.Max(0.02f, D.easeOutS)));
        if (BowCfg.Log && D.holdAfterReleaseS > 0f && wasHold != holdIk && !drawNow) { Debug.Log("[bow] npc release hold " + name + " " + (holdIk ? "ON" : "OFF") + " t=" + Time.time.ToString("F3") + " ikK " + ikK.ToString("F2") + " hold " + D.holdAfterReleaseS.ToString("F2") + " ease " + D.easeOutS.ToString("F2")); }
        wasHold = holdIk; if (ikK <= 0.001f) { if (D.groundHips) GroundHips(); return; }
        Vector3 fw = fighter.transform.forward; fw.y = 0f; fw.Normalize(); Vector3 rt = fighter.transform.right; rt.y = 0f; rt.Normalize();
        if (D.groundHips) GroundHips();
        if (D.spineLevel) LevelTorso(rt, D);
        Vector3 T = ikHead.position + rt * D.cheekRightM + Vector3.down * D.cheekDownM + fw * D.cheekFwdM;
        Vector3 S = ikUp.position, E = ikFore.position, W = ikHand.position; float l1 = (E - S).magnitude, l2 = (W - E).magnitude; if (l1 < 1e-3f || l2 < 1e-3f) return;
        Vector3 toT = T - S; float d = Mathf.Clamp(toT.magnitude, 0.05f, (l1 + l2) * 0.999f); Vector3 dir = toT.normalized;
        Vector3 pole = -fw + rt * 0.6f + Vector3.up * 0.2f; Vector3 perp = pole - dir * Vector3.Dot(pole, dir); if (perp.sqrMagnitude < 1e-6f) perp = rt; perp.Normalize();
        float a = (l1 * l1 - l2 * l2 + d * d) / (2f * d); float h = Mathf.Sqrt(Mathf.Max(0f, l1 * l1 - a * a)); Vector3 E2 = S + dir * a + perp * h;
        Quaternion q1 = Quaternion.FromToRotation(E - S, E2 - S); ikUp.rotation = Quaternion.Slerp(Quaternion.identity, q1, ikK) * ikUp.rotation;
        E = ikFore.position; W = ikHand.position; Quaternion q2 = Quaternion.FromToRotation(W - E, (S + dir * d) - E); ikFore.rotation = Quaternion.Slerp(Quaternion.identity, q2, ikK) * ikFore.rotation;
        if (D.bowArmIk)   // NPCARCHER v5: the bow arm hung at the hip (bow low, arrow at head height): the left hand goes straight out along the line of fire at shoulder height
        {
            if (lUp == null) { var sk2 = Skel(); string pre2 = Roster.BoneName(this, ""); lUp = FindNamed(sk2, pre2 + "LeftArm"); lFore = FindNamed(sk2, pre2 + "LeftForeArm"); lHand = FindNamed(sk2, pre2 + "LeftHand"); }
            if (lUp != null && lFore != null && lHand != null)
            {
                Vector3 S2 = lUp.position, E3 = lFore.position, W3 = lHand.position; float m1 = (E3 - S2).magnitude, m2 = (W3 - E3).magnitude;
                if (m1 > 1e-3f && m2 > 1e-3f)
                {
                    Vector3 aim = fw; if (fighter.opponent != null) { aim = fighter.opponent.transform.position - fighter.transform.position; aim.y = 0f; if (aim.sqrMagnitude < 1e-4f) aim = fw; aim.Normalize(); }
                    Vector3 T2 = S2 + aim * (m1 + m2) * 0.97f + Vector3.up * D.bowArmRaiseM; Vector3 dir2 = (T2 - S2).normalized; float d2 = Mathf.Min((T2 - S2).magnitude, (m1 + m2) * 0.999f);
                    Vector3 pole2 = -rt * 0.4f + Vector3.down * 0.6f; Vector3 perp2 = pole2 - dir2 * Vector3.Dot(pole2, dir2); if (perp2.sqrMagnitude < 1e-6f) perp2 = Vector3.down; perp2.Normalize();
                    float a2 = (m1 * m1 - m2 * m2 + d2 * d2) / (2f * d2); float h2 = Mathf.Sqrt(Mathf.Max(0f, m1 * m1 - a2 * a2)); Vector3 E4 = S2 + dir2 * a2 + perp2 * h2;
                    lUp.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(E3 - S2, E4 - S2), ikK) * lUp.rotation;
                    E3 = lFore.position; W3 = lHand.position; lFore.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(W3 - E3, (S2 + dir2 * d2) - E3), ikK) * lFore.rotation;
                }
            }
        }
    }
    // NPCARCHER v6 (COO 21:25): while drawing, the NPC leaned back with the head tilted to the sky. Level the spine (lean about the character's right axis to bow.json visual.draw.leanTargetDeg, forward positive) and the head (pitch to 0), weighted by the draw, before the arm IK reads the bones.
    Transform lvHips, lvSp1, lvSp2, lvNeck; bool lvTried;
    void LevelTorso(Vector3 rt, BowCfg.Draw D)
    {
        if (!lvTried) { lvTried = true; var sk = Skel(); string pre = Roster.BoneName(this, ""); lvHips = FindNamed(sk, pre + "Hips"); lvSp1 = FindNamed(sk, pre + "Spine1"); lvSp2 = FindNamed(sk, pre + "Spine2"); lvNeck = FindNamed(sk, pre + "Neck"); }
        float w = ikK * D.spineLevelW;
        if (lvHips != null && lvSp1 != null && lvSp2 != null)
        {
            float lean = Vector3.SignedAngle(Vector3.up, lvSp2.position - lvHips.position, rt); float corr = (D.leanTargetDeg - lean) * w;
            lvSp1.rotation = Quaternion.AngleAxis(corr * 0.5f, rt) * lvSp1.rotation; lvSp2.rotation = Quaternion.AngleAxis(corr * 0.5f, rt) * lvSp2.rotation;
        }
        if (lvNeck != null && ikHead != null)
        {
            float hp = Vector3.SignedAngle(Vector3.up, ikHead.position - lvNeck.position, rt); ikHead.rotation = Quaternion.AngleAxis((D.headPitchDeg - hp) * w, rt) * ikHead.rotation;
        }
    }
    // NPCARCHER v7 (COO 21:35): the Cedar floated ~0.5 m in the hold (the clip's hips height does not match the body). While no draw runs the lowest foot height above the fighter's root is remembered; while drawing, the hips are lowered by whatever the feet are
    // higher than that (bow.json visual.draw.groundHips), so the feet stay on the ground (the shadow). Upper-body IK only, no change to the root transform.
    Transform gfL, gfR; float footRest = float.NaN, nextGhLog; bool gfTried;
    void GroundHips()
    {
        if (!gfTried) { gfTried = true; var sk = Skel(); string pre = Roster.BoneName(this, ""); gfL = FindNamed(sk, pre + "LeftFoot"); gfR = FindNamed(sk, pre + "RightFoot"); if (lvHips == null) lvHips = FindNamed(sk, pre + "Hips"); }
        if (gfL == null || gfR == null || lvHips == null) return;
        float rootY = fighter.transform.position.y; float foot = Mathf.Min(gfL.position.y, gfR.position.y) - rootY;
        if (ikK <= 0.001f) { footRest = float.IsNaN(footRest) ? foot : Mathf.Lerp(footRest, foot, 0.2f); return; }
        if (float.IsNaN(footRest)) return;
        float up = foot - footRest; if (BowCfg.Log && Time.time >= nextGhLog) { nextGhLog = Time.time + 0.5f; Debug.Log("[bow] ground hips " + name + " rootY " + rootY.ToString("F2") + " foot " + foot.ToString("F2") + " rest " + footRest.ToString("F2") + " up " + up.ToString("F2") + " hipsY " + lvHips.position.y.ToString("F2")); } if (up > 0.02f) lvHips.position -= Vector3.up * up * ikK;
    }
    float nextAimLog;
    BanditArcher archer;
    bool Drawing() { return fighter != null && ((fighter.state == FState.Windup && BowHooks.Handles(fighter, fighter.move)) || (archer != null && archer.Aiming)); }
    LineRenderer strLr; Vector3 tipA, tipB; bool strOk; float drawK; Transform rHand;
    void PrepString()
    {
        var D = BowCfg.Get().visual.draw; if (D == null || !D.enabled || inst == null || strOk) return;
        try
        {
            foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh; if (mesh == null) continue;
                if (!mesh.isReadable) { GearTable.Warn("[bow] draw: the bow mesh is not readable: the baked string stays, no draw"); return; }
                var M = inst.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix; var vs = mesh.vertices; var lv = new Vector3[vs.Length];
                Vector3 mn = new Vector3(1e9f, 1e9f, 1e9f), mx = -mn; for (int i = 0; i < vs.Length; i++) { lv[i] = M.MultiplyPoint3x4(vs[i]); mn = Vector3.Min(mn, lv[i]); mx = Vector3.Max(mx, lv[i]); }
                Vector3 ext = mx - mn; int ax = ext.x >= ext.y && ext.x >= ext.z ? 0 : (ext.y >= ext.z ? 1 : 2); float lo = mn[ax], len = Mathf.Max(1e-4f, ext[ax]);
                // the string's two nock points are bow.json data in the glb's own frame; glTFast flips an axis on import, so try the 8 sign combinations in MESH space and keep the one that sits on the most thin triangles
                if (D.stringA == null || D.stringA.Length < 3 || D.stringB == null || D.stringB.Length < 3) return;
                var tris = mesh.triangles; Vector3 pa0 = new Vector3(D.stringA[0], D.stringA[1], D.stringA[2]), pb0 = new Vector3(D.stringB[0], D.stringB[1], D.stringB[2]);
                int bestN = -1; Vector3 bestA = pa0, bestB = pb0;
                for (int sgn = 0; sgn < 8; sgn++)
                {
                    var sg = new Vector3((sgn & 1) != 0 ? -1f : 1f, (sgn & 2) != 0 ? -1f : 1f, (sgn & 4) != 0 ? -1f : 1f); Vector3 a = Vector3.Scale(pa0, sg), b = Vector3.Scale(pb0, sg), ab = b - a; float abl = ab.magnitude; if (abl < 0.05f) continue; Vector3 ad = ab / abl; int n = 0;
                    for (int i = 0; i + 2 < tris.Length; i += 3) { bool ok = true; for (int k = 0; k < 3 && ok; k++) { Vector3 w = vs[tris[i + k]] - a; float t = Vector3.Dot(w, ad) / abl; float d = (w - ad * Vector3.Dot(w, ad)).magnitude; if (d > D.stringRadiusM || t < -0.02f || t > 1.02f) ok = false; } if (ok) n++; }
                    if (n > bestN) { bestN = n; bestA = a; bestB = b; }
                }
                if (bestN < 10) { if (BowCfg.Log) Debug.Log("[bow] draw: no string found at the data points (best " + bestN + " triangles): the baked string stays"); return; }
                tipA = M.MultiplyPoint3x4(bestA); tipB = M.MultiplyPoint3x4(bestB);
                var keep = new List<int>(tris.Length); int removed = 0; { Vector3 ab = bestB - bestA; float abl = ab.magnitude; Vector3 ad = ab / abl;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    bool thin = true;
                    for (int k = 0; k < 3 && thin; k++) { Vector3 w = vs[tris[i + k]] - bestA; float t = Vector3.Dot(w, ad) / abl; float d = (w - ad * Vector3.Dot(w, ad)).magnitude; if (d > D.stringRadiusM || t < -0.02f || t > 1.02f) thin = false; }
                    if (thin) removed++; else { keep.Add(tris[i]); keep.Add(tris[i + 1]); keep.Add(tris[i + 2]); }
                } }
                float cl = (tipB - tipA).magnitude;
                if (BowCfg.Log) Debug.Log("[bow] draw: string triangles removed " + removed + " of " + (tris.Length / 3) + ", tips " + tipA.ToString("F3") + " / " + tipB.ToString("F3") + " chord " + cl.ToString("F3") + " m");
                if (removed == 0) return;
                var m2 = Instantiate(mesh); m2.SetTriangles(keep, 0); m2.RecalculateBounds(); mf.sharedMesh = m2;
                var go = new GameObject("BowString"); go.transform.SetParent(inst.transform, false); strLr = go.AddComponent<LineRenderer>();
                strLr.positionCount = 3; strLr.useWorldSpace = true; strLr.startWidth = strLr.endWidth = D.widthM; strLr.numCapVertices = 2; strLr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; strLr.alignment = LineAlignment.View;
                Shader sh = null; var shs = BowCfg.Get().arrow.shaders; for (int i = 0; shs != null && i < shs.Length && sh == null; i++) sh = Shader.Find(shs[i]);
                if (sh != null) { var mat = new Material(sh) { name = "BowString" }; Color col = BowCfg.Col(D.color, Color.white); if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", col); if (mat.HasProperty("_Color")) mat.SetColor("_Color", col); if (mat.HasProperty("_UnlitColor")) mat.SetColor("_UnlitColor", col); if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.2f); strLr.sharedMaterial = mat; }
                strOk = true; return;
            }
        }
        catch (Exception e) { GearTable.Warn("[bow] draw setup failed: " + e.Message); }
    }

    void DrawString()
    {
        if (!strOk || strLr == null || inst == null) return;
        var D = BowCfg.Get().visual.draw; var N = BowCfg.Get().visual.nock;
        bool draw = Drawing();
        if (!draw) drawK = 0f; else drawK = Mathf.MoveTowards(drawK, 1f, Time.deltaTime / Mathf.Max(0.02f, D.drawSeconds));
        if (rHand == null) rHand = FindNamed(Skel(), Roster.BoneName(this, D.handRole));
        var T = inst.transform; Vector3 rest = (tipA + tipB) * 0.5f, np = rest;
        if (drawK > 0f && rHand != null) { Vector3 hp = T.InverseTransformPoint(rHand.TransformPoint(new Vector3(D.handPx, D.handPy, D.handPz)) + Vector3.down * D.dropM); np = Vector3.Lerp(rest, hp, Mathf.SmoothStep(0f, 1f, drawK)); }
        strLr.SetPosition(0, T.TransformPoint(tipA)); strLr.SetPosition(1, T.TransformPoint(np)); strLr.SetPosition(2, T.TransformPoint(tipB));
        if (nocked != null && nocked.activeSelf) PlaceNocked(T.TransformPoint(np)); else if (nocked != null && N != null) nocked.transform.localPosition = new Vector3(N.px, N.py, N.pz) + (np - rest);
    }

    GameObject nocked;
    void Nock()   // the arrow on the string while the fighter draws (Windup); gone on the loose
    {
        var N = BowCfg.Get().visual.nock; bool show = N != null && N.enabled && ((Drawing() && (archer == null || archer.ArrowOnString)) || BowCarry.ArrowOn(fighter));   // BOWCARRY v1: the arrow appears at the nock clip's event, and sits on the string in the nocked-ready loop
        if (show && nocked == null && inst != null) { nocked = BowArrowView.MakeNocked(inst.transform); nocked.transform.localPosition = new Vector3(N.px, N.py, N.pz); nocked.transform.localRotation = Quaternion.Euler(N.rxDeg, N.ryDeg, N.rzDeg); }
        if (nocked != null) nocked.SetActive(show);
    }

    void Seat(bool toHand, BowCfg.Carry K)
    {
        var V = BowCfg.Get().visual; Transform parent = hand;
        if (!toHand)
        {
            if (carryBone == null) carryBone = FindNamed(Skel(), Roster.BoneName(this, K.parentRole));
            if (carryBone == null) { if (!carryWarned) { carryWarned = true; GearTable.Warn("[bow] no carry bone '" + Roster.BoneName(this, K.parentRole) + "' on " + name + ": the bow stays on the hand"); } return; }
            parent = carryBone;
        }
        var t = inst.transform; t.SetParent(parent, false);
        if (toHand) { t.localRotation = Quaternion.Euler(V.holdRxDeg, V.holdRyDeg, V.holdRzDeg); t.localPosition = new Vector3(V.holdPx, V.holdPy, V.holdPz); }
        else { t.localRotation = Quaternion.Euler(K.rxDeg, K.ryDeg, K.rzDeg); t.localPosition = new Vector3(K.px, K.py, K.pz); }
        t.localScale = (toHand || K.scale <= 0.01f) ? Vector3.one : Vector3.one * K.scale; onHand = toHand;   // carry.scale: K.scale 1 = as before
    }

    void RestoreSword() { for (int i = 0; i < hidden.Count; i++) if (hidden[i] != null) hidden[i].SetActive(true); hidden.Clear(); }
}

// Auto-aim brackets on the target while the bow is up, and a small mode chip. Own OnGUI host (created at boot when the master is on), so GameHUD is untouched.
public class BowHud : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot() { if (!BowCfg.Master || !BowCfg.Get().hud.enabled || FindFirstObjectByType<BowHud>() != null) return; var go = new GameObject("BowHud"); DontDestroyOnLoad(go); go.AddComponent<BowHud>(); }   // HUDKEEP: Boot runs once at first load; without DontDestroyOnLoad a zone restart / Retry (scene reload) destroyed the host for good: no chip, no reticle

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint || !BowCfg.Master || ZoneEndScreen.Active) return; /* ENDHUD v1 */
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
        BowFree.DrawReticle(f, bow);   // FREEAIM v1
        string chip = bow ? (BowCfg.Get().quiverOn ? H.chipBow + H.chipAmmoSep + ammo : H.chipBow) : WeaponChip(f, H.chipSword);   // WEAPONTAG v1
        float chipA = 1f;   // TAKE C8b (HUD text rule): chipShowS > 0 = the weapon chip shows only for that many seconds after it CHANGES (weapon swapped, mode, arrows left), fading over chipFadeS; 0 = always, as today
        if (H.chipShowS > 0f)
        {
            if (!chipRead) { chipRead = true; Debug.Log("[Take] C8b weapon chip: shows only on change, " + H.chipShowS.ToString("F1") + " s (bow.json hud.chipShowS)"); }
            if (chip != chipLast) { chipLast = chip; chipAt = Time.unscaledTime; chipHidLogged = false; }
            float ca = Time.unscaledTime - chipAt; chipA = Mathf.Clamp01((H.chipShowS - ca) / Mathf.Max(0.01f, H.chipFadeS));
            if (chipA <= 0f) { if (!chipHidLogged) { chipHidLogged = true; Debug.Log("[Take] C8b weapon chip hidden " + ca.ToString("F1") + " s after the last change (" + chipLast + ")"); } return; }
        }
        float oldA = HudDraw.Alpha; if (chipA < 1f) HudDraw.Alpha = oldA * chipA;
        HudDraw.Text(new Rect(Screen.width - H.chipRightPx - H.chipWidthPx, Screen.height - H.chipBottomPx, H.chipWidthPx, H.chipFontPx * H.chipLineH), chip, "serif", H.chipFontPx, TextAnchor.MiddleRight, false, c);
        HudDraw.Alpha = oldA;
    }
    string chipLast = ""; float chipAt = -999f; bool chipRead, chipHidLogged;
    static string wcId = null, wcName = "";
    static string WeaponChip(DuelFighter f, string dflt)   // WEAPONTAG v1: equipped weapon display name, cached per id
    {
        var ws = WeaponHooks.Of(f); string id = ws != null && ws.id != null ? ws.id : "";
        if (id.Length == 0 || id == "knight_sword" || id == "bandit_sword") return dflt;
        if (id != wcId) { wcId = id; var it = GearTable.Find(id); wcName = it != null && !string.IsNullOrEmpty(it.name) ? it.name.ToUpperInvariant() : id.ToUpperInvariant(); }
        return wcName;
    }
}

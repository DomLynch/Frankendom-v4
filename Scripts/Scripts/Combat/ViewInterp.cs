using UnityEngine;

// PHYSAUDIT: one-way sim -> view. The sim moves a fighter's root on 60 Hz ticks (DuelFighter.MoveTick); this draws the model between the last two tick positions so any display
// frame rate looks smooth while the root transform (what Duel.Resolve reads) stays exactly the tick position. Nothing in the sim reads ViewPos or the model offset.
[DefaultExecutionOrder(10000)]
public class ViewInterp : MonoBehaviour
{
    Vector3 prev, cur, lastOff; bool have; Transform model;
    public Vector3 ViewPos { get; private set; }
    public void Push(Vector3 pos, bool snap) { if (!have || snap) { prev = cur = pos; have = true; } else { prev = cur; cur = pos; } }
    void LateUpdate()
    {
        if (model == null) { var f = GetComponent<Fighter>(); if (f != null) model = f.model; }
        if (model != null) model.position -= lastOff;          // undo last frame's offset (Fighter / FootGround only rewrite localPosition.y)
        if (!have) { lastOff = Vector3.zero; ViewPos = transform.position; return; }
        Vector3 off = Vector3.Lerp(prev, cur, Duel.Alpha) - cur; off.y = 0f;
        if (off.sqrMagnitude > 1f) off = Vector3.zero;          // a teleport / knock over 1 m in a tick: no smear
        if (model != null) model.position += off;
        lastOff = off; ViewPos = transform.position + off;
    }
    void OnDisable() { if (model != null) model.position -= lastOff; lastOff = Vector3.zero; }
}

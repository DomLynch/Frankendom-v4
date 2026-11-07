using UnityEngine;

// Mobile touch layer ported from V3 touch-layout (left joystick; STRIKE bottom-right with tap / swipe-up stab / swipe-side slash / hold heavy;
// SPECIAL 1 at 135 deg, SPECIAL 2 at 45, GUARD 225 (hold), ROLL 315, KICK 180 around STRIKE). Enabled on touch devices.
// HCAUDIT1 v104: every layout number, threshold, colour and label is controls.json "touch" (ControlsTable.TouchCfg); the class defaults there are the literals this file had. A colour array with fewer than 4 numbers draws magenta (= data error).
public class TouchControls : MonoBehaviour
{
    public static bool Active;
    public Transform cam;
    DuelFighter df;
    int joyId = -1, strikeId = -1; Vector2 joyOrigin, joyNow, strikeStart; float strikeT; bool strikeHeavy, strikeDone;
    int guardId = -1;
    Vector2 sc; float R;
    GUIStyle label;

    void Awake()
    {
        df = GetComponent<DuelFighter>();
        Active = Input.touchSupported && Application.isMobilePlatform;
    }

    Vector2 Btn(float deg) { var T = ControlsTable.Get().touch; float a = deg * Mathf.Deg2Rad; return new Vector2(sc.x * T.centreX, sc.y * T.centreY) + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * R * T.btnPreviewRingR; }   // sc.y flipped below (GUI y down)

    void Update()
    {
        if (!Active && Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began) Active = true;
        if (!Active || df == null || df.Dead) return;
        var T = ControlsTable.Get().touch;
        sc = new Vector2(Screen.width, Screen.height); R = Mathf.Min(sc.x, sc.y) * T.radiusFrac;
        Vector2 strikeC = new Vector2(sc.x * T.centreX, sc.y * T.centreY);   // touch space: y up
        bool guard = false;
        for (int i = 0; i < Input.touchCount; i++)
        {
            var t = Input.GetTouch(i); Vector2 p = t.position;
            if (t.phase == TouchPhase.Began)
            {
                if (p.x < sc.x * T.joyZoneX && joyId < 0) { joyId = t.fingerId; joyOrigin = p; joyNow = p; }
                else if (Vector2.Distance(p, strikeC) < R * T.strikeHitR && strikeId < 0) { strikeId = t.fingerId; strikeStart = p; strikeT = Time.time; strikeHeavy = false; strikeDone = false; }
                else
                {
                    for (int k = 0; k < T.buttons.Length; k++)
                    {
                        float deg = T.buttons[k].deg;
                        Vector2 c = strikeC + new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)) * R * T.buttonRingR;
                        if (Vector2.Distance(p, c) < R)
                        {
                            string act = T.buttons[k].action;
                            if (act == "guard") guardId = t.fingerId; else if (!string.IsNullOrEmpty(act)) df.Press(act);   // "guard" = hold; anything else is a DuelFighter.Press id
                        }
                    }
                }
            }
            if (t.fingerId == joyId) { joyNow = p; if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) joyId = -1; }
            if (t.fingerId == strikeId)
            {
                Vector2 d = p - strikeStart;
                if (!strikeDone && !strikeHeavy && Time.time - strikeT > T.holdHeavyS && d.magnitude < R * T.holdMoveR) { df.Press("heavy"); strikeHeavy = true; }
                df.heavyHeld = strikeHeavy;
                if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                {
                    if (!strikeHeavy)
                    {
                        if (d.y > R * T.swipeR) df.Press("thrust"); else if (d.x < -R * T.swipeR) df.Press("light_left"); else if (d.x > R * T.swipeR) df.Press("light_right"); else df.Press("light");
                    }
                    strikeId = -1; df.heavyHeld = false;
                }
            }
            if (t.fingerId == guardId) { if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) guardId = -1; else guard = true; }
        }
        df.guardHeld = guard;
        Vector2 j = joyId >= 0 ? Vector2.ClampMagnitude((joyNow - joyOrigin) / (R * T.joyRangeR), 1f) : Vector2.zero;
        Vector3 fwd = cam ? Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized : Vector3.forward, right = cam ? Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized : Vector3.right;
        df.moveDir = j.magnitude > T.joyDeadZone ? (fwd * j.y + right * j.x).normalized : Vector3.zero;
        df.run = j.magnitude > T.runAt;
    }

    void Circle(Vector2 c, float r, Color col, string text)
    {
        GUI.color = col; GUI.DrawTexture(new Rect(c.x - r, sc.y - c.y - r, r * 2, r * 2), Texture2D.whiteTexture); GUI.color = Color.white;
        if (label == null) label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = ControlsTable.Get().touch.fontSize, fontStyle = FontStyle.Bold };
        GUI.Label(new Rect(c.x - r, sc.y - c.y - r, r * 2, r * 2), text, label);
    }

    void OnGUI()
    {
        if (!Active) return;
        var T = ControlsTable.Get().touch;
        sc = new Vector2(Screen.width, Screen.height); R = Mathf.Min(sc.x, sc.y) * T.radiusFrac;
        Vector2 strikeC = new Vector2(sc.x * T.centreX, sc.y * T.centreY);
        Circle(strikeC, R * T.strikeDrawR, ControlsTable.Col(T.strikeColor, Color.magenta), T.strikeLabel);
        for (int k = 0; k < T.buttons.Length; k++) Circle(strikeC + new Vector2(Mathf.Cos(T.buttons[k].deg * Mathf.Deg2Rad), Mathf.Sin(T.buttons[k].deg * Mathf.Deg2Rad)) * R * T.buttonRingR, R * T.buttonDrawR, ControlsTable.Col(T.buttonColor, Color.magenta), T.buttons[k].label);
        if (joyId >= 0) { Circle(joyOrigin, R * T.joyDrawR, ControlsTable.Col(T.joyBaseColor, Color.magenta), ""); Circle(Vector2.ClampMagnitude(joyNow - joyOrigin, R * T.joyDrawR) + joyOrigin, R * T.joyKnobR, ControlsTable.Col(T.joyKnobColor, Color.magenta), ""); }
    }
}

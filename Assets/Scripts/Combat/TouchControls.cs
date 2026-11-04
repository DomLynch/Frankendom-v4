using UnityEngine;

// Mobile touch layer ported from V3 touch-layout (left joystick; STRIKE bottom-right with tap / swipe-up stab / swipe-side slash / hold heavy;
// SPECIAL 1 at 135 deg, SPECIAL 2 at 45, GUARD 225 (hold), ROLL 315, KICK 180 around STRIKE). Enabled on touch devices.
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

    Vector2 Btn(float deg) { float a = deg * Mathf.Deg2Rad; return new Vector2(sc.x * 0.84f, sc.y * 0.2f) + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * R * 1.85f; }   // sc.y flipped below (GUI y down)

    void Update()
    {
        if (!Active && Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began) Active = true;
        if (!Active || df == null || df.Dead) return;
        sc = new Vector2(Screen.width, Screen.height); R = Mathf.Min(sc.x, sc.y) * 0.075f;
        Vector2 strikeC = new Vector2(sc.x * 0.84f, sc.y * 0.2f);   // touch space: y up
        bool guard = false;
        for (int i = 0; i < Input.touchCount; i++)
        {
            var t = Input.GetTouch(i); Vector2 p = t.position;
            if (t.phase == TouchPhase.Began)
            {
                if (p.x < sc.x * 0.4f && joyId < 0) { joyId = t.fingerId; joyOrigin = p; joyNow = p; }
                else if (Vector2.Distance(p, strikeC) < R * 1.3f && strikeId < 0) { strikeId = t.fingerId; strikeStart = p; strikeT = Time.time; strikeHeavy = false; strikeDone = false; }
                else
                {
                    for (int k = 0; k < 5; k++)
                    {
                        float deg = k == 0 ? 135 : k == 1 ? 45 : k == 2 ? 225 : k == 3 ? 315 : 180;
                        Vector2 c = strikeC + new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)) * R * 2.0f;
                        if (Vector2.Distance(p, c) < R)
                        {
                            if (k == 0) df.Press("special1"); else if (k == 1) df.Press("special2"); else if (k == 2) guardId = t.fingerId; else if (k == 3) df.Press("roll"); else df.Press("kick");
                        }
                    }
                }
            }
            if (t.fingerId == joyId) { joyNow = p; if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) joyId = -1; }
            if (t.fingerId == strikeId)
            {
                Vector2 d = p - strikeStart;
                if (!strikeDone && !strikeHeavy && Time.time - strikeT > 0.22f && d.magnitude < R * 0.4f) { df.Press("heavy"); strikeHeavy = true; }
                df.heavyHeld = strikeHeavy;
                if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                {
                    if (!strikeHeavy)
                    {
                        if (d.y > R * 0.6f) df.Press("thrust"); else if (d.x < -R * 0.6f) df.Press("light_left"); else if (d.x > R * 0.6f) df.Press("light_right"); else df.Press("light");
                    }
                    strikeId = -1; df.heavyHeld = false;
                }
            }
            if (t.fingerId == guardId) { if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) guardId = -1; else guard = true; }
        }
        df.guardHeld = guard;
        Vector2 j = joyId >= 0 ? Vector2.ClampMagnitude((joyNow - joyOrigin) / (R * 2.2f), 1f) : Vector2.zero;
        Vector3 fwd = cam ? Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized : Vector3.forward, right = cam ? Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized : Vector3.right;
        df.moveDir = j.magnitude > 0.2f ? (fwd * j.y + right * j.x).normalized : Vector3.zero;
        df.run = j.magnitude > 0.85f;
    }

    void Circle(Vector2 c, float r, Color col, string text)
    {
        GUI.color = col; GUI.DrawTexture(new Rect(c.x - r, sc.y - c.y - r, r * 2, r * 2), Texture2D.whiteTexture); GUI.color = Color.white;
        if (label == null) label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 14, fontStyle = FontStyle.Bold };
        GUI.Label(new Rect(c.x - r, sc.y - c.y - r, r * 2, r * 2), text, label);
    }

    void OnGUI()
    {
        if (!Active) return;
        sc = new Vector2(Screen.width, Screen.height); R = Mathf.Min(sc.x, sc.y) * 0.075f;
        Vector2 strikeC = new Vector2(sc.x * 0.84f, sc.y * 0.2f);
        Circle(strikeC, R * 1.1f, new Color(0.8f, 0.2f, 0.15f, 0.45f), "STRIKE");
        string[] names = { "SKULL", "WIND", "GUARD", "ROLL", "KICK" }; float[] degs = { 135, 45, 225, 315, 180 };
        for (int k = 0; k < 5; k++) Circle(strikeC + new Vector2(Mathf.Cos(degs[k] * Mathf.Deg2Rad), Mathf.Sin(degs[k] * Mathf.Deg2Rad)) * R * 2.0f, R * 0.75f, new Color(0.3f, 0.3f, 0.35f, 0.45f), names[k]);
        if (joyId >= 0) { Circle(joyOrigin, R * 1.2f, new Color(1, 1, 1, 0.12f), ""); Circle(Vector2.ClampMagnitude(joyNow - joyOrigin, R * 1.2f) + joyOrigin, R * 0.6f, new Color(1, 1, 1, 0.35f), ""); }
    }
}

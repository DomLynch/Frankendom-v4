using UnityEngine;
using UnityEngine.SceneManagement;

public class GameHUD : MonoBehaviour
{
    public Health player, enemy;
    public DuelFighter pf, ef;
    GUIStyle big; bool showHelp, showFps; float fpsAcc, fpsShown; int fpsN;
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F1)) showHelp = !showHelp;
        if (Input.GetKeyDown(KeyCode.F3)) showFps = !showFps;
        fpsAcc += Time.unscaledDeltaTime; fpsN++; if (fpsAcc >= 0.5f) { fpsShown = fpsN / fpsAcc; fpsAcc = 0f; fpsN = 0; }
        if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Return)) SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        if (Input.GetKeyDown(KeyCode.Escape)) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
    }
    void Bar(Rect r, float v, Color c)
    {
        GUI.color = new Color(0, 0, 0, 0.6f); GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = c; GUI.DrawTexture(new Rect(r.x + 2, r.y + 2, (r.width - 4) * Mathf.Clamp01(v), r.height - 4), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }
    Texture2D disc, ringTex;
    Texture2D Disc(bool ring)
    {
        var t = new Texture2D(128, 128, TextureFormat.RGBA32, false);
        for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++) { float d = Vector2.Distance(new Vector2(x, y), new Vector2(63.5f, 63.5f)) / 63.5f; float a = ring ? Mathf.Clamp01((d - 0.8f) * 12f) * Mathf.Clamp01((1f - d) * 24f) : Mathf.Clamp01((1f - d) * 24f); t.SetPixel(x, y, new Color(1, 1, 1, a)); }
        t.Apply(); return t;
    }
    // round dark special icon with a ready ring (gold when ready, dim fill while cooling down)
    void Ring(Rect r, float cd, string key, string glyph)
    {
        if (disc == null) { disc = Disc(false); ringTex = Disc(true); }
        GUI.color = new Color(0.04f, 0.04f, 0.05f, 0.8f); GUI.DrawTexture(r, disc);
        GUI.color = cd <= 0f ? new Color(0.95f, 0.72f, 0.2f, 1f) : new Color(0.4f, 0.4f, 0.45f, 0.9f); GUI.DrawTexture(r, ringTex);
        GUI.color = cd <= 0f ? new Color(0.9f, 0.9f, 0.9f, 1f) : new Color(0.5f, 0.5f, 0.5f, 0.9f);
        var gs = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 26, fontStyle = FontStyle.Bold };
        GUI.Label(r, glyph, gs); GUI.color = Color.white;
        var ks = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 11 };
        GUI.Label(new Rect(r.x, r.yMax - 2, r.width, 16), key, ks);
    }
    void Start() { foreach (var a in System.Environment.GetCommandLineArgs()) if (a == "-fps" || a == "-gaps" || a == "-autoplay") showFps = true; }
    void Outlined(Rect r, string t) { var o = new GUIStyle(GUI.skin.label); o.normal.textColor = Color.black; foreach (var d in new[] { new Vector2(-2, 0), new Vector2(2, 0), new Vector2(0, -2), new Vector2(0, 2), new Vector2(-2, -2), new Vector2(2, 2), new Vector2(-2, 2), new Vector2(2, -2) }) GUI.Label(new Rect(r.x + d.x, r.y + d.y, r.width, r.height), t, o); GUI.Label(r, t); }
    void Panel(Rect r) { GUI.color = new Color(0, 0, 0, 0.6f); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.white; }
    void OnGUI()
    {
        if (showFps) GUI.Label(new Rect(Screen.width / 2 - 60, 8, 140, 24), "FPS " + fpsShown.ToString("F0"));
        if (showFps && pf && Camera.main) { var c = Camera.main.transform; var rel = c.position - pf.transform.position; var fl = new Vector3(c.forward.x, 0f, c.forward.z).normalized; float back = -Vector3.Dot(rel, new Vector3(c.forward.x, 0f, c.forward.z).normalized); var ax = ef ? ef.transform.position - pf.transform.position : Vector3.forward; ax.y = 0f; ax.Normalize(); float side = Vector3.Dot(rel, Quaternion.Euler(0f, 90f, 0f) * ax); float pitch = Mathf.Asin(c.forward.y) * Mathf.Rad2Deg; var sp = Camera.main.WorldToScreenPoint(pf.transform.position + Vector3.up); GUI.Label(new Rect(Screen.width / 2 - 200, 28, 420, 24), $"CAM h={rel.y:F2} back={back:F2} side={side:F2} pitch={pitch:F1} px={sp.x / Screen.width:F2}"); }
        if (big == null) big = new GUIStyle(GUI.skin.label) { fontSize = 48, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        Panel(new Rect(22, 8, 376, 76)); Panel(new Rect(Screen.width - 398, 8, 376, 76));
        if (player) { Outlined(new Rect(30, 14, 300, 24), "DRACULA"); Bar(new Rect(30, 38, 360, 20), player.current / player.max, new Color(0.7f, 0.05f, 0.08f)); }
        if (enemy) { Outlined(new Rect(Screen.width - 390, 14, 300, 24), "PITBORN"); Bar(new Rect(Screen.width - 390, 38, 360, 20), enemy.current / enemy.max, new Color(0.8f, 0.45f, 0.1f)); }
        if (pf) { Bar(new Rect(30, 62, 360, 8), pf.stamina / pf.maxStamina, new Color(0.2f, 0.65f, 0.3f)); Bar(new Rect(30, 72, 360, 8), pf.posture / 100f, new Color(0.85f, 0.8f, 0.3f)); }
        if (ef) { Bar(new Rect(Screen.width - 390, 62, 360, 8), ef.stamina / ef.maxStamina, new Color(0.2f, 0.65f, 0.3f)); Bar(new Rect(Screen.width - 390, 72, 360, 8), ef.posture / 100f, new Color(0.85f, 0.8f, 0.3f)); }
        if (pf) { Ring(new Rect(Screen.width - 170, Screen.height - 100, 64, 64), pf.special1Cd / 540f, "1", "S"); Ring(new Rect(Screen.width - 90, Screen.height - 100, 64, 64), pf.special2Cd / 540f, "2", "W"); }
        if (showHelp || Time.timeSinceLevelLoad < 10f) GUI.Label(new Rect(30, Screen.height - 30, 1600, 24), "WASD move  Shift run  F/LMB strike (tap=light, A/D=side, W=stab, hold=heavy)  T stab  G heavy  E/Space roll (+A/D/S = evade)  tap A/D twice = side-step  Q/RMB guard  C kick  1/2 specials  Tab lock  R recenter  Backspace restart");
        string msg = player && player.Dead ? "YOU DIED  -  press Backspace" : enemy && enemy.Dead ? "VICTORY  -  press Backspace" : null;
        if (msg != null)
        {
            GUI.Label(new Rect(0, Screen.height / 2 - 60, Screen.width, 120), msg, big);
            if (GUI.Button(new Rect(Screen.width / 2 - 110, Screen.height / 2 + 70, 220, 50), "Retry")) SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }
    }
}











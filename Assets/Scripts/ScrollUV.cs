using UnityEngine;

public class ScrollUV : MonoBehaviour
{
    public Vector2 speed = new Vector2(0.01f, 0.004f);
    Renderer r;
    void Start() { r = GetComponent<Renderer>(); }
    void Update()
    {
        var o = speed * Time.time;
        var m = r != null ? r.material : null;
        if (m == null) return;
        string bk = m.HasProperty("_BaseColorMap") ? "_BaseColorMap" : m.HasProperty("_UnlitColorMap") ? "_UnlitColorMap" : m.HasProperty("_BaseMap") ? "_BaseMap" : null;
        if (!string.IsNullOrEmpty(bk)) { try { m.SetTextureOffset(bk, o); } catch { } }
        if (m.HasProperty("_NormalMap")) { try { m.SetTextureOffset("_NormalMap", o); } catch { } }
        else if (m.HasProperty("_BumpMap")) { try { m.SetTextureOffset("_BumpMap", o); } catch { } }
    }
}


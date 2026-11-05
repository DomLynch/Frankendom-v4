using UnityEngine;

public class ScrollUV : MonoBehaviour
{
    public Vector2 speed = new Vector2(0.01f, 0.004f);
    Renderer r;
    // PERF: r.material (instancing check), five HasProperty string lookups and string-keyed SetTextureOffset ran every frame. Resolved once per material instead (re-resolved if the renderer's material changes).
    Material m; int baseId, normId; bool haveBase, haveNorm;
    void Start() { r = GetComponent<Renderer>(); }
    void Resolve()
    {
        m = r != null ? r.material : null;
        haveBase = haveNorm = false;
        if (m == null) return;
        string bk = m.HasProperty("_BaseColorMap") ? "_BaseColorMap" : m.HasProperty("_UnlitColorMap") ? "_UnlitColorMap" : m.HasProperty("_BaseMap") ? "_BaseMap" : null;
        if (!string.IsNullOrEmpty(bk)) { baseId = Shader.PropertyToID(bk); haveBase = true; }
        if (m.HasProperty("_NormalMap")) { normId = Shader.PropertyToID("_NormalMap"); haveNorm = true; }
        else if (m.HasProperty("_BumpMap")) { normId = Shader.PropertyToID("_BumpMap"); haveNorm = true; }
    }
    void Update()
    {
        if (r == null) return;
        if (m == null || r.sharedMaterial != m) Resolve();
        if (m == null) return;
        var o = speed * Time.time;
        if (haveBase) m.SetTextureOffset(baseId, o);
        if (haveNorm) m.SetTextureOffset(normId, o);
    }
}


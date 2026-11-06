using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// ALPHA v1 (laneALPHA): READ-ONLY material census for the A/B. Off unless the player is started with -alphacensus:<outdir>. Changes nothing in the scene.
//   -alphacensus:<dir>        write <dir>\alpha_census_<tag>.csv once, then stop
//   -alphacensuswait:<sec>    seconds after load before sampling (default 25; the benchmark warm-up is 10)
//   -benchtag:<tag>           reused for the file name when present
// One row per material actually on a live Renderer: shader, render queue, whether alpha clip is on (_AlphaCutoffEnable, _ALPHATEST_ON), cutoff, how many renderers use it,
// how many of them the camera currently sees, and the sum of their screen-space bounding-rect areas as a share of the screen (an upper bound: rects overlap and a rect is bigger than the
// mesh; a renderer with n materials counts 1/n of its rect to each). Use it to rank materials by cost, not to quote a percentage.
public static class AlphaCensus
{
    static string Arg(string key)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length; i++) if (a[i].StartsWith(key, StringComparison.Ordinal)) return a[i].Substring(key.Length);
        return null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string dir = Arg("-alphacensus:"); if (string.IsNullOrEmpty(dir)) return;
        var go = new GameObject("AlphaCensus"); go.hideFlags = HideFlags.HideAndDontSave; UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<Runner>().Init(dir, Arg("-benchtag:") ?? "run", float.TryParse(Arg("-alphacensuswait:"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 25f);
    }

    class Row { public string shader; public int queue, renderers, visible; public bool clipEnable, clipKeyword; public float cutoff, share; }

    class Runner : MonoBehaviour
    {
        string dir, tag; float wait;
        public void Init(string d, string t, float w) { dir = d; tag = t; wait = w; StartCoroutine(Go()); }
        IEnumerator Go()
        {
            yield return new WaitForSecondsRealtime(wait);
            yield return new WaitForEndOfFrame();
            try { Write(); } catch (Exception e) { Debug.LogWarning("ALPHACENSUS failed: " + e.Message); }
            Destroy(gameObject);
        }
        void Write()
        {
            var cam = Camera.main; var rows = new Dictionary<string, Row>();
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var ms = r.sharedMaterials; if (ms == null || ms.Length == 0) continue;
                float rect = 0f;
                if (cam != null && r.isVisible)
                {
                    var b = r.bounds; var c = b.center; var e = b.extents; float x0 = 2f, y0 = 2f, x1 = -1f, y1 = -1f; bool behind = false;
                    for (int i = 0; i < 8; i++)
                    {
                        var p = cam.WorldToViewportPoint(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                        if (p.z <= 0f) { behind = true; break; }
                        x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x); y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y);
                    }
                    if (!behind) rect = Mathf.Max(0f, Mathf.Min(1f, x1) - Mathf.Max(0f, x0)) * Mathf.Max(0f, Mathf.Min(1f, y1) - Mathf.Max(0f, y0));
                }
                foreach (var m in ms)
                {
                    if (m == null) continue;
                    string key = m.name; Row row;
                    if (!rows.TryGetValue(key, out row))
                    {
                        row = new Row { shader = m.shader != null ? m.shader.name : "?", queue = m.renderQueue };
                        row.clipEnable = m.HasProperty("_AlphaCutoffEnable") && m.GetFloat("_AlphaCutoffEnable") > 0.5f;
                        row.clipKeyword = m.IsKeywordEnabled("_ALPHATEST_ON");
                        row.cutoff = m.HasProperty("_AlphaCutoff") ? m.GetFloat("_AlphaCutoff") : (m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : -1f);
                        rows[key] = row;
                    }
                    row.renderers++; if (r.isVisible) row.visible++; row.share += rect / ms.Length;
                }
            }
            var sb = new StringBuilder("material,shader,queue,clipEnable,clipKeyword,cutoff,renderers,visibleRenderers,rectShareUpperBound\n");
            var keys = new List<string>(rows.Keys); keys.Sort((a, b) => rows[b].share.CompareTo(rows[a].share));
            foreach (var k in keys)
            {
                var r = rows[k];
                sb.Append(k.Replace(',', ';')).Append(',').Append(r.shader.Replace(',', ';')).Append(',').Append(r.queue).Append(',').Append(r.clipEnable ? 1 : 0).Append(',').Append(r.clipKeyword ? 1 : 0).Append(',')
                  .Append(r.cutoff.ToString("0.###", CultureInfo.InvariantCulture)).Append(',').Append(r.renderers).Append(',').Append(r.visible).Append(',').Append(r.share.ToString("0.0000", CultureInfo.InvariantCulture)).Append('\n');
            }
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "alpha_census_" + tag + ".csv"), sb.ToString());
            Debug.Log("ALPHACENSUS rows=" + rows.Count + " -> " + Path.Combine(dir, "alpha_census_" + tag + ".csv"));
        }
    }
}

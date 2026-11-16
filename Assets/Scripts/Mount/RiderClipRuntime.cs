using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// RUNTIMECLIPS (unity-dev, 2026-11-16): rider clips straight from a .glb at RUNTIME, no Unity asset, no import, no bake, no scene reopen.
// Why: any asset import in a player build makes Unity re-open scenes (the main scene alone is 25-30 GB in the Editor, pe 12:27); a clip change must be a FILE COPY (StreamingAssets\Rider\Joan_rider_clips.glb), like the gear glbs.
// A small own glTF 2.0 binary reader (no glTFast): nodes, accessors, bufferViews, animations (rotation / translation channels, LINEAR) -> AnimationClips on Joan's existing bone paths (SetCurve). glTF is right handed, Unity left handed:
// position (x, y, z) -> (-x, y, z), quaternion (x, y, z, w) -> (x, -y, -z, w) (same as glTFast). Scale channels are ignored.
public static class RiderClipRuntime
{
    [Serializable] class GNode { public string name = ""; }
    [Serializable] class GBuf { public int buffer = 0, byteOffset = 0, byteLength = 0, byteStride = 0; }
    [Serializable] class GAcc { public int bufferView = 0, byteOffset = 0, componentType = 0, count = 0; public string type = ""; }
    [Serializable] class GTarget { public int node = -1; public string path = ""; }
    [Serializable] class GChan { public int sampler = 0; public GTarget target = new GTarget(); }
    [Serializable] class GSamp { public int input = 0, output = 0; public string interpolation = "LINEAR"; }
    [Serializable] class GAnim { public string name = ""; public GChan[] channels = new GChan[0]; public GSamp[] samplers = new GSamp[0]; }
    [Serializable] class GDoc { public GNode[] nodes = new GNode[0]; public GAcc[] accessors = new GAcc[0]; public GBuf[] bufferViews = new GBuf[0]; public GAnim[] animations = new GAnim[0]; }

    static float[] Floats(GDoc d, byte[] bin, int acc, out int comps)
    {
        var a = d.accessors[acc]; comps = a.type == "SCALAR" ? 1 : a.type == "VEC3" ? 3 : a.type == "VEC4" ? 4 : 0;
        if (comps == 0 || a.componentType != 5126) { comps = 0; return null; }   // float only (the rider clips are float)
        var bv = d.bufferViews[a.bufferView]; int stride = bv.byteStride > 0 ? bv.byteStride : comps * 4; var o = new float[a.count * comps];
        for (int i = 0; i < a.count; i++) for (int c = 0; c < comps; c++) o[i * comps + c] = BitConverter.ToSingle(bin, bv.byteOffset + a.byteOffset + i * stride + c * 4);
        return o;
    }
    static Transform FindNode(Transform root, string name) { if (root.name == name) return root; foreach (Transform c in root) { var r = FindNode(c, name); if (r != null) return r; } return null; }
    static string PathOf(Transform t, Transform root) { var parts = new List<string>(); for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name); parts.Reverse(); return string.Join("/", parts.ToArray()); }

    /// <summary>All animations of the glb as runtime AnimationClips (key = animation name), bone paths relative to animRoot. Null when the file is missing or unreadable.</summary>
    public static Dictionary<string, AnimationClip> Load(string glbPath, Transform animRoot, out string report)
    {
        report = ""; if (animRoot == null || string.IsNullOrEmpty(glbPath) || !File.Exists(glbPath)) { report = "no glb at " + glbPath; return null; }
        byte[] all; try { all = File.ReadAllBytes(glbPath); } catch (Exception e) { report = "read failed: " + e.Message; return null; }
        if (all.Length < 20 || BitConverter.ToUInt32(all, 0) != 0x46546C67u) { report = "not a glb"; return null; }
        string json = null; byte[] bin = null; int p = 12;
        while (p + 8 <= all.Length) { int len = (int)BitConverter.ToUInt32(all, p), type = (int)BitConverter.ToUInt32(all, p + 4); p += 8; if (type == 0x4E4F534A) json = Encoding.UTF8.GetString(all, p, len); else if (type == 0x004E4942) { bin = new byte[len]; Buffer.BlockCopy(all, p, bin, 0, len); } p += len; }
        if (json == null || bin == null) { report = "glb has no JSON/BIN chunk"; return null; }
        GDoc d; try { d = JsonUtility.FromJson<GDoc>(json); } catch (Exception e) { report = "json parse failed: " + e.Message; return null; }
        var res = new Dictionary<string, AnimationClip>(); var sb = new StringBuilder();
        foreach (var an in d.animations)
        {
            if (an == null || string.IsNullOrEmpty(an.name)) continue; var clip = new AnimationClip { name = an.name, frameRate = 30f }; int ok = 0, miss = 0; float len = 0f;
            foreach (var ch in an.channels)
            {
                if (ch == null || ch.target == null || ch.target.node < 0 || ch.target.node >= d.nodes.Length) continue; bool rot = ch.target.path == "rotation", pos = ch.target.path == "translation"; if (!rot && !pos) continue;
                var smp = an.samplers[ch.sampler]; int ic, oc; var times = Floats(d, bin, smp.input, out ic); var vals = Floats(d, bin, smp.output, out oc); if (times == null || vals == null) continue;
                var node = FindNode(animRoot, d.nodes[ch.target.node].name); if (node == null) { miss++; continue; } string path = PathOf(node, animRoot); int n = times.Length; if (n > 0) len = Mathf.Max(len, times[n - 1]);
                if (rot)
                {
                    var kx = new Keyframe[n]; var ky = new Keyframe[n]; var kz = new Keyframe[n]; var kw = new Keyframe[n]; Quaternion prev = Quaternion.identity;
                    for (int i = 0; i < n; i++) { var q = new Quaternion(vals[i * 4], -vals[i * 4 + 1], -vals[i * 4 + 2], vals[i * 4 + 3]); if (i > 0 && Quaternion.Dot(prev, q) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w); prev = q; kx[i] = new Keyframe(times[i], q.x); ky[i] = new Keyframe(times[i], q.y); kz[i] = new Keyframe(times[i], q.z); kw[i] = new Keyframe(times[i], q.w); }
                    clip.SetCurve(path, typeof(Transform), "localRotation.x", new AnimationCurve(kx)); clip.SetCurve(path, typeof(Transform), "localRotation.y", new AnimationCurve(ky)); clip.SetCurve(path, typeof(Transform), "localRotation.z", new AnimationCurve(kz)); clip.SetCurve(path, typeof(Transform), "localRotation.w", new AnimationCurve(kw)); ok++;
                }
                else
                {
                    var kx = new Keyframe[n]; var ky = new Keyframe[n]; var kz = new Keyframe[n];
                    for (int i = 0; i < n; i++) { kx[i] = new Keyframe(times[i], -vals[i * 3]); ky[i] = new Keyframe(times[i], vals[i * 3 + 1]); kz[i] = new Keyframe(times[i], vals[i * 3 + 2]); }
                    clip.SetCurve(path, typeof(Transform), "localPosition.x", new AnimationCurve(kx)); clip.SetCurve(path, typeof(Transform), "localPosition.y", new AnimationCurve(ky)); clip.SetCurve(path, typeof(Transform), "localPosition.z", new AnimationCurve(kz)); ok++;
                }
            }
            clip.wrapMode = WrapMode.Loop; res[an.name] = clip; sb.Append(an.name + " len " + len.ToString("0.00") + " s tracks " + ok + " unmatched " + miss + "; ");
        }
        report = sb.ToString(); return res;
    }
}

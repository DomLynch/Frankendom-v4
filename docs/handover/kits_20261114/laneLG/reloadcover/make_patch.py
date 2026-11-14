#!/usr/bin/env python3
"""Stages RELOADCOVER v1 (NOT applied to Assets, NOT compiled): the StartupWarm black cover is re-armed in GameLoop.Reload, so an in-process restart / Retry /
next-fight scene reload also holds black until the saved loadout has attached (the first lit frame no longer shows the plain body suit).
Reads the tree StartupWarm.cs + GameLoop.cs, asserts every anchor is unique, writes <name>.patched + <name>.diff next to this file.
unity-dev: copy each .patched over its tree file (md5s printed below), CODE-build, with the data kit f1bundle (startup_warm.json) in the same release.
Behaviour: GameLoop.Reload() calls StartupWarm.ReArm() first. ReArm does nothing before the first boot cover is over (Done), in harness runs, or when startup_warm.json cover.onReload is false
(default TRUE). The cover goes up at once, waits for the new scene (sceneLoaded, Single mode, hard cap 20 s), then cover.reloadHoldFrames frames (default 100, cap cover.reloadCapSeconds 4 s from the load)
and releases. cover.reloadHoldSim true also holds Duel.Frozen during the hold (default false: untested). The LoadingCard is NOT re-armed: it waits for Prewarm92 + calm, which are process-wide and long done."""
import os, difflib, hashlib
ROOT = r'C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts'; HERE = os.path.dirname(os.path.abspath(__file__))
def patch(name, subs):
    raw = open(os.path.join(ROOT, name), 'rb').read(); crlf = b'\r\n' in raw; t = raw.decode('utf-8').replace('\r\n', '\n')   # a BOM stays as a leading \ufeff char and is written back
    base = t
    for a, b in subs:
        assert t.count(a) == 1, (name, 'anchor not unique', a[:70], t.count(a)); t = t.replace(a, b)
    out = t.replace('\n', '\r\n' if crlf else '\n').encode('utf-8')
    open(os.path.join(HERE, name + '.patched'), 'wb').write(out)
    d = ''.join(difflib.unified_diff(base.splitlines(True), t.splitlines(True), name + ' (tree)', name + '.patched', n=1))
    open(os.path.join(HERE, name + '.diff'), 'w', encoding='utf-8', newline='').write(d)
    print(name, 'base md5', hashlib.md5(raw).hexdigest()[:10], 'patched md5', hashlib.md5(out).hexdigest()[:10], 'crlf', crlf, 'bom', raw[:3] == b'\xef\xbb\xbf', 'diff lines', d.count('\n'), 'braces', t.count('{'), t.count('}'))
patch('StartupWarm.cs', [
    ('public bool waitForGear = false; public float gearCapSeconds = 12f; public int gearAttachFrames = 20; }',
     'public bool waitForGear = false; public float gearCapSeconds = 12f; public int gearAttachFrames = 20;\n'
     '        public bool onReload = true, reloadHoldSim = false; public int reloadHoldFrames = 100; public float reloadCapSeconds = 4f; }   // RELOADCOVER v1: re-arm after an in-process scene reload (Retry / Backspace / next fight)'),
    ('    void OnDestroy() { if (froze) Duel.Frozen = false; }\n',
     '    // RELOADCOVER v1 (staged): GameLoop.Reload calls this before SceneManager.LoadScene. The persistent StartupWarm object (DontDestroyOnLoad) raises the black cover again, waits for the new scene\n'
     '    // and then reloadHoldFrames frames (the saved loadout applies ~20 frames after a load and every armour piece attaches over more frames), then releases. No-op before the first cover is over, in harness runs, or with cover.onReload false.\n'
     '    public static void ReArm()\n    {\n'
     '        var s = FindFirstObjectByType<StartupWarm>(); if (s == null || !Done || s.harness || s.reloading) return;\n'
     '        var c = Load(); if (c == null || !c.enabled || !c.cover.enabled || !c.cover.onReload) return;\n'
     '        s.StartCoroutine(s.ReloadCover(c));\n    }\n'
     '    bool reloading;\n'
     '    IEnumerator ReloadCover(Cfg c)\n    {\n'
     '        reloading = true; cover = true; Done = false;\n'
     '        if (tex == null) { tex = new Texture2D(1, 1); tex.SetPixel(0, 0, new Color(c.cover.r, c.cover.g, c.cover.b, 1f)); tex.Apply(); }\n'
     '        bool loaded = false; UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode> h = (sc, md) => { if (md == UnityEngine.SceneManagement.LoadSceneMode.Single) loaded = true; };\n'
     '        UnityEngine.SceneManagement.SceneManager.sceneLoaded += h;\n'
     '        float t0 = Time.realtimeSinceStartup; while (!loaded && Time.realtimeSinceStartup - t0 < 20f) yield return null;   // hc:ok hard cap on the scene load wait\n'
     '        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= h;\n'
     '        if (c.cover.reloadHoldSim && !Duel.Frozen) { Duel.Frozen = true; froze = true; }\n'
     '        float t1 = Time.realtimeSinceStartup, cap = Mathf.Max(0.2f, c.cover.reloadCapSeconds); int n = 0;\n'
     '        for (; n < c.cover.reloadHoldFrames && Time.realtimeSinceStartup - t1 < cap; n++) yield return null;\n'
     '        Debug.Log("[Cover] reload cover held " + (Time.realtimeSinceStartup - t0).ToString("0.0") + " s (load " + (t1 - t0).ToString("0.0") + " s + " + n + " frames)");\n'
     '        reloading = false; Release();\n    }\n'
     '    void OnDestroy() { if (froze) Duel.Frozen = false; }\n')])
patch('GameLoop.cs', [
    ('    static void Reload()\n    {\n        ResetGlobals();\n',
     '    static void Reload()\n    {\n        StartupWarm.ReArm();   // RELOADCOVER v1: black cover until the reloaded scene has its gear on (startup_warm.json cover.onReload, default on)\n        ResetGlobals();\n')])

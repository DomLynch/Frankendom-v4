#!/usr/bin/env python3
"""Stages the MountRider.cs patch 'hideWeaponWhileMounted' (NOT applied to Assets). Reads the tree MountRider.cs, asserts every anchor is unique,
writes MountRider.cs.patched + MountRider.diff next to this file. unity-dev: copy MountRider.cs.patched over Assets\\Scripts\\Mount\\MountRider.cs (md5 check below), CODE-build."""
import os, sys, difflib, hashlib
SRC = r'C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\Mount\MountRider.cs'; HERE = os.path.dirname(os.path.abspath(__file__))
raw = open(SRC, 'rb').read(); crlf = b'\r\n' in raw; t = raw.decode('utf-8'); nl = '\r\n' if crlf else '\n'; t = t.replace('\r\n', '\n')
def sub(a, b):
    global t
    assert t.count(a) == 1, ('anchor not unique', a[:60], t.count(a)); t = t.replace(a, b)
sub('    public MtVariant[] variants = new MtVariant[0];   // hades-env variants v1\n',
    '    public MtVariant[] variants = new MtVariant[0];   // hades-env variants v1\n'
    '    public bool hideWeaponWhileMounted = false; public string[] hideSockets = { "WeaponSocket_R" };   // HIDEWEAPON v1: mounted = the carried weapon renderers (under these socket names) are switched off, back on at dismount; default off\n')
sub('Vector3 baseModelPos, lastPos;\n',
    'Vector3 baseModelPos, lastPos;\n    readonly List<Renderer> hiddenW = new List<Renderer>(); float nextHideScan;   // HIDEWEAPON v1\n')
sub('        PlaceHorse(); if (K.debugLog) Debug.Log("[Mount] mounted " + H.id);\n',
    '        nextHideScan = 0f; HideWeapon();   // HIDEWEAPON v1\n        PlaceHorse(); if (K.debugLog) Debug.Log("[Mount] mounted " + H.id);\n')
sub('        mounted = false; /* FIX2 v1: nothing to restore, the fields were never written */\n',
    '        mounted = false; /* FIX2 v1: nothing to restore, the fields were never written */\n        ShowWeapon();   // HIDEWEAPON v1\n')
sub('            df.guardHeld = false;   // v1: no fighting from the saddle\n',
    '            df.guardHeld = false;   // v1: no fighting from the saddle\n            if (K.hideWeaponWhileMounted && Time.unscaledTime >= nextHideScan) { nextHideScan = Time.unscaledTime + 0.5f; HideWeapon(); }   // HIDEWEAPON v1: a weapon swap while riding is caught within 0.5 s\n')
sub('    /// <summary>Procedural gait (the glb has no rig)',
    '    /// <summary>HIDEWEAPON v1: switch off every enabled Renderer under the named weapon sockets of the player (data: mount.json hideWeaponWhileMounted, hideSockets). Only renderers this call switched off are switched back on.</summary>\n'
    '    void HideWeapon()\n    {\n'
    '        if (!K.hideWeaponWhileMounted || df == null || K.hideSockets == null) return;\n'
    '        foreach (var tr in df.GetComponentsInChildren<Transform>(true))\n'
    '        {\n'
    '            bool hit = false; for (int i = 0; i < K.hideSockets.Length; i++) if (tr.name == K.hideSockets[i]) { hit = true; break; }\n'
    '            if (!hit) continue;\n'
    '            foreach (var r in tr.GetComponentsInChildren<Renderer>(true)) if (r.enabled) { r.enabled = false; hiddenW.Add(r); }\n'
    '        }\n    }\n\n'
    '    void ShowWeapon() { for (int i = 0; i < hiddenW.Count; i++) if (hiddenW[i] != null) hiddenW[i].enabled = true; hiddenW.Clear(); }\n\n'
    '    /// <summary>Procedural gait (the glb has no rig)')
out = t.replace('\n', nl)
open(os.path.join(HERE, 'MountRider.cs.patched'), 'wb').write(out.encode('utf-8'))
d = ''.join(difflib.unified_diff(raw.decode('utf-8').replace('\r\n', '\n').splitlines(True), t.splitlines(True), 'MountRider.cs (tree)', 'MountRider.cs.patched', n=1))
open(os.path.join(HERE, 'MountRider.diff'), 'w', encoding='utf-8', newline='').write(d)
print('base md5', hashlib.md5(raw).hexdigest()[:10], 'patched md5', hashlib.md5(out.encode('utf-8')).hexdigest()[:10], 'crlf', crlf, 'diff lines', d.count('\n'))

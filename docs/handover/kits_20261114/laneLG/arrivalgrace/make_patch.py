#!/usr/bin/env python3
"""Stages the ARRIVALGRACE v1 C# patch (NOT applied to Assets). Reads the tree ZoneData.cs + ZoneRuntime.cs, asserts each anchor is unique,
writes <name>.patched + <name>.diff next to this file. unity-dev: copy each .patched over its tree file (md5 check printed below), CODE-build.
WHY: ZoneRuntime.SpanEnter (the seamless-world zone switch, called by ZoneSpan.cs:202 when Joan walks z4->z5->z6) sets arriveGraceUntil = 0f, so the
8 s engage.graceAfterArriveSeconds (which already gates Engage in Poll, ZoneRuntime.cs:329) never runs on a walked arrival and the pre-boss raiders engage at once.
FIX: new engage keys in zones.json: spanGraceSeconds (default 0 = old behaviour, grace 0) and spanGraceZones (zone ids; empty = every zone). Default off."""
import os, difflib, hashlib
ROOT = r'C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\Zones'; HERE = os.path.dirname(os.path.abspath(__file__))
def patch(name, subs):
    raw = open(os.path.join(ROOT, name), 'rb').read(); crlf = b'\r\n' in raw; t = raw.decode('utf-8').replace('\r\n', '\n')
    base = t
    for a, b in subs:
        assert t.count(a) == 1, (name, 'anchor not unique', a[:70], t.count(a)); t = t.replace(a, b)
    out = t.replace('\n', '\r\n' if crlf else '\n').encode('utf-8')
    open(os.path.join(HERE, name + '.patched'), 'wb').write(out)
    d = ''.join(difflib.unified_diff(base.splitlines(True), t.splitlines(True), name + ' (tree)', name + '.patched', n=1))
    open(os.path.join(HERE, name + '.diff'), 'w', encoding='utf-8', newline='').write(d)
    print(name, 'base md5', hashlib.md5(raw).hexdigest()[:10], 'patched md5', hashlib.md5(out).hexdigest()[:10], 'crlf', crlf, 'diff lines', d.count('\n'))
patch('ZoneData.cs', [
    ('public float graceAfterArriveSeconds = 3f, corpseSeconds = 25f, spawnMinDistance = 30f; }',
     'public float graceAfterArriveSeconds = 3f, corpseSeconds = 25f, spawnMinDistance = 30f; public float spanGraceSeconds = 0f; public string[] spanGraceZones = new string[0]; /* ARRIVALGRACE v1: a seamless-span zone entry (SpanEnter) gets this many seconds of no-engage; 0 = old behaviour (no grace); spanGraceZones = zone ids it applies to, empty = all */ }')])
patch('ZoneRuntime.cs', [
    ('arriveGraceUntil = 0f; UpdateRift(true); LookSpawns(false); return true;',
     'arriveGraceUntil = SpanGrace(z); UpdateRift(true); LookSpawns(false); return true;   // ARRIVALGRACE v1 (was 0f)'),
    ('    public void SpanRestoreLimits()',
     '    /// <summary>ARRIVALGRACE v1: the engage grace after a walked (span) zone entry, from zones.json engage.spanGraceSeconds / spanGraceZones. 0 seconds = the old value 0 (Poll engages at once).</summary>\n'
     '    float SpanGrace(ZZone z)\n    {\n'
     '        var e = T.engage; if (e == null || e.spanGraceSeconds <= 0f || z == null) return 0f;\n'
     '        if (e.spanGraceZones != null && e.spanGraceZones.Length > 0 && System.Array.IndexOf(e.spanGraceZones, z.id) < 0) return 0f;\n'
     '        return Time.unscaledTime + e.spanGraceSeconds;\n    }\n'
     '    public void SpanRestoreLimits()')])

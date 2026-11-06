"""laneINTGATE hitch_gate2.py = laneHITCH hitch_gate.py with ONE change: the startup window is decided by the LOG ORDER, not by a wall-clock guess.
Why: hitch_gate.py falls back to 't >= 8 s' when '[Prewarm92] done' is absent (always, in harness runs), so v104 verify's frames 1 and 2 (the process's first two frames:
scene load 8182 ms at t 8.3 s, first present 1722 ms at t 9.9 s) landed after the guess and FAILED, while the same frames at 3 s in the other logs passed by luck.
Rule here: SLOW lines BEFORE the first '[StartupWarm] done' line (or, if that line is absent, frame number <= --startup-frames, default 4) are startup, listed not gated.
Everything else is as hitch_gate.py (limit 300 ms; capture-adjacent frames reported separately; '[Prewarm92] done' still moves the start if present). Usage: python -I hitch_gate2.py <log> [--limit 300] [--startup-frames 4]
Does NOT weaken the post-startup rule: any non-capture frame > limit after the StartupWarm line still FAILS."""
import sys, re
a = sys.argv[1:]; log = a[0]; lim = float(a[a.index('--limit') + 1]) if '--limit' in a else 300
sf = int(a[a.index('--startup-frames') + 1]) if '--startup-frames' in a else 4
lines = open(log, errors='ignore').read().splitlines()
warm = next((i for i, l in enumerate(lines) if '[StartupWarm] done' in l), None)
donet = None
for i, l in enumerate(lines):
    if '[Prewarm92] done total' in l:
        for m in lines[i:i + 40]:
            r = re.search(r'SLOW frame \d+ dt=\d+ms t=([\d.]+)s?', m)
            if r: donet = float(r.group(1)); break
        break
pre = []; harn = []; real = []
for i, l in enumerate(lines):
    m = re.match(r'SLOW frame (\d+) dt=(\d+)ms t=([\d.]+)', l)
    if not m: continue
    fr, dt, t = int(m[1]), int(m[2]), float(m[3])
    if dt <= lim: continue
    startup = (i < warm) if warm is not None else (fr <= sf)
    if donet is not None and t < donet: startup = True
    if startup: pre.append((fr, dt, t)); continue
    ctx = ' '.join(lines[max(0, i - 3):i + 4])
    (harn if re.search(r'\[VERIFY\]|SHOT |\.png|\[postureproof\] COLORS', ctx) else real).append((fr, dt, t))
print('log', log, '| startup end:', ('StartupWarm line %d' % (warm + 1)) if warm is not None else 'frame<=%d' % sf, '| prewarm-done t', donet, '| limit', lim)
print('startup (not gated):', pre)
print('post-startup, capture-adjacent (harness):', len(harn), harn[:6])
print('post-startup, other (GATE):', len(real), real[:8])
print('GATE', 'PASS' if not real else 'FAIL')
sys.exit(1 if real else 0)

#!/usr/bin/env python3
r"""laneINT107 zones_switch107.py: THE zone switch for the data files (the launcher flag `-zones` does the same for one run without touching files).
 zones.json, chase.json, zone_span.json top-level "enabled", Data + StreamingAssets together (6 files), text edit (nothing else changes), byte-identical copies kept.
  python -I zones_switch107.py --status [--root R]
  python -I zones_switch107.py --on  [--root R] [--apply]     zones + chase + zone span ON (chase and span are useless without zones, so there is no single-file mode)
  python -I zones_switch107.py --off [--root R] [--apply]     all three OFF = the old duel
 Dry-run default for --on/--off. Not touched: tail_spring.json (Sobek tail runtime, separate switch), zones' services (hearth/stall rows)."""
import argparse, json, os, sys
LIVE = (os.environ.get('FK_ROOT') or os.path.join(os.path.expanduser('~'), 'Documents', 'unity_test_hdrp'))
FILES = ('zones.json', 'chase.json', 'zone_span.json')
def top_enabled(t):
    """(start, end, value) of the depth-1 "enabled": true|false literal"""
    depth = 0; i = 0; n = len(t); instr = False
    while i < n:
        c = t[i]
        if instr:
            if c == '\\': i += 2; continue
            if c == '"': instr = False
            i += 1; continue
        if c == '"':
            if depth == 1 and t.startswith('"enabled"', i):
                j = i + 9
                while t[j] in ' \t\r\n': j += 1
                if t[j] == ':':
                    j += 1
                    while t[j] in ' \t\r\n': j += 1
                    for lit in ('true', 'false'):
                        if t.startswith(lit, j): return j, j + len(lit), lit == 'true'
            instr = True
        elif c in '{[': depth += 1
        elif c in '}]': depth -= 1
        i += 1
    return None
def main():
    ap = argparse.ArgumentParser(); ap.add_argument('--root', default=LIVE); g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument('--on', action='store_true'); g.add_argument('--off', action='store_true'); g.add_argument('--status', action='store_true'); ap.add_argument('--apply', action='store_true'); a = ap.parse_args()
    R = os.path.abspath(a.root); want = a.on; out = {}; st = {}
    for f in FILES:
        for sub in ('Data', 'StreamingAssets'):
            p = os.path.join(R, 'Assets', sub, f)
            if not os.path.exists(p): print('STOP %s/%s missing (merge the zone kits first)' % (sub, f)); return 2
            raw = open(p, 'rb').read(); t = raw.decode('utf-8-sig'); json.loads(t); r = top_enabled(t)
            if r is None: print('STOP %s/%s: no top-level "enabled"' % (sub, f)); return 2
            st[(f, sub)] = r[2]
            if not a.status and r[2] != want: out[p] = (t[:r[0]] + ('true' if want else 'false') + t[r[1]:], raw.startswith(b'\xef\xbb\xbf'))
    for f in FILES: print('%-14s Data %-5s StreamingAssets %-5s' % (f, str(st[(f, 'Data')]).lower(), str(st[(f, 'StreamingAssets')]).lower()))
    if a.status: return 0
    print('%s %d file(s)' % ('SET' if out else 'ALREADY', len(out)))
    if not a.apply: print('DRY RUN (add --apply)'); return 0
    for p, (t, bom) in out.items(): json.loads(t); open(p, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + t.encode('utf-8'))
    print('APPLIED zones %s' % ('ON' if want else 'OFF')); return 0
sys.exit(main())

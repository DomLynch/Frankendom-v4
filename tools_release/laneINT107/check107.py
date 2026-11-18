#!/usr/bin/env python3
"""laneINT107 check107.py --root R: asserts (1) no sobek_staged*.json / chase.SOBEK_ROW* in Assets\Data or StreamingAssets, (2) AIBrain.cs and AiDial.cs in <root> are NOT the stale laneSOBEKMOVES stage copies and carry the anchor hunks (+ CHASE one-liner in AIBrain)."""
import argparse, glob, os, sys
ap = argparse.ArgumentParser(); ap.add_argument('--root', default=(os.environ.get('FK_ROOT') or os.path.join(os.path.expanduser('~'), 'Documents', 'unity_test_hdrp'))); R = os.path.abspath(ap.parse_args().root); bad = 0
for sub in ('Data', 'StreamingAssets'):
    for pat in ('sobek_staged*', 'chase.SOBEK*', '*.WEAPON.json'):
        for f in glob.glob(os.path.join(R, 'Assets', sub, pat)): print('FAIL staged file in live data: ' + f); bad += 1
for n in ('AIBrain.cs', 'AiDial.cs'):
    live = open(os.path.join(R, 'Assets', 'Scripts', 'Combat', n), 'rb').read(); st = os.path.join(R, '_agents', 'laneSOBEKMOVES', 'stage', 'Assets', 'Scripts', 'Combat', n)
    if os.path.exists(st) and open(st, 'rb').read() == live: print('FAIL %s equals the stale SOBEKMOVES stage copy' % n); bad += 1
    t = live.decode('utf-8', 'replace')
    if 'SOBEKMOVES v1' not in t: print('FAIL %s lacks the SOBEKMOVES hunks' % n); bad += 1
if 'CHASE v1' not in open(os.path.join(R, 'Assets', 'Scripts', 'Combat', 'AIBrain.cs'), encoding='utf-8-sig').read(): print('FAIL AIBrain.cs lost the CHASE one-liner'); bad += 1
print('check107: %s' % ('PASS' if not bad else '%d FAIL' % bad)); sys.exit(1 if bad else 0)

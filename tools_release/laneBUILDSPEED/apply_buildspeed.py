#!/usr/bin/env python3
r"""apply_buildspeed.py - laneBUILDSPEED. Installs the editor-only build-chain helpers. DRY RUN by default.
  python -I apply_buildspeed.py            dry run: every hunk and whether its anchor is found exactly once
  python -I apply_buildspeed.py --apply    new file Assets\Editor\BuildSpeed.cs + 3 small call-site hunks (Zone2Build.cs, HdrpBatch.cs, MonsterBatch.cs); originals saved in bak\
  python -I apply_buildspeed.py --off      put the three files back byte for byte from bak\ and remove BuildSpeed.cs (moves it to bak\, never deletes)
  FPS_ROOT=<scratch> python -I apply_buildspeed.py --apply     rehearse on a scratch copy of Assets\Editor
Refuses --apply / --off while an Editor works on this project (pool\unity_busy.py). Never writes Assets in the dry run. No change in what the chain builds: the hunks only call
BuildTimer.Step (a log line) and Zone2BuildInc.Skip (false unless Tools\zone_skip.json is present and fresh)."""
import hashlib, os, shutil, sys, time
from pathlib import Path
LANE = Path(__file__).resolve().parent; P = Path(os.environ.get('FPS_ROOT', str(LANE.parents[1])))
ED = P / 'Assets' / 'Editor'; BAK = (P / 'bak') if os.environ.get('FPS_ROOT') else (LANE / 'bak')   # a rehearsal never shares the real backups
HUNKS = [
 ('Zone2Build.cs', 'if (row == null) continue; float D = row.offsetZ;', 'if (row == null) continue; if (Zone2BuildInc.Skip(row.root)) continue; float D = row.offsetZ;'),
 ('HdrpBatch.cs', 'UnityEngine.Debug.Log("PRODUCTION step " + name); a(); }', 'UnityEngine.Debug.Log("PRODUCTION step " + name); BuildTimer.Step(name, a); }'),
 ('MonsterBatch.cs', 'AttackClipSetup.Run();   // v109c CLAWMOTION', 'BuildTimer.Step("AttackClipSetup", () => AttackClipSetup.Run());   // v109c CLAWMOTION'),
 ('MonsterBatch.cs', '        DodgeClipSetup.Run();\n', '        BuildTimer.Step("DodgeClipSetup", () => DodgeClipSetup.Run());\n'),
 ('MonsterBatch.cs', '        EnemySwap.BuildScenesMenu();\n', '        BuildTimer.Step("EnemySwap.BuildScenesMenu", () => EnemySwap.BuildScenesMenu());\n'),
 ('MonsterBatch.cs', '        int miss = ZoneCreaturePrefab.SaveAll();\n', '        int miss = BuildTimer.Step("ZoneCreaturePrefab.SaveAll", () => ZoneCreaturePrefab.SaveAll());\n'),
]
NEW = LANE / 'stage' / 'Assets' / 'Editor' / 'BuildSpeed.cs'
def sha(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def busy():
    sys.path.append(str(P / '_agents' / 'pool'))
    try:
        import unity_busy; return unity_busy.unity_busy()
    except Exception as e:
        print('unity_busy not usable (%s): treating as busy' % e); return True
def main():
    apply_, off = '--apply' in sys.argv, '--off' in sys.argv
    if off:
        if not os.environ.get('FPS_ROOT') and busy(): print('REFUSED: Unity Editor busy on this project'); return 1
        BAK.mkdir(exist_ok=True)
        for f in sorted({h[0] for h in HUNKS}):
            b = BAK / f
            if b.exists(): shutil.copyfile(b, ED / f); print('restored', f)
        t = ED / 'BuildSpeed.cs'
        if t.exists(): shutil.move(str(t), str(BAK / ('BuildSpeed.cs.removed_' + time.strftime('%H%M%S')))); print('BuildSpeed.cs moved to bak\\')
        mt = ED / 'BuildSpeed.cs.meta'
        if mt.exists(): shutil.move(str(mt), str(BAK / ('BuildSpeed.cs.meta.removed_' + time.strftime('%H%M%S'))))
        return 0
    texts, ok = {}, True
    for f, old, new in HUNKS:
        t = texts.get(f) or (ED / f).read_text(encoding='utf-8-sig'); bom = (ED / f).read_bytes()[:3] == b'\xef\xbb\xbf'
        old = old.replace('\n', '\r\n') if '\r\n' in t else old; new = new.replace('\n', '\r\n') if '\r\n' in t else new
        n = t.count(old)
        print('%-4s %-16s anchor found %d time(s)  %s' % ('OK' if n == 1 else 'FAIL', f, n, old.strip()[:70]))
        if n != 1: ok = False; continue
        if new in t: print('     already applied'); ok = False; continue
        texts[f] = t.replace(old, new)
    if (ED / 'BuildSpeed.cs').exists(): print('BuildSpeed.cs already installed'); ok = False
    if not ok: print('NOT APPLIED (dry run or anchors differ)'); return 0 if not apply_ else 1
    if not apply_: print('DRY RUN: would write BuildSpeed.cs + %d hunks in %d files. Add --apply.' % (len(HUNKS), len(texts))); return 0
    if not os.environ.get('FPS_ROOT') and busy(): print('REFUSED: Unity Editor busy on this project (pool\\unity_busy.py)'); return 1
    BAK.mkdir(exist_ok=True)
    for f, t in texts.items():
        if not (BAK / f).exists(): shutil.copyfile(ED / f, BAK / f)
        raw = (ED / f).read_bytes(); bom = raw[:3] == b'\xef\xbb\xbf'
        (ED / f).write_bytes((b'\xef\xbb\xbf' if bom else b'') + t.encode('utf-8'))
    shutil.copyfile(NEW, ED / 'BuildSpeed.cs')
    print('APPLIED: BuildSpeed.cs + hunks; revert with --off. Unity imports the new file at the next refresh.'); return 0
sys.exit(main())

#!/usr/bin/env python3
"""laneINT107 build_chain107.py: rehearses the zone-2 / Sobek / chase stack on the CURRENT base = laneINTFPS chain + laneINTGATE (tree laneINTGATE\\scratch\\gate1, read only) in ONE scratch tree.
  python -I build_chain107.py <name>            copy gate1 -> scratch\\<name>, copy kit lanes, sandbox their paths, run the chain
  python -I build_chain107.py <name> --emit     write out\\live_commands.ps1 (nothing run)
Kit scripts live in <name>\\_agents (copies, live paths rewritten to the scratch root): no apply script can write to live. Writes only under laneINT107."""
import sys, os, shutil, subprocess, fnmatch, json, hashlib, time
from pathlib import Path
LIVE = Path((os.environ.get('FK_ROOT') or os.path.join(os.path.expanduser('~'), 'Documents', 'unity_test_hdrp'))); AG = LIVE / '_agents'; HERE = AG / 'laneINT107'; BASE = AG / 'laneINTGATE' / 'scratch' / 'gate1'
A = sys.argv[1:]; name = A[0]; D = HERE / 'scratch' / name; EMIT = '--emit' in A
LANES = ['laneZONES', 'laneCHASE', 'laneZONE2DUP', 'laneZONESPAN', 'laneSOBEKDATA', 'laneSOBEKBIND', 'laneSOBEKTUNE', 'laneSOBEKGETUP', 'laneSOBEKMOVES', 'laneSOBEKWEAPON',
         'laneHCKIT7', 'laneINT104', 'laneNODRAC', 'laneHCAUDIT2', 'laneLADDER']
SKIPDIR = {'proj', 'proj2', 'proj3', 'scratch', 'out', 'out2', 'out3', 'work', 'compile_out', 'frames', 'backup', 'backup_scratch', 'evidence', '__pycache__', 'raw', 'kitcopy', '_bak', 'live_copy', 'v104_copy', 'testdebris', 'bak', 'stills',
           'logs', 'renders', 'clipbuild_out', 'clipsets_out', 'out_a', 'out_b', 'out_c', 'out_d', 'out_e', 'out_f', 'out_probe', 'scratch_proj', 'trident', 'selftest', 'base', 'snaps', 'projB', 'diskaudit'}
SKIPFILE = ('*.png', '*.dll', '*.jpg', '*.exe', '*.wav', '*.mp4', '*.blend', '*.blend1', '*.obj', '*.fbx')
KEEPGLB_FILES = ('Sobek_attacks_v2.glb', 'Sobek_combat.glb', 'Sobek_anim.glb', 'sobek_trident_LOD0.glb', 'sobek_trident_LOD1.glb', 'Sobek_Head.cutlod.glb')
def ign(d, names):
    r = []
    for n in names:
        p = Path(d) / n
        if p.is_dir() and (n in SKIPDIR or n.startswith('backup_') or n.startswith('_backup')): r.append(n)
        elif p.is_file():
            l = n.lower()
            if l.endswith('.glb') and n not in KEEPGLB_FILES: r.append(n)
            elif any(fnmatch.fnmatch(l, s) for s in SKIPFILE): r.append(n)
    return r
def copy_base():
    if D.exists(): print('REFUSED: %s exists (never overwrites; pick a new name)' % D); sys.exit(2)
    # BASE = a true copy of LIVE (INT107 16:55: live is INTFPS + FloorHunt + FEETCAP; laneINTGATE is NOT applied in live) - same copy as laneINT106's copy_base
    for sub in ('Scripts', 'Editor', 'Data'): shutil.copytree(LIVE / 'Assets' / sub, D / 'Assets' / sub, ignore=shutil.ignore_patterns('*.meta'))
    (D / 'Assets' / 'StreamingAssets').mkdir(parents=True)
    for p in (LIVE / 'Assets' / 'StreamingAssets').glob('*.json'): shutil.copy2(p, D / 'Assets' / 'StreamingAssets' / p.name)
    (D / 'Assets' / 'HDRP').mkdir(); shutil.copy2(LIVE / 'Assets' / 'HDRP' / 'HDRP_Max.asset', D / 'Assets' / 'HDRP' / 'HDRP_Max.asset')
    (D / 'ProjectSettings').mkdir()
    for f in ('QualitySettings.asset', 'ProjectSettings.asset', 'TagManager.asset'): shutil.copy2(LIVE / 'ProjectSettings' / f, D / 'ProjectSettings' / f)
    (D / 'Packages').mkdir(); [shutil.copy2(LIVE / 'Packages' / f, D / 'Packages' / f) for f in ('manifest.json', 'packages-lock.json')]
    if (LIVE / 'Packages' / 'com.unity.cloud.gltfast').exists(): shutil.copytree(LIVE / 'Packages' / 'com.unity.cloud.gltfast', D / 'Packages' / 'com.unity.cloud.gltfast', ignore=shutil.ignore_patterns('Tests', 'Samples*', 'Documentation*'))
    (D / 'Tools').mkdir()
    for p in (LIVE / 'Tools').glob('*'):
        if p.is_file() and p.suffix in ('.py', '.ps1', '.json', '.txt'): shutil.copy2(p, D / 'Tools' / p.name)
    for l in LANES:
        src = AG / l
        if src.exists(): shutil.copytree(src, D / '_agents' / l, ignore=ign)
    # evidence files the kits READ (lane out\ dirs are skipped by ign)
    for s in (AG / 'laneSOBEKMOVES' / 'out').glob('*.json'): (D / '_agents' / 'laneSOBEKMOVES' / 'out').mkdir(parents=True, exist_ok=True); shutil.copy2(s, D / '_agents' / 'laneSOBEKMOVES' / 'out' / s.name)
def sandbox():
    n = 0; L = str(LIVE); P = str(D)
    for p in sorted((D / '_agents').rglob('*.py')):
        t = p.read_bytes().decode('utf-8', 'replace'); t0 = t
        t = t.replace("['tasklist'", "['cmd', '/c', 'rem', 'tasklist'").replace('["tasklist"', "['cmd', '/c', 'rem', 'tasklist'")   # the unity-dev's running Unity must not refuse a scratch rehearsal
        if L in t:
            t = t.replace(P, '\x00P\x00').replace(L + '\\_agents', P + '\\_agents').replace(L, P).replace('\x00P\x00', P)
        if t != t0: p.write_bytes(t.encode('utf-8')); n += 1
    print('sandboxed %d kit script(s) to %s' % (n, D))
def thash():
    h = {}
    for sub in ('Assets', 'Tools', 'ProjectSettings', 'Packages'):
        for p in (D / sub).rglob('*'):
            if p.is_file(): h[str(p.relative_to(D)).replace(chr(92), '/')] = hashlib.md5(p.read_bytes()).hexdigest()
    return h
fails = []; LOG = []; TOUCH = {}; EM = []
def run(label, args, ok=(0,)):
    if EMIT and label.startswith('107.09'):
        EM.append(('107.09 laneSOBEKUNITY replaces the rehearsal --enable (Unity batch: controller, scene HadesSlice_Sobek.unity, materials, grip; its pre step runs apply_sobek --enable on the MOVES + split-head bundle)', 'Push-Location _agents\\laneSOBEKUNITY\\stage; .\\run_sobek_unity.ps1   # dry run first; then -Apply with Unity CLOSED (it launches the one Unity batch itself); Pop-Location'))
        EM.append(('107.09b extras: chase.json Sobek 0.92 + AIBrain/AiDial weaponReach hunks (SOBEKUNITY does not apply the hunks)', 'python -I _agents\\laneINT107\\apply_sobek107.py --extras --apply')); return None
    if EMIT:
        a = [str(x) for x in args]; out = []; i = 0
        while i < len(a):
            if a[i] in ('--root', '--tree') and i + 1 < len(a) and a[i + 1].startswith(str(D)): i += 2; continue
            if a[i] in ('--force', '--rehearse'): i += 1; continue
            out.append(a[i]); i += 1
        out[0] = str(Path(out[0]).relative_to(D)) if str(out[0]).startswith(str(D)) else (str(Path(out[0]).relative_to(LIVE)) if str(out[0]).startswith(str(LIVE)) else out[0])
        EM.append((label, 'python -I ' + ' '.join(out))); return None
    time.sleep(1.1)
    before = thash(); e = dict(os.environ); e['HCKIT7_LIVE'] = str(D); e['HCKIT2_LIVE'] = str(D); e['PYTHONIOENCODING'] = 'utf-8'
    r = subprocess.run([sys.executable, '-I'] + [str(x) for x in args], cwd=str(D), capture_output=True, text=True, env=e, errors='replace')
    tail = (r.stdout.strip().splitlines() or [''])[-1][:150]
    print('%s %-44s exit %d | %s' % ('OK  ' if r.returncode in ok else 'FAIL', label, r.returncode, tail))
    after = thash(); TOUCH[label] = sorted(k for k in set(before) | set(after) if before.get(k) != after.get(k))
    LOG.append('==== %s | %s | exit %d\n%s\n%s\n' % (label, ' '.join(str(x) for x in args), r.returncode, r.stdout, r.stderr))
    if r.returncode not in ok:
        fails.append(label); print('\n'.join('      ' + x[:200] for x in (r.stdout + r.stderr).strip().splitlines()[-10:]))
    return r
def chain():
    ag = D / '_agents'; R = str(D); H = HERE; I104 = ag / 'laneINT104'
    run('107.01 ZONES (GameLoop.cs + ArenaLimits.cs hunks, 9 new files; zones.json enabled:false)', [ag / 'laneZONES/apply_zones.py', '--root', R, '--apply', '--force'])
    run('107.02 CHASE (ZoneRuntime replace, ChaseAgent/ChaseCore, AIBrain/DuelFighter one-liners; chase.json enabled:false)', [H / 'apply_chase107.py', '--root', R, '--apply'])
    run('107.03 ZONE2DUP (ZoneRuntime 2 hunks, zone_span v1, layout, zones.json values)', [ag / 'laneZONE2DUP/apply_zone2dup.py', '--root', R, '--apply', '--force'])
    run('107.04 ZONESPAN (4 C# files replaced, zone_span v2, zone_plan; supersedes ZONE2DUP C#/data)', [ag / 'laneZONESPAN/apply_zonespan.py', '--root', R, '--apply', '--force'])
    run('107.05 SOBEKDATA part 1+2 on the MERGED bundle (inert files + ladder support)', [H / 'apply_sobek107.py', '--root', R, '--part12', '--apply'])
    run('107.06 SOBEKBIND hdrp_char_mats extract + material rows', [ag / 'laneSOBEKBIND/merge_hdrp_rows.py', '--apply'])
    run('107.07 SOBEKTUNE (clip_build blocks, tail_spring, 3 Tools patches)', [ag / 'laneSOBEKTUNE/apply_tune.py', '--root', R, '--apply'])
    run('107.08 SOBEKGETUP (hipsLiftM + G-floor, 2 Tools files, 2 glb)', [H / 'apply_sobekgetup107.py', '--root', R, '--apply'])
    run('107.09 SOBEKMOVES = enable rows on the merged bundle + v1 target + attacks glb + chase speed + AIBrain/AiDial hunks (REHEARSAL: asset check skipped)', [H / 'apply_sobek107.py', '--root', R, '--enable', '--rehearse', '--apply'])
    run('107.10 tuning profile key sobek_own -> scaled_own (data_gates name-in-key; pure data, the code reads the key through characters.<id>.tuning)', [H / 'apply_tunekey107.py', '--root', R, '--apply'])
    run('107.11 ladder row off while zones are on (inLadder:false on the sobek row + 2-line GameLoop skip)', [H / 'apply_ladderoff107.py', '--root', R, '--apply'])
    run('107.12 ZONES z2_sobek.ladderRow = sobek', [H / 'apply_sobekzone107.py', '--root', R, '--apply'])
    run('107.12b DOOR open from the start + maxEngaged 2 + retarget + finisher guard (Dom 08:19)', [H / 'apply_multi107.py', '--root', R, '--apply'])
    run('107.12c SOBEK SPAWN FIXES: creature.scene + prefab SaveAll/BatchSave, per-object attach (9 attachers) after Instantiate, chase rows start from the * row', [H / 'apply_spawnfix107.py', '--root', R, '--apply'])
    if EMIT:
        EM.append(('107.09a PREFAB (hard check): Sobek prefab from HadesSlice_Sobek, then fail the list if the file is missing', r'$p = Start-Process -Wait -PassThru "C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe" -ArgumentList "-batchmode","-projectPath",(Get-Location).Path,"-executeMethod","ZoneCreaturePrefab.BatchSave","-logFile","_agents\laneINT107\out\prefab_save.log"; if ($p.ExitCode -ne 0 -or -not (Test-Path Assets\Resources\Creatures\Sobek.prefab)) { throw "Sobek prefab missing: zones will skip Sobek" }'))
    run('107.13 SOBEKFIN STAGE0 safe finisher rows (3)', [H / 'apply_sobekfin107.py', '--root', R, '--apply'])
    run('107.14 zone flag: one launcher flag -zones / -nozones (ZoneFlag.cs + 3 one-line hunks)', [H / 'apply_zoneflag107.py', '--root', R, '--apply'])
    run('107.15a key refill: make_patches', [ag / 'laneHCKIT7/make_patches.py'])
    run('107.15b key refill apply', [I104 / 'apply_key_refill.py', '--root', R, '--apply'])
    run('107.16 move *.bak_sobekbind strays out of Data/StreamingAssets', [H / 'apply_strays107.py', '--root', R, '--apply'])
    run('107.17 check: no staged sobek/chase files in live Data, AIBrain/AiDial are not the stale stage copies', [H / 'check107.py', '--root', R])
    # 107.99 HCAUDIT2 --move-strays is NOT run: with the current Tools\data_gates.py it turns 6 editor-side json (character_setup, joan_swap, mhhead_attach, occlusion, perf_mem_build, v1_finisher_clips) into 'only in Data' FAILs; INTFPS/INTGATE do not run it either. Dom/Strategy call.
if EMIT:
    chain(); L = ['# laneINT107 live command list (build_chain107.py --emit). Run from %s on the LIVE tree as it is now (INTFPS + FloorHunt + FEETCAP applied; laneINTGATE is NOT applied and is not needed by this chain),' % LIVE,
                  '# Unity closed, no build/gate running, no CLAIM on the board. Every script is dry-run by default: run each line first WITHOUT --apply. Order matters.']
    for lab, c in EM: L += ['# ' + lab, c, 'Start-Sleep 2']
    L += [r'# ===== gates (compile in Unity first: expect 0 errors) =====', r'python -I Tools\missing_keys.py --quiet', r'python -I Tools\data_gates.py --quiet   # expect 0 FAIL after the Unity block (the Sobek controller FAIL goes away)', r'python -I _agents\laneNODRAC\tools\no_dracula_check.py --root . --quiet',
          r'python -I _agents\laneZONES\zones_check.py', r'python -I _agents\laneZONESPAN\zone_span_check.py',
          r'# ===== Unity menus (Unity dev, REPORT section 5): Hades > Zones > Save creature prefabs (with HadesSlice_Sobek.unity open, then HadesSlice.unity), Hades > Zones > Build zone 2 (duplicate) =====',
          r'# ===== PLAY: .\_agents\laneINT107\play_zones.ps1 -Build <exe>   (= exe -zones).  Switch in files: python -I _agents\laneINT107\zones_switch107.py --on --apply | --off --apply =====']
    (HERE / 'out' / 'live_commands.ps1').write_text('\n'.join(L) + '\n', encoding='utf-8'); print('\n'.join(L)); sys.exit(0)
copy_base(); sandbox(); chain()
(HERE / 'out').mkdir(exist_ok=True); (HERE / 'out' / (name + '_steps.txt')).write_text('\n'.join(LOG), encoding='utf-8'); (HERE / 'out' / (name + '_touch.json')).write_text(json.dumps(TOUCH, indent=1), encoding='utf-8')
print('RESULT: %s' % ('ALL STEPS OK' if not fails else 'FAILED STEPS: ' + ', '.join(fails)))
sys.exit(1 if fails else 0)

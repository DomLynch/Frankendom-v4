#!/usr/bin/env python3
"""F3 kits (boss rewards), lgkit style. Staged by the F3 agent 2026-11-14, FILE-ONLY, nothing applied. DRY RUN BY DEFAULT. HOT, data only.
Base = live HDRP_v109lx tree twins (Assets\\Data == Assets\\StreamingAssets): gear.json d1fb8581f4, abilities.json 349b473f51, boss_phase.json 793a9a7f29.
Kits: sigbash (abilities.json), sigaxe (gear.json), tells (boss_phase.json). No armor / poise / damage / speed / reach number is touched anywhere.
  python -I f3kit.py <kit> make
  python -I f3kit.py <kit> apply  [--target DIR] [--go]    DIR = a StreamingAssets-like dir (QA copy / scratch); none = BOTH tree twins
  python -I f3kit.py <kit> revert [--target DIR] [--go]    byte-identical; refuses unless the file == the kit's after md5
  python -I f3kit.py proof                                  scratch-copy round trip of every kit (apply -> parse -> revert -> md5 equal), run twice, + all three stacked
make asserts json(after) == mutate(json(base)). Refuses any target inside Builds\\ (live / launcher / rollback builds): use a QA copy."""
import os, re, sys, json, shutil, hashlib, time
REPO = r'C:\Users\dom12\Documents\unity_test_hdrp'; HERE = os.path.dirname(os.path.abspath(__file__))
TWINS = [os.path.join(REPO, 'Assets', 'Data'), os.path.join(REPO, 'Assets', 'StreamingAssets')]
def md5(b): return hashlib.md5(b).hexdigest()[:10]
def rd(p): return open(p, 'rb').read()
def row(lst, k, v): return [x for x in lst if x.get(k) == v][0]
def J(rx, new, mut):
    """rx has a named empty-or-literal group 'old' = the span replaced by the literal `new`; mut(parsed_json) applies the same change to the parsed base."""
    return {'rx': rx, 'new': new, 'mut': mut}
# ---- sigbash: the trident is a ONE-handed weapon (shield stays on), but shield_bash (slot 2, "C") lists boar_spear and not sobek_trident -> with the trident the C slot has no row (the HUD shows the hud.json kick icon and the key does nothing)
def _bash(d): row(d['abilities'], 'id', 'shield_bash')['weapons'].insert(3, 'sobek_trident')
KITS = {'sigbash': {'file': 'abilities.json', 'baseMd5': '349b473f51', 'edits': [
    J(r'("id":\s*"shield_bash".*?"weapons":\s*\[\s*"",\s*"knight_sword",\s*"boar_spear",)(?P<old>)', '\n        "sobek_trident",', _bash)]}}
# ---- sigaxe: Erlking drops his antler great-axe next to cap + bascinet (live gear.json already has the erlking_axe ability rows via sigabil, the market row via mkt_z7, the glb, rarity relic)
_ek = r'"id":\s*"erlking_axe",\s*"shield".*?'
def _ax1(d): row(d['items'], 'id', 'erlking_axe')['dropFrom'] = ['Erlking']
def _ax2(d): row(d['items'], 'id', 'erlking_axe')['dropChance'] = 1.0
def _ax3(d): r = row(d['drop']['maxPerKillBy'], 'victim', 'Erlking'); assert r['max'] == 2; r['max'] = 3
KITS['sigaxe'] = {'file': 'gear.json', 'baseMd5': 'd1fb8581f4', 'edits': [
    J(r'(' + _ek + r'"dropFrom":\s*\[)(?P<old>\s*)(?=\])', '"Erlking"', _ax1),
    J(r'(' + _ek + r'"dropChance":\s*)(?P<old>0\.0)(?=,)', '1.0', _ax2),
    J(r'("victim":\s*"Erlking",\s*"max":\s*)(?P<old>2)(?=\s*\})', '3', _ax3)]}
# ---- tells: Pitborn and Humbaba phase rows have telegraph {} = no ground ring when the phase starts (Sobek/Grendel/Erlking have one). Same ring numbers as the nearest filled rows; a visual tell only.
def _tel(b, rad, sec):
    def m(d): r = row(d['rows'], 'id', b); assert r['telegraph'] == {}; r['telegraph'] = {'radiusM': rad, 'seconds': sec}
    return J(r'("id":\s*"%s".*?"telegraph":\s*)(?P<old>\{\})' % b, '{\n    "radiusM": %s,\n    "seconds": %s\n   }' % (rad, sec), m)
KITS['tells'] = {'file': 'boss_phase.json', 'baseMd5': '793a9a7f29', 'edits': [_tel('Pitborn', 2.4, 0.6), _tel('Humbaba', 3.0, 0.7)]}
def build(g, src=None):
    s = KITS[g]; f = s['file']
    a = rd(os.path.join(src, f)) if src else rd(os.path.join(TWINS[0], f))
    if not src and a != rd(os.path.join(TWINS[1], f)): sys.exit('twins differ: ' + f)
    if md5(a) != s['baseMd5']: sys.exit('BASE CHANGED since staging: %s %s (staged on %s)' % (f, md5(a), s['baseMd5']))
    t = a.decode('utf-8'); nt = t; pa = json.loads(t.lstrip('\ufeff'))
    for e in s['edits']:
        ms = list(re.finditer(e['rx'], nt, re.S))
        if len(ms) != 1: sys.exit('EDIT FAILED: %d matches for %s' % (len(ms), e['rx']))
        m = ms[0]; nt = nt[:m.start('old')] + e['new'] + nt[m.end('old'):]; e['mut'](pa)
    pb = json.loads(nt.lstrip('\ufeff')); assert pa == pb, 'after != base + the listed edits'
    return a, nt.encode('utf-8')
def guard(d):
    bd = os.path.normcase(os.path.abspath(d))
    if os.path.normcase(os.path.join(REPO, 'Builds')) in bd: sys.exit('REFUSED: %s is inside Builds\\ . Use a QA copy or a scratch dir.' % d)
def run(g, cmd, tg, go, quiet=False):
    f = KITS[g]['file']; GD = os.path.join(HERE, g)
    if cmd == 'make':
        base, after = build(g); os.makedirs(os.path.join(GD, 'base'), exist_ok=True); os.makedirs(os.path.join(GD, 'after'), exist_ok=True)
        open(os.path.join(GD, 'base', f), 'wb').write(base); open(os.path.join(GD, 'after', f), 'wb').write(after)
        json.dump({'file': f, 'base': md5(base), 'after': md5(after)}, open(os.path.join(GD, 'MD5.json'), 'w'), indent=1)
        print(g, f, 'md5', md5(base), '->', md5(after)); return
    man = json.load(open(os.path.join(GD, 'MD5.json'))); rev = cmd == 'revert'
    dirs = [tg] if tg else TWINS
    for d in dirs: guard(d)
    src, want = ('base', 'after') if rev else ('after', 'base')
    assert md5(rd(os.path.join(GD, 'base', f))) == man['base'] and md5(rd(os.path.join(GD, 'after', f))) == man['after'], 'kit files changed after make'
    for d in dirs:
        cur = md5(rd(os.path.join(d, f))); ok = cur == man[want]
        if not quiet: print('%-7s %s %-16s now %s (expect %s %s) -> %s %s' % ('OK' if ok else ('ALREADY' if cur == man[src] else 'BAD'), os.path.basename(d.rstrip('\\')), f, cur, want, man[want], src, man[src]))
        if not ok and cur != man[src]: sys.exit('REFUSED: %s in %s is neither the kit base nor its after' % (f, d))
    if not go:
        if not quiet: print('DRY RUN (%s %s): nothing written. Add --go.' % (g, cmd.upper()))
        return
    stamp = time.strftime('%Y%m%d_%H%M%S'); bk = os.path.join(GD, 'backup', stamp)
    for i, d in enumerate(dirs):
        os.makedirs(os.path.join(bk, str(i)), exist_ok=True); shutil.copy2(os.path.join(d, f), os.path.join(bk, str(i), f)); shutil.copyfile(os.path.join(GD, src, f), os.path.join(d, f))
    open(os.path.join(GD, 'STATE.txt'), 'a', encoding='utf-8').write('%s %s %s dirs=%s backup=%s\n' % (stamp, g, cmd.upper(), dirs, bk))
    if not quiet: print('WROTE %s into %s; backup %s' % (src, dirs, bk))
def proof():
    """scratch copy of the three base files; every kit applied -> parsed -> reverted -> md5 compared, twice; then all three stacked and reverted in reverse order. Writes PROOF_KITS.txt."""
    sc = os.path.join(HERE, '_scratch'); shutil.rmtree(sc, ignore_errors=True); os.makedirs(sc); out = []
    for g in KITS:
        f = KITS[g]['file']; shutil.copyfile(os.path.join(TWINS[0], f), os.path.join(sc, f))
    orig = {KITS[g]['file']: md5(rd(os.path.join(sc, KITS[g]['file']))) for g in KITS}
    for g in KITS: run(g, 'make', None, False)
    def chk(g, tag, want_key):
        m = json.load(open(os.path.join(HERE, g, 'MD5.json'))); f = KITS[g]['file']; cur = md5(rd(os.path.join(sc, f)))
        assert cur == m[want_key], (g, tag, cur, m[want_key]); out.append('%-8s %-22s %s %s OK' % (g, tag, f, cur))
    for rnd in (1, 2):
        for g in KITS:
            f = KITS[g]['file']; chk_before = md5(rd(os.path.join(sc, f)))
            run(g, 'apply', sc, True, True); chk(g, 'round %d applied' % rnd, 'after'); json.loads(rd(os.path.join(sc, f)).decode('utf-8').lstrip('\ufeff'))
            out.append('%-8s %-22s parses as JSON OK' % (g, 'round %d applied' % rnd))
            run(g, 'revert', sc, True, True); chk(g, 'round %d reverted' % rnd, 'base'); assert md5(rd(os.path.join(sc, f))) == chk_before == orig[f]
            # byte compare against the untouched tree file
            assert rd(os.path.join(sc, f)) == rd(os.path.join(TWINS[0], f)); out.append('%-8s %-22s byte-identical to tree file OK' % (g, 'round %d reverted' % rnd))
    for g in KITS: run(g, 'apply', sc, True, True)
    out.append('STACK    all three applied: ' + ', '.join('%s %s' % (KITS[g]['file'], md5(rd(os.path.join(sc, KITS[g]['file'])))) for g in KITS))
    for g in reversed(list(KITS)): run(g, 'revert', sc, True, True)
    for g in KITS: assert md5(rd(os.path.join(sc, KITS[g]['file']))) == orig[KITS[g]['file']] and rd(os.path.join(sc, KITS[g]['file'])) == rd(os.path.join(TWINS[0], KITS[g]['file']))
    out.append('STACK    reverted in reverse order: all three byte-identical to the tree OK')
    # semantic checks on the afters
    a = json.loads(rd(os.path.join(HERE, 'sigbash', 'after', 'abilities.json')).decode().lstrip('\ufeff')); assert 'sobek_trident' in row(a['abilities'], 'id', 'shield_bash')['weapons']
    g2 = json.loads(rd(os.path.join(HERE, 'sigaxe', 'after', 'gear.json')).decode().lstrip('\ufeff')); ek = row(g2['items'], 'id', 'erlking_axe'); assert ek['dropFrom'] == ['Erlking'] and ek['dropChance'] == 1.0
    b = json.loads(rd(os.path.join(HERE, 'sigaxe', 'base', 'gear.json')).decode().lstrip('\ufeff'))
    for it, it0 in zip(g2['items'], b['items']): assert it.get('stats') == it0.get('stats') and it.get('weapon') == it0.get('weapon'), it['id']   # no stat / weapon number moved
    out.append('CHECK    sigaxe: every item stats{} and weapon{} block equal to base (no armor/poise/damage number moved) OK')
    open(os.path.join(HERE, 'PROOF_KITS.txt'), 'w').write('\n'.join(out) + '\n'); print('\n'.join(out))
def main():
    if len(sys.argv) >= 2 and sys.argv[1] == 'proof': return proof()
    if len(sys.argv) < 3 or sys.argv[1] not in KITS or sys.argv[2] not in ('make', 'apply', 'revert'): print(__doc__); return
    tg = sys.argv[sys.argv.index('--target') + 1] if '--target' in sys.argv else None
    run(sys.argv[1], sys.argv[2], tg, '--go' in sys.argv)
main()

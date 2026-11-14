#!/usr/bin/env python3
"""F5 kits (base = live HDRP_v109lv, tree twins equal lv). Staged by the F5 agent, 2026-11-14. DRY RUN BY DEFAULT. lgkit style, but a kit may touch several files.
Kits (all DATA = hot, see PATCHES.md for which also need a CODE build before they do anything):
  f5plain     hub.json + chase.json + zone_span.json enabled true: a plain HadesSlice.exe launch (Steam, no flags) becomes the same game as PLAY.bat (-zones). DATA only, needs a Lead yes.
  f5gfx       quality_tiers.json userScale.enabled true + ultra2k label "Ultra 2K (upscaled)"; game_settings.json graphics.tier hint told truthfully. Pairs with CODE userscale + kit f5menu.
  f5menu      menu.json: Render scale + Upscaler rows in Graphics, D-pad axes for menu navigation (the dpad keys need CODE dpadnav; the rows need f5gfx, the code userscale makes them show the truth).
  f5menuboot  = f5menu + menu.json boot{} (title screen proxy: the pause menu opens once at boot). Needs CODE bootmenu. Pick ONE of f5menu / f5menuboot (same base, revert one before the other).
  f5card      loading_card.json maxSeconds 4 -> 14 (interim, works today: the card stops giving up before the StartupWarm cover) + waitForStartupWarm true (needs CODE cardwait).
  f5start     zones.json pit_yard arrive_start pos [] -> [0.0, 8.6]: Joan starts 7.6 m from Pitborn (aggro 5.5) instead of 3.6 m, so the first fight is not on frame 1. MOVES THE STORE-PAGE FRAME: frame check first.
  f5loot      loot_ui.json screen.blockWhileMenu true (needs CODE lootpad): pad Start no longer opens the pause menu AND the equip screen together.
  python -I f5kit.py <kit> make [--rebase]
  python -I f5kit.py <kit> apply  [--target DIR] [--go]    DIR = StreamingAssets of a QA copy (or any scratch dir holding the files); none = BOTH tree twins
  python -I f5kit.py <kit> revert [--target DIR] [--go]    byte-identical; refuses unless every file == the kit's after md5
  python -I f5kit.py <kit> show                            print the edited lines (base -> after)
Every edit is one literal in the raw text; make asserts after == base + exactly those values (JSON equality). Refuses a target inside a build named by Builds\\PLAY*.bat and Builds\\Play."""
import os, re, sys, json, glob, shutil, hashlib, time
REPO = r'C:\Users\dom12\Documents\unity_test_hdrp'; HERE = os.path.dirname(os.path.abspath(__file__))
TWINS = [os.path.join(REPO, 'Assets', 'Data'), os.path.join(REPO, 'Assets', 'StreamingAssets')]


def B(rx, old, new, path):   # bool swap (true/false literal)
    return {'kind': 'bool', 'rx': rx, 'old': old, 'new': new, 'path': path}


def N(rx, old, new, path):   # number literal swap
    return {'kind': 'num', 'rx': rx, 'old': old, 'new': new, 'path': path}


def S(rx, new, path):        # string swap: named group 'old' = the string contents
    return {'kind': 'str', 'rx': rx, 'new': new, 'path': path}


def I(rx, text, parent, key, val):   # insert text after the match, JSON: parent[key] = val
    return {'kind': 'ins', 'rx': rx, 'text': text, 'parent': parent, 'key': key, 'val': val}


def LT(rx, new, path, val):   # literal swap of the named group 'old' (a list / object literal); JSON: path = val
    return {'kind': 'lit', 'rx': rx, 'new': new, 'path': path, 'val': val}


def LI(rx, text, path, idx, val):    # insert text after the match, JSON: list at path gets val at idx
    return {'kind': 'lins', 'rx': rx, 'text': text, 'path': path, 'idx': idx, 'val': val}


_ROW = ('    {\n     "type": "gs",\n     "id": "%s",\n     "fmt": "0",\n     "max": 1,\n     "step": 0.05,\n     "scale": 1\n    },\n')
_ROWJ = lambda i: {'type': 'gs', 'id': i, 'fmt': '0', 'max': 1, 'step': 0.05, 'scale': 1}
_MENU = [
    LI(r'("id": "graphics.tier",.*?"scale": 1\n    \},\n)', _ROW % 'graphics.renderScale', ['tabs|id=graphics', 'rows'], 1, _ROWJ('graphics.renderScale')),
    LI(r'("id": "graphics.renderScale",.*?"scale": 1\n    \},\n)', _ROW % 'graphics.upscaler', ['tabs|id=graphics', 'rows'], 2, _ROWJ('graphics.upscaler')),
    I(r'("axisY": "Vertical",\n)', '  "dpadAxisX": "DPadX",\n', ['input'], 'dpadAxisX', 'DPadX'),
    I(r'("dpadAxisX": "DPadX",\n)', '  "dpadAxisY": "DPadY",\n', ['input'], 'dpadAxisY', 'DPadY'),
]
_BOOT = {'enabled': True, 'title': 'FRANKENDOM', 'resumeLabel': 'Continue', 'footer': 'Up / Down  Select      Enter / A  Confirm', 'afterCardS': 0.3, 'maxWaitS': 60.0}
_BOOTTXT = (' "boot": {\n  "_about": "F5 BOOTMENU (MenuUI.cs): the pause menu opens once by itself when the loading card and the StartupWarm cover are gone, with this title / Continue label / footer, so a new player meets a menu (Continue, Settings, Controls, New game, Quit) instead of a fight. enabled false = off. skipArgs (code default) keeps proof and capture runs out of it.",\n'
            '  "enabled": true,\n  "title": "FRANKENDOM",\n  "resumeLabel": "Continue",\n  "footer": "Up / Down  Select      Enter / A  Confirm",\n  "afterCardS": 0.3,\n  "maxWaitS": 60.0\n },\n')
_BOOTJ = dict(_BOOT); _BOOTJ = {'_about': 'F5 BOOTMENU (MenuUI.cs): the pause menu opens once by itself when the loading card and the StartupWarm cover are gone, with this title / Continue label / footer, so a new player meets a menu (Continue, Settings, Controls, New game, Quit) instead of a fight. enabled false = off. skipArgs (code default) keeps proof and capture runs out of it.', **_BOOT}
_TOP = r'(?m)(^  "enabled":\s*)false(?=,)'
KITS = {
    'f5plain': {'files': {
        'hub.json': {'baseMd5': 'e18c6356d4', 'edits': [B(_TOP, 'false', 'true', ['enabled'])]},
        'chase.json': {'baseMd5': 'e14b414b9e', 'edits': [B(_TOP, 'false', 'true', ['enabled'])]},
        'zone_span.json': {'baseMd5': '06af47e058', 'edits': [B(_TOP, 'false', 'true', ['enabled'])]}}},
    'f5gfx': {'files': {
        'quality_tiers.json': {'baseMd5': '15ea65a016', 'edits': [
            B(r'("userScale":\s*\{\s*"enabled":\s*)false(?=,)', 'false', 'true', ['userScale', 'enabled']),
            S(r'("name": "ultra2k", "label": ")(?P<old>[^"]*)(?=")', 'Ultra 2K (upscaled)', ['tiers|name=ultra2k', 'label'])]},
        'game_settings.json': {'baseMd5': '56d314772c', 'edits': [
            S(r'("id": "graphics.tier", "label": "Graphics", "kind": "choice", "source": "graphics.tiers", "hint": ")(?P<old>[^"]*)(?=")',
              'Ultra 2K is the default: the Ultra look drawn at 67% of your window and upscaled with FSR 2, SSGI off, made to hold 30 fps or more at 2560x1600. Ultra (scaled) is the older look with full SSGI. Lower tiers trade effects for frame rate on 8-16 GB graphics cards.', ['menu|id=graphics.tier', 'hint'])]}}},
    'f5menu': {'files': {'menu.json': {'baseMd5': '5da8a0510d', 'edits': list(_MENU)}}},
    'f5menuboot': {'files': {'menu.json': {'baseMd5': '5da8a0510d', 'edits': list(_MENU) + [I(r'("openWaitS": 0\.8,\n)', _BOOTTXT, [], 'boot', _BOOTJ)]}}},
    'f5card': {'files': {'loading_card.json': {'baseMd5': '81ca419b34', 'edits': [
        N(r'("maxSeconds":\s*)4\.0(?=,)', '4.0', '14.0', ['maxSeconds']),
        I(r'("maxSeconds":\s*14\.0,\n)', '  "waitForStartupWarm": true,\n', [], 'waitForStartupWarm', True)]}}},
    'f5start': {'files': {'zones.json': {'baseMd5': '185898dbcc', 'edits': [
        LT(r'("id": "arrive_start",\r?\n\s*"pos": )(?P<old>\[\])', '[0.0, 8.6]', ['zones|id=pit_yard', 'spots|id=arrive_start', 'pos'], [0.0, 8.6])]}}},
    'f5loot': {'files': {'loot_ui.json': {'baseMd5': 'cd33e731da', 'edits': [
        I(r'("openInFight":\s*false,\n)', '    "blockWhileMenu": true,\n', ['screen'], 'blockWhileMenu', True)]}}},
}


def md5(b): return hashlib.md5(b).hexdigest()[:10]
def rd(p): return open(p, 'rb').read()


def walk(o, path):
    for k in path:
        if isinstance(k, str) and '|' in k:
            lst, sel = k.split('|'); kk, vv = sel.split('='); o = [x for x in o[lst] if x.get(kk) == vv][0]
        else: o = o[k]
    return o


def build(kit, f):
    s = KITS[kit]['files'][f]; a, b = rd(os.path.join(TWINS[0], f)), rd(os.path.join(TWINS[1], f))
    if a != b: sys.exit('twins differ: ' + f)
    if '--rebase' not in sys.argv and md5(a) != s['baseMd5']: sys.exit('BASE CHANGED since staging: %s %s (staged on %s)' % (f, md5(a), s['baseMd5']))
    t = a.decode('utf-8'); crlf = '\r\n' in t; nt = t; pa = json.loads(t.lstrip('\ufeff'))
    for e in s['edits']:
        rx = e['rx']; ms = list(re.finditer(rx, nt, re.S if e['kind'] in ('lins',) else 0))
        if len(ms) != 1: sys.exit('EDIT FAILED: %d matches for %s in %s' % (len(ms), rx, f))
        m = ms[0]; fix = (lambda x: x.replace('\n', '\r\n')) if crlf else (lambda x: x)
        if e['kind'] in ('ins', 'lins'):
            nt = nt[:m.end()] + fix(e['text']) + nt[m.end():]
            if e['kind'] == 'ins': walk(pa, e['parent'])[e['key']] = e['val'] if not isinstance(e['val'], dict) else dict(e['val'])
            else: walk(pa, e['path']).insert(e['idx'], e['val'])
        elif e['kind'] == 'lit':
            nt = nt[:m.start('old')] + e['new'] + nt[m.end('old'):]; o = walk(pa, e['path'][:-1]); o[e['path'][-1]] = e['val']
        elif e['kind'] == 'str':
            nt = nt[:m.start('old')] + e['new'] + nt[m.end('old'):]; o = walk(pa, e['path'][:-1]); o[e['path'][-1]] = e['new']
        else:
            nt = nt[:m.start()] + m.group(1) + e['new'] + nt[m.end():]; o = walk(pa, e['path'][:-1]); old = o[e['path'][-1]]
            if e['kind'] == 'bool': assert old == (e['old'] == 'true'), (e['path'], old); o[e['path'][-1]] = e['new'] == 'true'
            else: assert abs(old - float(e['old'])) < 1e-9, (e['path'], old); o[e['path'][-1]] = float(e['new'])
    pb = json.loads(nt.lstrip('\ufeff')); assert pa == pb, 'after != base + the listed values in ' + f
    return a, nt.encode('utf-8')


def guard(d):
    bd = os.path.normcase(os.path.abspath(d)); names = set()
    for p in glob.glob(os.path.join(REPO, 'Builds', 'PLAY*.bat')): names |= set(re.findall(r'%~dp0(HDRP_[^\\"]+)\\', open(p, errors='ignore').read()))
    for n in names:
        if os.path.normcase(os.path.join(REPO, 'Builds', n)) in bd: sys.exit('REFUSED: %s is inside %s, named by a PLAY*.bat launcher (live/rollback). Use a QA copy.' % (d, n))
    if os.path.normcase(os.path.join(REPO, 'Builds', 'Play')) in bd: sys.exit('REFUSED: Builds\\Play')


def main():
    if len(sys.argv) < 3 or sys.argv[1] not in KITS: print(__doc__); return
    g, cmd = sys.argv[1], sys.argv[2]; files = list(KITS[g]['files']); GD = os.path.join(HERE, 'kits', g); go = '--go' in sys.argv
    if cmd == 'make':
        man = {}
        for f in files:
            base, after = build(g, f)
            for sub, data in (('base', base), ('after', after)):
                os.makedirs(os.path.join(GD, sub), exist_ok=True); open(os.path.join(GD, sub, f), 'wb').write(data)
            man[f] = {'base': md5(base), 'after': md5(after)}; print(g, f, 'md5', md5(base), '->', md5(after))
        json.dump(man, open(os.path.join(GD, 'MD5.json'), 'w'), indent=1); return
    if cmd == 'show':
        for f in files:
            b = rd(os.path.join(GD, 'base', f)).decode('utf-8').splitlines(); a = rd(os.path.join(GD, 'after', f)).decode('utf-8').splitlines()
            import difflib
            print('==', f); [print(x) for x in difflib.unified_diff(b, a, 'base', 'after', n=0, lineterm='') if not x.startswith(('---', '+++', '@@'))]
        return
    if cmd not in ('apply', 'revert'): print(__doc__); return
    man = json.load(open(os.path.join(GD, 'MD5.json'))); rev = cmd == 'revert'; tg = sys.argv[sys.argv.index('--target') + 1] if '--target' in sys.argv else None
    dirs = [tg] if tg else TWINS
    for d in dirs: guard(d)
    src, want = ('base', 'after') if rev else ('after', 'base')
    for f in files: assert md5(rd(os.path.join(GD, 'base', f))) == man[f]['base'] and md5(rd(os.path.join(GD, 'after', f))) == man[f]['after'], 'kit files changed after make: ' + f
    for d in dirs:
        for f in files:
            cur = md5(rd(os.path.join(d, f))); ok = cur == man[f][want]
            print('%-7s %s %-20s now %s (expect %s %s) -> %s %s' % ('OK' if ok else ('ALREADY' if cur == man[f][src] else 'BAD'), os.path.basename(d.rstrip('\\')), f, cur, want, man[f][want], src, man[f][src]))
            if not ok and cur != man[f][src]: sys.exit('REFUSED: %s in %s is neither the kit base nor its after' % (f, d))
    if not go: print('DRY RUN (%s %s): nothing written. Add --go.' % (g, cmd.upper())); return
    stamp = time.strftime('%Y%m%d_%H%M%S'); bk = os.path.join(GD, 'backup', stamp)
    for i, d in enumerate(dirs):
        for f in files:
            os.makedirs(os.path.join(bk, str(i)), exist_ok=True); shutil.copy2(os.path.join(d, f), os.path.join(bk, str(i), f)); shutil.copyfile(os.path.join(GD, src, f), os.path.join(d, f))
    open(os.path.join(GD, 'STATE.txt'), 'a', encoding='utf-8').write('%s %s %s dirs=%s backup=%s\n' % (stamp, g, cmd.upper(), dirs, bk))
    print('WROTE %s into %s; backup %s' % (src, dirs, bk))


main()

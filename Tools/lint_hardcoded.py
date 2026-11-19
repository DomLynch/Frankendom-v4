#!/usr/bin/env python3
"""Hardcoded-literal scan for the data-layer lint (Frankendom, 18 Nov).
Usage: python Tools/lint_hardcoded.py [--base b8cd693b] [--out <md>] [--json <json>]
Scans every .cs changed since --base under Assets/Scripts, plus the named reader areas.
HARD      = literal in code with no row behind it, looks like a tunable.
FALLBACK  = reader default (?? x, cfg>0 ? cfg : x): already row-backed.
ROWDEFAULT= field initialiser on a plain JSON config class: already data.
Read-only: writes nothing under Assets."""
import re, subprocess, sys, os, json, collections

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
args = sys.argv[1:]


def opt(n, d):
    return args[args.index(n) + 1] if n in args else d


BASE = opt('--base', 'b8cd693b')
OUT = opt('--out', os.path.join(ROOT, '_agents', 'laneLG', 'lint_datalayer', 'HARDCODED.md'))
JSN = opt('--json', os.path.join(ROOT, '_agents', 'laneLG', 'lint_datalayer', 'hardcoded.json'))
NAMED = ['MonsterLook.cs', 'Roster.cs', 'RosterResolver.cs', 'Zones/ZoneRuntime.cs', 'Combat/BossPhase.cs',
         'Combat/CueTable.cs', 'Combat/AudioManifest.cs', 'GameHUD.cs', 'HudCfg.cs', 'WeaponGrip.cs', 'GripPose.cs',
         'Combat/MoveDefsOverlay.cs', 'Zones/QaSwitches.cs', 'Gear/WeaponSwap.cs', 'Gear/GearTable.cs']


# big files touched only in a few lines: scan the changed lines only
PARTIAL = ['DuelFighter', 'CameraRig', 'HubNpcs', 'PerfFixes', 'BowVisual']


def git(*a):
    return subprocess.run(['git', '-C', ROOT] + list(a), capture_output=True, text=True,
                          encoding='utf-8', errors='replace').stdout


changed = [l.strip() for l in git('diff', '--name-only', BASE + '..HEAD', '--', 'Assets/Scripts').splitlines()
           if l.endswith('.cs')]
changed = [c for c in changed if os.path.exists(os.path.join(ROOT, c))]
chg_lines = {}
for f in changed:
    s = set()
    for l in git('diff', '-U0', BASE + '..HEAD', '--', f).splitlines():
        m = re.match(r'@@ -\S+ \+(\d+)(?:,(\d+))? @@', l)
        if m:
            a = int(m.group(1))
            n = int(m.group(2) or 1)
            s.update(range(a, a + n))
    chg_lines[f] = s
files = list(changed)
for n in NAMED:
    p = 'Assets/Scripts/' + n
    if p not in files and os.path.exists(os.path.join(ROOT, p)):
        files.append(p)

AREA = [('bow', 'Combat/Bow'), ('bow', 'DuelFighter'), ('axe grip', 'WeaponGrip'), ('axe grip', 'GripPose'),
        ('enemies (look/roster/zone)', 'MonsterLook'), ('enemies (look/roster/zone)', 'Roster'),
        ('enemies (look/roster/zone)', 'ZoneRuntime'), ('boss audio/phase', 'BossPhase'),
        ('boss audio/phase', 'CueTable'), ('boss audio/phase', 'AudioManifest'), ('HUD', 'GameHUD'),
        ('HUD', 'HudCfg'), ('Humbaba', 'HumbabaWatch'), ('Humbaba', 'ChaseAgent'),
        ('maul/weapon swap', 'WeaponSwap'), ('maul/weapon swap', 'GearTable'), ('preload/perf', 'GlbPreload'),
        ('preload/perf', 'HubNpcs'), ('preload/perf', 'PerfFixes'), ('QA switches', 'QaSwitches'),
        ('move defs', 'MoveDefsOverlay'), ('camera', 'CameraRig')]


def area(p):
    for a, k in AREA:
        if k in p:
            return a
    return 'other'


FW = [('Combat/Bow', 10), ('DuelFighter', 10), ('WeaponSwap', 9), ('GearTable', 9), ('BossPhase', 8),
      ('ChaseAgent', 8), ('HumbabaWatch', 7), ('CameraRig', 8), ('WeaponGrip', 6), ('GripPose', 6),
      ('MoveDefsOverlay', 7), ('CueTable', 6), ('AudioManifest', 4), ('GameHUD', 5), ('HudCfg', 4),
      ('ZoneRuntime', 6), ('MonsterLook', 4), ('Roster', 5), ('GlbPreload', 3), ('HubNpcs', 3),
      ('PerfFixes', 3), ('QaSwitches', 1)]


def fw(p):
    for k, w in FW:
        if k in p:
            return w
    return 3


CAT = [
    ('damage/balance', 10, r'(?i)\b(dmg|damage|hp|health|stamina|poise|stagger|knock|heal|cost|chance|prob|mult|weight)\w*'),
    ('timing', 9, r'(?i)\b(dur|time|cooldown|cd|delay|wait|interval|window|lock|buffer|grace|blend|fade|ramp|hold|ttl|life|expire|sec)\w*'),
    ('speed/accel', 9, r'(?i)\b(speed|vel|accel|decel|turn|rate|slew|lerp|smooth|damp|spring)\w*'),
    ('distance/offset', 7, r'(?i)\b(dist|range|radius|offset|height|reach|length|width|size|near|far|margin|dead|band|zone)\w*'),
    ('threshold', 7, r'(?i)\b(min|max|limit|clamp|cap|thresh|tol|epsilon|angle|deg|scale)\w*'),
    ('colour', 5, r'new Color|Color32|\.color|(?i:\bcolou?r\b)'),
    ('vector', 5, r'new Vector[234]\(|Quaternion\.Euler'),
]
NUM = re.compile(r'(?<![\w.])(-?\d+\.\d+f?|-?\d*\.\d+f?|-?\d+f|-?\d+)(?![\w.])')
SCI = re.compile(r'\d+(?:\.\d+)?[eE][-+]?\d+f?')
IGN = {'0', '1', '-1', '0.5', '2', '60', '1000', '0f', '1f', '-1f', '0.5f', '2f', '60f', '1000f', '0.0', '1.0',
       '0.0f', '1.0f', '-1.0f', '2.0', '2.0f', '.5f', '.5', '0.001', '0.001f', '360', '360f', '180', '180f',
       '255', '255f', '100', '100f', '-0.5f', '-0.5', '-2', '-2f', '0.01', '0.01f', '0.02f', '3', '3f', '8', '4', '4f'}
STR = re.compile(r'"((?:[^"\\]|\\.)*)"')
SKIPLINE = re.compile(r'Debug\.|Log\(|LogWarning|LogError|\[Header|\[Tooltip|\[Range|\[CreateAssetMenu|Console\.|'
                      r'\bthrow\b|^\s*using\s|\[MenuItem|Format\(|\[RuntimeInitializeOnLoadMethod|\[DefaultExecutionOrder|\[AddComponentMenu')


def strip_comments(src):
    out = []
    inblk = False
    for ln in src.split('\n'):
        i = 0
        res = ''
        instr = False
        while i < len(ln):
            c = ln[i]
            if inblk:
                if ln.startswith('*/', i):
                    inblk = False
                    i += 2
                    continue
                i += 1
                continue
            if instr:
                res += c
                if c == '\\':
                    res += ln[i + 1:i + 2]
                    i += 2
                    continue
                if c == '"':
                    instr = False
                i += 1
                continue
            if c == '"':
                instr = True
                res += c
                i += 1
                continue
            if ln.startswith('//', i):
                break
            if ln.startswith('/*', i):
                inblk = True
                i += 2
                continue
            res += c
            i += 1
        out.append(res)
    return out


CLSDEF = re.compile(r'^\s*(?:public|internal|private)?\s*(?:static\s+)?(?:sealed\s+)?(?:partial\s+)?class\s+(\w+)')
FIELDINIT = re.compile(r'^\s*public\s+(?:readonly\s+)?(?:float|int|bool|string|double|long|Color|Vector\d|float\[\]|int\[\]|string\[\]|List<\w+>)\s+\w+\s*=\s*[^;(]+;')
CFGNAME = re.compile(r'(Cfg|Row|Json|Data|Entry|File|Def|Rule|Item|Cue|Table|Root|Raw|Spec|Opts|Set)$')


def what(line, lit, isstr):
    s = line.strip()
    if isstr:
        if re.search(r'\.(json|glb|png|bc7|wav|ogg|mp3|asset|prefab)\b', lit, re.I):
            return 'file name'
        return 'string id/key'
    for c, w, rx in CAT:
        if re.search(rx, s):
            return c
    return 'magic number'


rows = []
for p in files:
    _src = open(os.path.join(ROOT, p), encoding='utf-8', errors='replace').read()
    reviewed = set(i for i, l in enumerate(_src.split(chr(10)), 1) if 'hc:ok' in l)
    lines = strip_comments(_src)
    cls_stack = []
    depth = 0
    changed_set = chg_lines.get(p)
    for no, ln in enumerate(lines, 1):
        m = CLSDEF.match(ln)
        if m:
            cls_stack.append((m.group(1), depth, 'MonoBehaviour' in ln))
        incfg = bool(cls_stack) and bool(CFGNAME.search(cls_stack[-1][0]))
        opens = ln.count('{')
        closes = ln.count('}')
        skip = SKIPLINE.search(ln) or not ln.strip()
        if changed_set is not None and any(k in p for k in PARTIAL) and no not in changed_set:
            skip = True
        hits = []
        if not skip:
            nostr = SCI.sub('0', STR.sub('""', ln))
            for mm in NUM.finditer(nostr):
                v = mm.group(1)
                if v in IGN:
                    continue
                if nostr[max(0, mm.start() - 1):mm.start()] == '[':
                    continue
                hits.append((v, nostr[max(0, mm.start() - 34):mm.end() + 8]))
            for sm in STR.finditer(ln):
                lit = sm.group(1)
                if len(lit) < 2 or lit.startswith('_'):
                    continue
                if re.fullmatch(r'[A-Za-z0-9_./\-]{2,}|#[0-9a-fA-F]{6,8}', lit):
                    hits.append(('"' + lit + '"', ln[max(0, sm.start() - 34):sm.end() + 8]))
        if hits:
            isfield = bool(FIELDINIT.match(ln))
            rowdef = (isfield and bool(cls_stack) and not cls_stack[-1][2]) or (isfield and incfg) or ((incfg or ('[Serializable]' in ln)) and bool(re.search(r'public\s+[\w\[\]<>]+\s+\w+\s*=|,\s*\w+\s*=\s*[-"\d#n{]', ln)) and not re.search(r'\)\s*\{', ln.split('class')[-1] if 'class' not in ln else ''))
            fallback = (not rowdef) and bool(re.search(r'\?\?|>\s*0\w*\s*\?|GetOr\w*\(|Default\w*\(', ln))
            seen = set()
            for h, ctx in hits:
                if (h, no) in seen:
                    continue
                seen.add((h, no))
                isstr = h.startswith('"')
                kind = 'ROWDEFAULT' if rowdef else ('REVIEWED' if no in reviewed else ('FALLBACK' if fallback else 'HARD'))
                w = 'colour' if re.fullmatch(r'"#[0-9a-fA-F]+"', h) else what(ctx, h.strip('"'), isstr)
                rows.append(dict(file=p.replace('Assets/Scripts/', ''), line=no, lit=h, kind=kind, what=w,
                                 code=ln.strip()[:110], today=(changed_set is not None and no in changed_set), area=area(p)))
        depth += opens - closes
        while cls_stack and depth <= cls_stack[-1][1] and closes:
            cls_stack.pop()


def score(r):
    cw = dict((c, w) for c, w, _ in CAT)
    cw.update({'file name': 2, 'string id/key': 3, 'magic number': 4})
    s = cw.get(r['what'], 4) * fw(r['file'])
    if r['today']:
        s += 6
    if r['kind'] == 'FALLBACK':
        s *= 0.5
    return s


for r in rows:
    r['score'] = round(score(r), 1)
hard = [r for r in rows if r['kind'] == 'HARD']
fb = [r for r in rows if r['kind'] == 'FALLBACK']
rd = [r for r in rows if r['kind'] == 'ROWDEFAULT']
rv = [r for r in rows if r['kind'] == 'REVIEWED']
hard.sort(key=lambda r: -r['score'])
os.makedirs(os.path.dirname(OUT), exist_ok=True)
json.dump(rows, open(JSN, 'w'), indent=1)
byfile = collections.Counter(r['file'] for r in hard)
byarea = collections.Counter(r['area'] for r in hard)
L = ['# HARDCODED.md: literals in C# that a designer would want to tune (Frankendom, 18 Nov)', '',
     'Generated by `Tools\\lint_hardcoded.py` (base %s..HEAD plus the named reader areas). Read-only scan; nothing under Assets touched.' % BASE, '',
     'HARD = literal in code, no row behind it. FALLBACK = reader default (`?? x` / `cfg>0 ? cfg : x`), already row-backed. ROWDEFAULT = public field initialiser on a plain JSON class, already data. REVIEWED = line carries an `hc:ok` marker (kept out of the ranking). Only HARD is ranked. The scan is heuristic: colour/key/string hits may include non-tunables (shader property names, GameObject names); review the top of the list first.', '',
     '**Totals: %d HARD, %d REVIEWED (hc:ok), %d FALLBACK, %d ROWDEFAULT, over %d files (%d changed today, %d named-area readers).**' % (
         len(hard), len(rv), len(fb), len(rd), len(files), len(changed), len(files) - len(changed)), '',
     '## Count per area (HARD)']
for a, c in byarea.most_common():
    L.append('- %s: %d' % (a, c))
cur = os.path.join(os.path.dirname(OUT), 'curated_top.md')
if os.path.exists(cur):
    L += [''] + open(cur, encoding='utf-8').read().rstrip().split(chr(10))
L += ['', '## Count per file', '| file | HARD | FALLBACK | ROWDEFAULT | changed today |', '|---|---|---|---|---|']
for f, c in sorted(byfile.items(), key=lambda x: -x[1]):
    L.append('| %s | %d | %d | %d | %s |' % (f, c, sum(1 for r in fb if r['file'] == f), sum(1 for r in rd if r['file'] == f),
                                           'yes' if ('Assets/Scripts/' + f) in changed else 'no'))
L += ['', '## Ranked list (designer value: player feel and balance first)',
      '| # | score | file:line | literal | tunes | code |', '|---|---|---|---|---|---|']
for i, r in enumerate(hard, 1):
    L.append('| %d | %s | %s:%d%s | `%s` | %s | `%s` |' % (
        i, r['score'], r['file'], r['line'], ' (today)' if r['today'] else '', r['lit'].replace('|', '/'), r['what'],
        r['code'].replace('|', '/').replace('`', "'")))
open(OUT, 'w', encoding='utf-8').write('\n'.join(L) + '\n')
print('REVIEWED', len(rv), 'HARD', len(hard), 'FALLBACK', len(fb), 'ROWDEFAULT', len(rd), 'files', len(files))
print(dict(byarea))
for r in hard[:10]:
    print(r['score'], r['file'], r['line'], r['lit'], r['what'])

#!/usr/bin/env python3
"""laneDATALAYER hard-code scanner (read-only). Runtime Assets/Scripts only (not Editor_URP~).
Per non-trivial numeric literal it decides:
  default = field / class default (mirrors a json key; the json value wins)
  rdef    = default argument of R()/Rule()/Get..("key", 1.0) (data lookup with fallback)
  code    = literal inside an executable statement = HARD-CODED
CODE literals are also tagged: colour constructors, clip-name call sites, path strings, character-name strings.
FIX2 v1: a bare run is a gate (report + PASS/FAIL line + exit 1 on FAIL); --report = report only.
Usage: hc_scan.py [--root DIR] [--json out.json] [--top N] [--write-baseline f] [--check f]
Exit 1 from --check when any non-harness file has MORE literals in a hot category than the baseline (new file = baseline 0)."""
import re, os, sys, json, collections, argparse

HERE = os.path.dirname(os.path.abspath(__file__))
# installed as Tools\hc_gate.py: scripts = <project>\Assets\Scripts, baseline = Tools\hardcode_baseline.json (next to this file); from the lane folder: live project
_DEF_ROOT = os.path.join(os.path.dirname(HERE), 'Assets', 'Scripts') if os.path.basename(HERE).lower() == 'tools' else r'C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts'
_DEF_BASE = os.path.join(HERE, 'hardcode_baseline.json')
ap = argparse.ArgumentParser()
ap.add_argument('--root', default=_DEF_ROOT)
ap.add_argument('--json'); ap.add_argument('--write-baseline'); ap.add_argument('--check', nargs='?', const=_DEF_BASE)
ap.add_argument('--top', type=int, default=0); ap.add_argument('--ratchet', nargs='?', const=_DEF_BASE)
ap.add_argument('--vs', help='diff mode: compare --root (a staged tree) with this scripts folder (live) instead of a baseline file; use it in a kit rehearsal')
ap.add_argument('--accept', nargs=3, metavar=('FILE', 'CATEGORY', 'REASON'), help='record a reviewed exception in the baseline (counts above the baseline for that file and category are then allowed, with the reason)')
ap.add_argument('--baseline-file', dest='baseline_file', default=_DEF_BASE)
ap.add_argument('--quiet', action='store_true', help='with --check: print only the failures and the verdict')
ap.add_argument('--report', action='store_true', help='FIX2 v1: report only (the old bare run): no verdict, exit 0')
a = ap.parse_args()
# FIX2 v1: a bare run (no mode flag) is now a real gate: the report, then 'hardcode gate: PASS' / 'FAIL n' and exit 1 on FAIL, against the default baseline. --report keeps the old report-only run.
if not (a.check or a.vs or a.write_baseline or a.ratchet or a.accept or a.json or a.report) and os.path.exists(_DEF_BASE): a.check = _DEF_BASE
if a.quiet:
    import io
    sys.stdout = io.StringIO()
ROOT = a.root

HARNESS = re.compile(r'(Proof|Auto|MoveSheet|LoopTest|VerifyGround|^ST[A-Z]|Shot|CaptureMeta|CaptureUtil|RotationCheck|Perf|Diag|ProfileMode|ResGate|Probe|Hunt|Selftest|Test)', re.I)
HARNESS_DIR = re.compile(r'(^|[\\/])(Diagnostics|Steer)([\\/]|$)')
STRICT_TRIV = {'0', '1', '2', '-1', '0.5', '10', '100', '255', '360', '180', '90', '1000', '3', '4', '-2', '2.0'}
numre = re.compile(r'(?<![\w.\"\]])-?(\d+\.\d+|\d+)(?:[fFdDlLuU]?)(?![\w\"])')
colre = re.compile(r'new\s+Color(32)?\s*\(\s*[-\d.]')
strre = re.compile(r'"((?:\\.|[^"\\])*)"')
declre = re.compile(r'^\s*(\[[^\]]*\]\s*)*((public|internal|private|protected|static|readonly|const|volatile|new)\s+)*[\w<>\[\],\.\?]+\s+\w+\s*(=[^;]*)?;\s*$')
rdef = re.compile(r'\b(R|Rule|F|Num|Get\w*|Float|Int|Param|Tun\w*|Or|Def)\(\s*"[^"]*"\s*,\s*[-\d.]')
playre = re.compile(r'\b(PlayClip|CrossFade|Play|SetTrigger|Force|SetBool|SetFloat|Load|LoadAll|Find|FindChild|LoadAssetAtPath|Combine)\s*\(\s*"')
pathre = re.compile(r'"[^"]*[/\\][^"]*\.(json|png|jpg|wav|ogg|mp3|mat|prefab|glb|asset)"|"(Assets|Data|StreamingAssets|Characters|Audio|Fx)/[^"]*"')
DEFAULT_FACTORY = re.compile(r'static\s+[\w<>\[\],\.]+\s+(Default\w*|D[A-Z]\w*)\s*\(\s*\)\s*(\{\s*(return|var)\b|=>)')
SCI = re.compile(r"(?<![\w.])\d+(\.\d+)?[eE][-+]?\d+[fFdD]?")
def is_tolerance(n):
    """RULE (documented in MANIFEST): |x| <= 0.001 is an epsilon / divide guard; -9xx is an unset sentinel (-998f, -999f); 60 is the sim rate (one shared constant, like Duel.cs /60f)."""
    try:
        v = float(n)
    except ValueError:
        return False
    return (v != 0 and abs(v) <= 0.001) or (-1000 < v <= -900) or v == 60
lit_names = re.compile(r'^(Pitborn|Sobek|Joan|Dracula|trident|maul|sword|claws?)$', re.I)


def system(rel):
    r = '/' + rel.replace('\\', '/')
    b = os.path.basename(r)
    if re.search(r'^(Cam|Camera)', b) or '/Zones/Cam' in r:
        return 'camera'
    if re.match(r'(AIBrain|Ai[A-Z]|FlowBot|ST)', b) or '/Steer/' in r:
        return 'ai'
    if re.search(r'Hud|Menu|Loot|Touch|Stance|UI|Draw', b) or '/Menu/' in r or '/Loot/' in r or '/Controls/' in r:
        return 'ui'
    if '/Audio/' in r or re.search(r'Audio|Sfx|Sound|Cue', b):
        return 'audio'
    if '/Finishers/' in r:
        return 'finishers'
    if '/Physics/' in r or re.search(r'Ragdoll|Phys|HitReact', b):
        return 'physics'
    if '/Combat/' in r:
        if re.search(r'Vfx|Fx|Blood|Wound|Decal|Spark|Trail|Clash|Gore', b):
            return 'fx'
        return 'combat'
    if re.search(r'Fx|Flicker|Gore|Trail|Blood|Atmos|Fog|Glow|Rift|Shard|Ember|Arc|Light|Look|Post|Exposure|Quality|Shadow', b):
        return 'fx_look'
    if '/Gear/' in r or re.search(r'Gear|Weapon|Grip|Arm|Foot|Head|Fighter|Roster|Hand|Hold|Tail', b):
        return 'gear_rig'
    if '/Zones/' in r:
        return 'zones'
    return 'other'


files = []
for d, _, fs in os.walk(ROOT):
    for f in fs:
        if f.endswith('.cs'):
            p = os.path.join(d, f)
            files.append((p, os.path.relpath(p, ROOT)))

res = {}
for p, rel in sorted(files):
    harness = bool(HARNESS.search(os.path.basename(rel))) or bool(HARNESS_DIR.search(rel))
    c = collections.Counter()
    samples = []
    inblock = False
    stack = []          # kinds: type, enum, method, init(ctx)
    stmt = ''           # text since the last ; { }
    def ctx():
        for k in reversed(stack):
            if k != 'init':
                return k
        return 'ns'
    for i, l in enumerate(open(p, encoding='utf-8-sig', errors='replace'), 1):
        s = l
        if inblock:
            if '*/' in s:
                s = s.split('*/', 1)[1]
                inblock = False
            else:
                continue
        s = re.sub(r'/\*.*?\*/', ' ', s)
        if '/*' in s:
            s = s.split('/*', 1)[0]
            inblock = True
        reviewed = re.search(r'//\s*hc:ok\b', s) is not None   # RULE: a line whose // comment starts with hc:ok <reason> was reviewed (algorithm constant / guard): its literals are not counted
        s = re.sub(r'("(?:\\.|[^"\\])*")|//.*$', lambda m: m.group(1) or '', s)  # RULE: a string inside a // comment is not a string literal (WeaponSwap.cs 'claws' false positive)
        strs = strre.findall(s)
        code = strre.sub('""', s)
        code = re.sub(r'//.*$', '', code)
        # walk braces to keep the context stack right; remember the context at line start
        start_ctx = ctx()
        inline_type = bool(re.search(r'(?<!\w)(class|struct)\s+\w+', code)) and '{' in code and '}' in code
        for ch_i, ch in enumerate(code):
            if ch == '{':
                pre = stmt
                if re.search(r'(?<!\w)namespace(?!\w)', pre): stack.append('ns')
                elif re.search(r'(?<!\w)(class|struct|interface)(?!\w)', pre): stack.append('type')
                elif re.search(r'(?<!\w)enum(?<!\w)', pre): stack.append('enum')
                elif re.search(r'(=|\]|,|(?<!\w)new\s+[\w<>\[\]\.]+\s*(\(\s*\))?|(?<!\w)return)\s*$', pre.rstrip() + ' ') and not re.search(r'\)\s*$', pre): stack.append('init')
                else: stack.append('method')
                stmt = ''
            elif ch == '}':
                if stack: stack.pop()
                stmt = ''
            elif ch == ';':
                stmt = ''
            else:
                stmt += ch
        if not code.strip() or code.strip().startswith('#'):
            continue
        cx = start_ctx
        if cx == 'enum':
            continue
        code2 = re.sub(r'(?<!\w)(Debug\.Log\w*|Console\.Write\w*|LogWarning|LogError|Log|Warn)\s*\(.*$', '', code)
        code2 = re.sub(r'\[[^\]]*\]', '[]', code2)
        code2 = SCI.sub(' ', code2)   # RULE: scientific-notation literals (1e-6f) are tolerances, never tunables
        nums = [m.group(0).rstrip('fFdDlLuU') for m in numre.finditer(code2)]
        nums = [n for n in nums if n not in STRICT_TRIV and not is_tolerance(n)]
        if reviewed: nums = []
        ncol = len(colre.findall(code2))
        isconst = re.match(r'\s*((public|internal|private)\s+)?(static\s+)?const(?<!\w)', code2) is not None
        if rdef.search(code2):
            k = 'rdef'
        elif isconst:
            k = 'const'
        elif cx == 'type' and '=>' not in code2 and not re.search(r'\)\s*\{', code2):
            k = 'default'
        elif cx == 'type' and DEFAULT_FACTORY.search(code2):
            k = 'default'   # RULE (b3 gate fix): a parameterless static factory named Default* / D<Upper>* in a data class builds the code-side DEFAULT of a json row (mirror of the json key); any other method stays code
        elif inline_type and re.search(r'(?<!\w)class \w+[^{]*\{', code2) and not re.search(r'\)\s*\{[^}]*;', code2):
            k = 'default'
        else:
            k = 'code'
        if nums:
            c[k] += len(nums)
            if k == 'code' and len(samples) < int(os.environ.get('DL_SAMPLES', '4')):
                samples.append((i, code2.strip()[:140]))
        if ncol:
            c[k + '_color'] += ncol
        if k == 'code':
            c['code_clipname'] += len(playre.findall(s))
            if pathre.search(s):
                c['code_path'] += 1
            c['code_charname'] += sum(1 for x in strs if lit_names.match(x))
            c['code_bone'] += sum(1 for x in strs if x.startswith('mixamorig'))
    if c:
        d = dict(c)
        d['_sys'] = system(rel)
        d['_harness'] = harness
        d['_samples'] = samples
        res[rel] = d

HOT = ('code', 'code_color', 'code_clipname', 'code_path', 'code_charname', 'code_bone')
bysys = collections.defaultdict(collections.Counter)
sysfiles = collections.Counter()
for rel, c in res.items():
    s = 'HARNESS' if c['_harness'] else c['_sys']
    sysfiles[s] += 1
    for k, v in c.items():
        if not k.startswith('_'):
            bysys[s][k] += v

if a.json:
    json.dump(res, open(a.json, 'w'), indent=1)
print('files scanned', len(files), '| files with literals', len(res), '| harness', sum(1 for c in res.values() if c['_harness']))
print('%-10s %8s %8s %8s %9s %8s %6s %8s %6s %6s' % ('system', 'default', 'rdef', 'CODE', 'codeColor', 'clipName', 'path', 'charName', 'bone', 'files'))
tot = collections.Counter()
for s in sorted(bysys, key=lambda s: -bysys[s]['code']):
    b = bysys[s]
    print('%-10s %8d %8d %8d %9d %8d %6d %8d %6d %6d' % (s, b['default'], b['rdef'], b['code'], b['code_color'], b['code_clipname'], b['code_path'], b['code_charname'], b['code_bone'], sysfiles[s]))
    if s != 'HARNESS':
        tot.update(b)
print('TOTAL non-harness: default %d  rdef %d  CODE %d  colour-in-code %d  clipname-calls %d  path %d  charName %d  boneNames %d' % tuple(tot[k] for k in ('default', 'rdef', 'code', 'code_color', 'code_clipname', 'code_path', 'code_charname', 'code_bone')))
if a.top:
    rows = sorted(((c.get('code', 0) + c.get('code_color', 0), rel, c) for rel, c in res.items() if not c['_harness']), reverse=True)[:a.top]
    for n, rel, c in rows:
        print('%4d code %3d col %2d  %-9s %s' % (n, c.get('code', 0), c.get('code_color', 0), c['_sys'], rel))


def key(c):
    return {k: v for k, v in c.items() if k in HOT}


if a.write_baseline:
    base = {rel: key(c) for rel, c in res.items() if not c['_harness'] and key(c)}
    json.dump({'version': 1, 'note': 'hard-coded literal counts per runtime file and hot category; gate fails only when a count goes UP or a new file appears', 'files': base}, open(a.write_baseline, 'w'), indent=1, sort_keys=True)
    print('baseline written', a.write_baseline, len(base), 'files')
if a.ratchet:
    old = json.load(open(a.ratchet)); fl = old['files']; ch = 0
    for rel, c in res.items():
        if c['_harness']: continue
        cur = key(c)
        for k, v in list(fl.get(rel, {}).items()):
            nv = cur.get(k, 0)
            if nv < v: fl[rel][k] = nv; ch += 1
    json.dump(old, open(a.ratchet, 'w'), indent=1, sort_keys=True); print('ratchet: lowered', ch, 'counts')
if a.quiet:
    sys.stdout = sys.__stdout__
if a.accept:
    rel_, cat_, why_ = a.accept
    bj = json.load(open(a.baseline_file)); cur = key(res[rel_]).get(cat_, 0) if rel_ in res else 0
    allowed = bj['files'].get(rel_, {}).get(cat_, 0)
    acc = bj.setdefault('accepted', {}).setdefault(rel_, {}).setdefault(cat_, {'n': 0})
    acc['n'] = max(acc['n'], cur - allowed); acc['reason'] = why_; acc['date'] = __import__('time').strftime('%Y-%m-%d')
    json.dump(bj, open(a.baseline_file, 'w'), indent=1, sort_keys=True)
    print('accepted %s %s: %d above the baseline, reason: %s' % (rel_, cat_, acc['n'], why_))
if a.check or a.vs:
    if a.vs:
        import subprocess, tempfile
        tj = os.path.join(tempfile.mkdtemp(), 'v.json')
        subprocess.run([sys.executable, os.path.abspath(__file__), '--root', a.vs, '--json', tj, '--quiet'], capture_output=True)
        vj = json.load(open(tj)); base = {rel: key(c) for rel, c in vj.items() if not c['_harness']}; accd = {}
        print('(diff mode: %s against %s)' % (ROOT, a.vs))
    else:
        bj = json.load(open(a.check)); base = bj['files']; accd = bj.get('accepted', {})
    bad = []
    for rel, c in res.items():
        if c['_harness']:
            continue
        for k, v in key(c).items():
            allowed = base.get(rel, {}).get(k, 0) + accd.get(rel, {}).get(k, {}).get('n', 0)
            if v > allowed:
                bad.append((rel, k, allowed, v))
    for r in bad:
        print('GATE-FAIL %s %s allowed %d now %d' % r)
    print('hardcode gate:', 'FAIL %d' % len(bad) if bad else 'PASS')
    sys.exit(1 if bad else 0)

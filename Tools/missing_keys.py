#!/usr/bin/env python3
r"""missing_keys.py - data_gates-style check (HCKIT7 v103; HCKIT8 v103b adds the Assets\Editor loaders, Tools\x.json hints and the ignored-by-design list): a [Serializable] row class field with a non-zero initializer must have its key in the json.

Why: JsonUtility.FromJson builds the row with `new T()`, so a key that is missing from the json silently keeps the C# field initializer
(example: FinisherCamera ColRow bodyClearM = 0.25f). The real value then lives in code, not in data. Dom's rule: everything lives in data.

  python Tools\missing_keys.py                 live project: Assets\Scripts (+ Assets\Editor with --editor) vs Assets\Data   (read-only)
  python Tools\missing_keys.py --quiet         only FAIL / WARN lines
  python Tools\missing_keys.py --all           also count zero-initialised / un-initialised fields (strict: every field needs its key)
  python Tools\missing_keys.py --overlay DIR   evaluate "live Data + DIR\Data" (what the project looks like after a lane's merge)
  python Tools\missing_keys.py --data D --scripts S   scratch copy
  python Tools\missing_keys.py --editor         HCKIT8: Assets\Editor row classes too (their json is in Data\ or, hint field "dir":"Tools", in Tools\)
  python Tools\missing_keys.py --tools DIR      HCKIT8: folder used for "dir":"Tools" hints (default <project>\Tools; --overlay DIR also overlays DIR\Tools\*.json)
  python Tools\missing_keys.py --allow FILE    lines "x.json:Class.field" that may stay code-only (reported as INFO)
  python Tools\missing_keys.py --json out.json  machine-readable result
Exit code 1 = at least one FAIL.

How a loader is found: every `JsonUtility.FromJson<T>(..)` / `FromJsonOverwrite(.., var)` / `Load<T>("x.json")` call in a .cs file is paired with the nearest "x.json"
string literal of that file; T is resolved to a class declared in the same file (else the only class of that name). The class tree is then walked against the json
(nested classes, arrays of rows: every element). A field the class declares but the json does not carry = FAIL when its initializer is non-zero.
Loaders it cannot pair or resolve are listed as INFO (never silently skipped)."""
import argparse, bisect, json, os, re, shutil, sys, tempfile
from pathlib import Path

PROJECT = Path(os.environ.get('HCKIT7_LIVE', r'C:\Users\dom12\Documents\unity_test_hdrp'))
PRIM = {'int', 'float', 'bool', 'string', 'double', 'long', 'short', 'byte', 'uint', 'ulong', 'ushort', 'sbyte', 'char'}
UNITY_LEAF = {'Vector2', 'Vector3', 'Vector4', 'Color', 'Color32', 'Quaternion', 'Rect', 'Bounds', 'AnimationCurve', 'LayerMask', 'Vector2Int', 'Vector3Int', 'Matrix4x4', 'Gradient'}
ZERO_RE = re.compile(r'^(?:[-+]?(?:float|double|Single|Double)\.NaN|[-+]?0+(?:\.0*)?(?:[fFdDuUlL]+)?|[-+]?\.0+[fF]?|false|null|""|default(?:\(.*\))?|string\.Empty|\(\w+\)\s*0|Vector[234]\.zero|Vector[23]Int\.zero|new\s+[\w.]+\s*\[\s*0\s*\]|new\s+[\w.]+\s*\[\s*\]\s*\{\s*\}|new\s+List<[^>]*>\s*\(\s*\)|new\s+Vector[234]\s*\(\s*\))$')


def load_hints():
    p = Path(__file__).with_name('missing_keys_hints.json')
    try: return json.loads(p.read_bytes().decode('utf-8-sig'))
    except Exception: return {}


HINTS = load_hints()   # data, not code: loaders the text scan cannot pair, ignored loaders, allowed code-only fields (missing_keys_hints.json)


# ------------------------------------------------------------------------------------------------ C# source reading
def blank_comments_strings(s):
    """same-length copy of s with comments and string/char literal contents replaced by spaces ('"' and "'" kept) so offsets and braces line up"""
    out = list(s); i, n = 0, len(s)
    while i < n:
        c = s[i]
        if c == '/' and i + 1 < n and s[i + 1] == '/':
            while i < n and s[i] != '\n': out[i] = ' '; i += 1
        elif c == '/' and i + 1 < n and s[i + 1] == '*':
            while i < n and not (s[i] == '*' and i + 1 < n and s[i + 1] == '/'):
                if s[i] != '\n': out[i] = ' '
                i += 1
            for j in (i, i + 1):
                if j < n: out[j] = ' '
            i += 2
        elif c == '@' and i + 1 < n and s[i + 1] == '"':
            i += 2
            while i < n:
                if s[i] == '"':
                    if i + 1 < n and s[i + 1] == '"': out[i] = ' '; out[i + 1] = ' '; i += 2; continue
                    break
                if s[i] != '\n': out[i] = ' '
                i += 1
            i += 1
        elif c == '"':
            i += 1
            while i < n and s[i] != '"' and s[i] != '\n':
                if s[i] == '\\' and i + 1 < n: out[i] = ' '; i += 1
                if s[i] != '\n': out[i] = ' '
                i += 1
            i += 1
        elif c == "'":
            j = i + 1
            while j < n and s[j] != "'" and s[j] != '\n' and j - i < 6:
                if s[j] == '\\': j += 1
                j += 1
            if j < n and s[j] == "'":
                for k in range(i + 1, j): out[k] = ' '
                i = j + 1
            else: i += 1
        else: i += 1
    return ''.join(out)


def match_brace(b, i):
    """b[i] == '{' -> index of the matching '}' (b is blanked)"""
    d = 0
    for j in range(i, len(b)):
        if b[j] == '{': d += 1
        elif b[j] == '}':
            d -= 1
            if d == 0: return j
    return len(b) - 1


def split_top(s, sep=','):
    out, d, cur = [], 0, []
    for ch in s:
        if ch in '({[': d += 1
        elif ch in ')}]': d -= 1
        if ch == sep and d <= 0: out.append(''.join(cur)); cur = []
        else: cur.append(ch)
    out.append(''.join(cur)); return out


CLASS_RE = re.compile(r'\b(?:(?:public|internal|private|protected|static|sealed|partial|abstract)\s+)*(class|struct)\s+(\w+)\s*(?:<[^>{]*>)?\s*(?::\s*[^{;]+?)?\s*\{')
ENUM_RE = re.compile(r'\benum\s+(\w+)\s*(?::\s*\w+\s*)?\{')


class Field:
    def __init__(s, typ, name, init, line): s.typ, s.name, s.init, s.line = typ, name, init, line

    @property
    def nonzero(s): return s.init is not None and not ZERO_RE.match(s.init.strip())


class Cls:
    def __init__(s, name, file, line): s.name, s.file, s.line, s.fields, s.base, s.start, s.end = name, file, line, [], None, 0, 0


def parse_members(raw, b, start, end, line_of):
    """fields declared directly in the class body raw[start:end] (b = blanked copy)"""
    out, i = [], start
    while i < end:
        while i < end and b[i].isspace(): i += 1
        if i >= end: break
        seg0 = i; d = 0; j = i; kind = None
        while j < end:
            c = b[j]
            if c == '{':
                pre = b[seg0:j]
                if '=' in pre.replace('==', '') and '(' not in pre.split('=')[0] or re.search(r'\bnew\s*(?:[\w.<>]+(?:\s*\[\s*\])*)?\s*$', pre):   # initializer braces belong to the field
                    j = match_brace(b, j) + 1; continue
                kind = 'block'; break                                   # method / property / nested type
            if c == '(': d += 1
            elif c == ')': d -= 1
            elif c == ';' and d <= 0: kind = 'field'; break
            j += 1
        if kind is None: break
        if kind == 'block':
            e = match_brace(b, j); i = e + 1
            k = i
            while k < end and b[k].isspace(): k += 1
            if k < end and b[k] == '=':                                   # auto-property initializer `{ get; set; } = x;`
                while k < end and b[k] != ';': k += 1
                i = k + 1
            continue
        text_b = b[seg0:j]; text_r = raw[seg0:j]
        i = j + 1
        if '(' in text_b.split('=')[0]: continue                       # method declaration without body / delegate / ctor call
        t = re.sub(r'^(\s*\[[^\]]*\])+', lambda m: ' ' * len(m.group(0)), text_b)   # drop the leading attributes only (a trailing [] belongs to the type)
        raw_attrs = ' '.join(re.findall(r'\[([^\]]*)\]', re.match(r'^(?:\s*\[[^\]]*\])*', text_r).group(0)))
        mods = set(); rest = t.strip(); off = len(t) - len(t.lstrip())
        while True:
            m = re.match(r'(public|private|protected|internal|static|const|readonly|volatile|new|unsafe|override|virtual|abstract|extern|partial)\b\s*', rest)
            if not m: break
            mods.add(m.group(1)); rest = rest[m.end():]; off += m.end()
        if 'static' in mods or 'const' in mods or 'readonly' in mods: continue
        ser = 'SerializeField' in raw_attrs
        if not ('public' in mods or ser): continue
        if 'NonSerialized' in raw_attrs: continue
        m = re.match(r'([\w.]+(?:\s*<[^;=]*?>)?(?:\s*\[\s*\])*)\s+(\w[^;]*)$', rest, re.S)
        if not m: continue
        typ = re.sub(r'\s+', '', m.group(1)); decl_off = seg0 + off + m.start(2)
        decl_raw = raw[decl_off:j]
        for d_ in split_top(decl_raw):
            mm = re.match(r'\s*(\w+)\s*(?:=\s*(.*?))?\s*$', d_, re.S)
            if not mm: continue
            out.append(Field(typ, mm.group(1), mm.group(2), line_of(seg0)))
    return out


def parse_cs(path, text):
    b = blank_comments_strings(text)
    line_starts = [0] + [m.end() for m in re.finditer(r'\n', text)]
    def line_of(pos): return bisect.bisect_right(line_starts, pos)
    classes, enums = [], []
    for m in ENUM_RE.finditer(b): enums.append(m.group(1))
    for m in CLASS_RE.finditer(b):
        ob = m.end() - 1; cb = match_brace(b, ob)
        c = Cls(m.group(2), path, line_of(m.start())); c.start, c.end = ob, cb
        head = b[m.start():ob]
        bm = re.search(r'\b' + re.escape(m.group(2)) + r'\s*(?:<[^>]*>)?\s*:\s*([\w.]+)', head)
        c.base = bm.group(1).split('.')[-1] if bm else None
        c.fields = parse_members(text, b, ob + 1, cb, line_of)
        classes.append(c)
    return classes, enums, b


# ------------------------------------------------------------------------------------------------ loader pairing
LOAD_RE = re.compile(r'JsonUtility\.FromJson\s*<\s*([\w.]+)\s*>|JsonUtility\.FromJsonOverwrite\s*\(\s*[^,]*,\s*([\w.]+)\s*\)|\bLoad\s*<\s*([\w.]+)\s*>\s*\(\s*"([^"]+\.json)"')
JSON_LIT_RE = re.compile(r'"([\w\-./\\ ]*?[\w\-]+\.json)"')


def find_loaders(path, raw, b, classes):
    lits = [(m.start(1), m.group(1).replace('\\', '/').split('/')[-1]) for m in JSON_LIT_RE.finditer(raw) if b[m.start(1) - 1:m.start(1)] == '"' and b[m.start(1):m.start(1) + 1] == ' ']
    out = []
    for m in LOAD_RE.finditer(b):
        # the blanked text has no string contents, so the Load<T>("x.json") alternative is matched on raw
        pos = m.start()
        T = m.group(1) or m.group(3); var = m.group(2); jf = None
        if var:
            vn = var.split('.')[-1]
            best = None
            for dm in re.finditer(r'\b([A-Za-z_]\w*)(?:\[\])?\s+' + re.escape(vn) + r'\b\s*(?:=|;|,|\))', b[:pos]):
                if dm.group(1) not in ('var', 'return', 'new', 'else', 'ref', 'out'): best = dm.group(1)
            for dm in re.finditer(r'\b' + re.escape(vn) + r'\s*=\s*new\s+(\w+)', b[:pos]): best = dm.group(1)
            T = best
        if T: T = T.split('.')[-1]
        conf = 'near'
        before = [l for l in lits if l[0] < pos and pos - l[0] < 2500]
        after = [l for l in lits if l[0] >= pos and l[0] - pos < 400]
        if before: jf = before[-1][1]
        elif after: jf = after[0][1]
        else:
            names = sorted({l[1] for l in lits})
            if len(names) == 1: jf = names[0]; conf = 'file'
            else:
                prev = [l for l in lits if l[0] < pos]
                if prev: jf = prev[-1][1]; conf = 'guess'
        out.append(dict(file=path, pos=pos, type=T, json=jf, conf=conf, var=var))
    # Load<T>("x.json") / Load<T>("x") calls (the literal is inside the string, so search the raw text); STSteer.Load<T>("block") = a block of steer.json
    for m in re.finditer(r'\b((?:\w+\.)?Load)\s*<\s*([\w.]+)\s*>\s*\(\s*"([\w\-./]+)"', raw):
        if b[m.start():m.start() + 4] != 'Load' and '.' not in m.group(1): continue
        owner = m.group(1).rsplit('.', 1)[0] if '.' in m.group(1) else ''
        lit = m.group(3).replace(chr(92), '/').split('/')[-1]
        T = m.group(2).split('.')[-1]
        if owner in HINTS.get('blockLoaders', {}):
            out.append(dict(file=path, pos=m.start(), type=T, json=HINTS['blockLoaders'][owner], block=lit, conf='block', var=None)); continue
        if lit.endswith('.json'): out.append(dict(file=path, pos=m.start(), type=T, json=lit, conf='literal', var=None))
        elif owner == '': out.append(dict(file=path, pos=m.start(), type=T, json=lit + '.json', conf='name', var=None))
    return out


# ------------------------------------------------------------------------------------------------ walk
class Registry:
    def __init__(s): s.by_name = {}; s.enums = set(); s.files = {}; s.loaders = []

    def add_file(s, path, text):
        classes, enums, b = parse_cs(path, text)
        for c in classes: s.by_name.setdefault(c.name, []).append(c)
        s.enums.update(enums); s.files[path] = (text, b, classes)
        s.loaders += find_loaders(path, text, b, classes)

    def resolve(s, name, file):
        cands = s.by_name.get(name, [])
        same = [c for c in cands if c.file == file]
        if same: return same[0]
        if len(cands) == 1: return cands[0]
        if not cands: return None
        d = os.path.dirname(file).replace(chr(92), '/').split('/')
        def common(c):
            e = os.path.dirname(c.file).replace(chr(92), '/').split('/'); n = 0
            while n < min(len(d), len(e)) and d[n] == e[n]: n += 1
            return n
        cands = sorted(cands, key=common, reverse=True)
        return cands[0] if common(cands[0]) > common(cands[1]) else None   # nearest directory wins, a tie stays unresolved


def elem_type(t):
    m = re.match(r'^(.*)\[\]$', t)
    if m: return m.group(1)
    m = re.match(r'^(?:System\.Collections\.Generic\.)?List<(.+)>$', t)
    return m.group(1) if m else None


def all_fields(reg, cls):
    fs = list(cls.fields)
    if cls.base:
        bc = reg.resolve(cls.base, cls.file)
        if bc is not None and bc is not cls: fs = all_fields(reg, bc) + fs
    return fs


def has_nonzero(reg, cls, depth=0):
    """does `new cls()` carry any value in code (a non-zero initializer, also inside nested rows)?"""
    if depth > 8: return False
    for f in all_fields(reg, cls):
        if f.init is None or not f.nonzero: continue
        et = elem_type(f.typ); bt = (et or f.typ).split('.')[-1]
        sub = reg.resolve(bt, cls.file) if bt not in PRIM and bt not in UNITY_LEAF and bt not in reg.enums else None
        if sub is not None and not et and re.match(r'^new\s+' + re.escape(bt) + r'\s*\(\s*\)$', f.init.strip()):
            if has_nonzero(reg, sub, depth + 1): return True
            continue
        return True
    return False


def walk(reg, cls, val, path, findings, strict, depth=0):
    """cls: class, val: parsed json. findings: dict(path, cls, field, kind)"""
    if depth > 12 or not isinstance(val, dict): return
    for f in all_fields(reg, cls):
        t = f.typ
        has = f.name in val
        et = elem_type(t)
        base_t = (et or t).split('.')[-1]
        sub = reg.resolve(base_t, cls.file) if base_t not in PRIM and base_t not in UNITY_LEAF and base_t not in reg.enums else None
        if sub is not None and (sub.fields or sub.base):
            if has:
                v = val[f.name]
                if et and isinstance(v, list):
                    for i, e in enumerate(v): walk(reg, sub, e, path + [f.name, i], findings, strict, depth + 1)
                elif not et: walk(reg, sub, v, path + [f.name], findings, strict, depth + 1)
                continue
            if f.init is not None and f.nonzero and not et and has_nonzero(reg, sub):
                findings.append(dict(path=path, cls=cls, field=f, kind='object', sub=sub))
            elif strict:
                findings.append(dict(path=path, cls=cls, field=f, kind='object0', sub=sub))
            continue
        if has: continue
        if f.nonzero: findings.append(dict(path=path, cls=cls, field=f, kind='leaf'))
        elif strict: findings.append(dict(path=path, cls=cls, field=f, kind='leaf0'))


# ------------------------------------------------------------------------------------------------ project scan
def jload(p):
    try: return json.loads(Path(p).read_bytes().decode('utf-8-sig'))
    except Exception: return None


def build_registry(scripts_dirs):
    reg = Registry()
    for sd in scripts_dirs:
        for p in sorted(Path(sd).rglob('*.cs')):
            try: reg.add_file(str(p), p.read_bytes().decode('utf-8-sig', 'replace'))
            except Exception as e: print('WARN cannot read', p, e)
    return reg


def rel(p):
    p = str(p)
    return os.path.relpath(p, str(PROJECT)) if p.startswith(str(PROJECT)) else p


def scan(reg, data_dir, strict=False, tools_dir=None, ignored=None):
    """-> (pairs, findings, unresolved). pairs: [(loader, cls, jsonfile)]. ignored (list): gets (file, type, why) of every loader skipped by design"""
    pairs, unresolved, findings, seen = [], [], [], set()
    tools_dir = tools_dir or (PROJECT / 'Tools')   # HCKIT8
    for L in reg.loaders:
        r = rel(L['file']); base = os.path.basename(str(L['file']))
        hint = next((h for h in HINTS.get('loaders', []) if h['file'] == base and (h.get('type') in (None, L['type']))), None)
        if hint and hint.get('json'): L['json'] = hint['json']; L['type'] = hint.get('type', L['type']); L['block'] = hint.get('block'); L['dir'] = hint.get('dir')   # HCKIT8: dir
        ig = next((h for h in HINTS.get('ignore', []) if h['file'] == base and h['type'] == L['type']), None)   # HCKIT8: listed, not silent
        if ig:
            if ignored is not None and (base, L['type']) not in {(x[0], x[1]) for x in ignored}: ignored.append((base, L['type'], ig.get('why', '')))
            continue
        if L['type'] and re.fullmatch(r'T\w{0,2}', L['type']) and L['type'] not in reg.by_name: continue   # generic helper: its callers are checked instead
        if not L['json']: unresolved.append((r, 'no json literal near the loader', L)); continue
        if not L['type']: unresolved.append((r, 'type of %s not resolved (generic / dynamic)' % L['json'], L)); continue
        cls = reg.resolve(L['type'], L['file'])
        if cls is None or L['type'] in PRIM: unresolved.append((r, 'class %s not found (%s)' % (L['type'], L['json']), L)); continue
        key = (cls.file, cls.name, cls.line, L.get('dir'), L['json'], L.get('block'))
        if key in seen: continue
        seen.add(key)
        jp = (Path(tools_dir) if L.get('dir') == 'Tools' else Path(data_dir)) / L['json']   # HCKIT8
        if not jp.exists(): unresolved.append((r, '%s not in %s (optional file)' % (L['json'], L.get('dir') or 'Data'), L)); continue
        val = jload(jp)
        if val is None: unresolved.append((r, '%s unreadable' % L['json'], L)); continue
        blk = L.get('block'); pre = []
        if blk:
            if not isinstance(val, dict) or blk not in val: unresolved.append((r, '%s has no block "%s" (optional)' % (L['json'], blk), L)); continue
            val = val[blk]; pre = [blk]
        pairs.append((L, cls, ((L['dir'] + '/') if L.get('dir') else '') + L['json']))
        fl = []
        if isinstance(val, dict): walk(reg, cls, val, pre, fl, strict)
        for f in fl: f['json'] = ((L['dir'] + '/') if L.get('dir') else '') + L['json']; f['loader'] = L; findings.append(f)
    return pairs, findings, unresolved


def fmt_path(p): return ''.join(('[%d]' % x) if isinstance(x, int) else ('.' + x if i else x) for i, x in enumerate(p))


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('--data'); ap.add_argument('--scripts', action='append'); ap.add_argument('--overlay'); ap.add_argument('--editor', action='store_true', help='also scan Assets\\Editor')
    ap.add_argument('--tools', help='folder for hinted json that lives in Tools (default <project>\\Tools)')
    ap.add_argument('--all', action='store_true'); ap.add_argument('--quiet', action='store_true'); ap.add_argument('--json')
    ap.add_argument('--allow', help='file with "x.json:Class.field" lines that are allowed to stay code-only (reported INFO)')
    args = ap.parse_args()
    data = Path(args.data) if args.data else PROJECT / 'Assets' / 'Data'
    scripts = args.scripts or [str(PROJECT / 'Assets' / 'Scripts')] + ([str(PROJECT / 'Assets' / 'Editor')] if args.editor else [])
    tmp = None; tools = Path(args.tools) if args.tools else PROJECT / 'Tools'
    if args.overlay:
        tmp = Path(tempfile.mkdtemp(prefix='missing_keys_')); d = tmp / 'Data'; d.mkdir()
        td = tmp / 'Tools'; td.mkdir()   # HCKIT8
        for f in tools.glob('*.json'): shutil.copyfile(f, td / f.name)
        ovt = Path(args.overlay) / 'Tools'
        if ovt.exists():
            for f in ovt.glob('*.json'): shutil.copyfile(f, td / f.name)
        tools = td
        for f in data.glob('*.json'): shutil.copyfile(f, d / f.name)
        ov = Path(args.overlay) / 'Data'
        if ov.exists():
            for f in ov.glob('*.json'): shutil.copyfile(f, d / f.name)
        data = d
    reg = build_registry(scripts)
    ignored = []
    pairs, findings, unresolved = scan(reg, data, args.all, tools, ignored)
    allow = {a['key'] for a in HINTS.get('allow', [])}
    if args.allow and Path(args.allow).exists(): allow = {l.strip() for l in Path(args.allow).read_text(encoding='utf-8').splitlines() if l.strip() and not l.startswith('#')}
    rows = []
    for f in findings:
        item = '%s:%s%s.%s' % (f['json'], fmt_path(f['path']) + '/' if f['path'] else '', f['cls'].name, f['field'].name)
        item_key = '%s:%s.%s' % (f['json'], f['cls'].name, f['field'].name)
        lvl = 'FAIL' if f['kind'] in ('leaf', 'object') else 'WARN'
        if args.all: lvl = 'FAIL'
        if item_key in allow: lvl = 'INFO'
        rows.append((lvl, item, 'no key in json; code default %s  (%s:%d)' % (f['field'].init if f['field'].init is not None else '(none)', rel(f['cls'].file), f['field'].line)))
    agg = {}
    for lvl, item, msg in rows:
        k = (lvl, re.sub(r'\[\d+\]', '[]', item), msg); agg[k] = agg.get(k, 0) + 1
    print('missing_keys: scripts=%s  data=%s%s  loaders=%d  paired=%d  ignored=%d  unresolved=%d' % (','.join(rel(s) for s in scripts), data if not tmp else 'Data', '  OVERLAY=' + args.overlay if args.overlay else '', len(reg.loaders), len(pairs), len(ignored), len(unresolved)))
    print('\n== CHECK: every row-class field has its key in the json (%s)' % ('all fields' if args.all else 'non-zero initializers'))
    for (lvl, item, msg), n in sorted(agg.items(), key=lambda x: (x[0][0], x[0][1])):
        if args.quiet and lvl == 'INFO': continue
        print('  [%-5s] %s%s  -  %s' % (lvl, item, ' (x%d rows)' % n if n > 1 else '', msg))
    if not args.quiet:
        for r, why, L in unresolved: print('  [INFO ] unchecked loader %s: %s' % (r, why))
        for b_, t_, why in sorted(ignored): print('  [INFO ] ignored by design %s %s: %s' % (b_, t_, why))
    nf = sum(1 for r in rows if r[0] == 'FAIL'); nw = sum(1 for r in rows if r[0] == 'WARN')
    print('  => %d FAIL (%d distinct), %d WARN' % (nf, sum(1 for k in agg if k[0] == 'FAIL'), nw))
    print('\nRESULT: %s  (%d loader/json pairs checked, %d ignored by design, %d unpaired, %d FAIL, %d WARN)' % ('FAIL' if nf else 'PASS', len(pairs), len(ignored), len(unresolved), nf, nw))
    if args.json: Path(args.json).write_text(json.dumps([dict(level=l, item=i, msg=m) for l, i, m in rows], indent=1), encoding='utf-8')
    if tmp: shutil.rmtree(tmp, ignore_errors=True)
    return 1 if nf else 0


if __name__ == '__main__':
    sys.exit(main())

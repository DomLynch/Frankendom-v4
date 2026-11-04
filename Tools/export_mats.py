import re, json, os, sys
# Export URP material properties (parsed from the .mat YAML, since the URP shaders are gone in the HDRP copy) so HdrpConvert.cs can rebuild them as HDRP materials.
# ALWAYS export from the frozen URP project: the HDRP copy is already converted, and re-exporting it drifts (fog/particle cards come out as Lit)
root = r'C:\Users\dom12\Documents\unity_test\Assets'
OUT = r'C:\Users\dom12\Documents\unity_test_hdrp\Assets\HDRP\urp_materials.json'
LIT = '933532a4fcc9baf4fa0491de14d08ed7'; PART = '0406db5a14f94604a8c57ccfbc9f3b46'
out = []
for dp, _, fs in os.walk(root):
    for f in fs:
        if not f.endswith('.mat'): continue
        p = os.path.join(dp, f); t = open(p, encoding='utf-8', errors='ignore').read()
        m = re.search(r'm_Shader: \{fileID: \d+, guid: ([0-9a-f]+)', t); g = m.group(1) if m else ''
        kind = 'lit' if g == LIT else 'particle' if g == PART else 'other'
        tex = {}; fl = {}; col = {}
        sec = None
        for line in t.splitlines():
            s = line.strip()
            if s.startswith('m_TexEnvs:'): sec = 'tex'; continue
            if s.startswith('m_Floats:'): sec = 'fl'; continue
            if s.startswith('m_Colors:'): sec = 'col'; continue
            if s.startswith('m_BuildTextureStacks') or s.startswith('m_AllowLocking'): sec = None
            if sec == 'tex':
                mm = re.match(r'- (\w+):', s)
                if mm: cur = mm.group(1)
                mg = re.search(r'm_Texture: \{fileID: (-?\d+), guid: ([0-9a-f]+)', s)
                if mg and mg.group(2) != '0' * 32: tex[cur] = mg.group(2) + ':' + mg.group(1)
            elif sec == 'fl':
                mm = re.match(r'- (\w+): ([-0-9.eE+]+)', s)
                if mm: fl[mm.group(1)] = float(mm.group(2))
            elif sec == 'col':
                mm = re.match(r'- (\w+): \{r: ([-0-9.eE+]+), g: ([-0-9.eE+]+), b: ([-0-9.eE+]+), a: ([-0-9.eE+]+)\}', s)
                if mm: col[mm.group(1)] = [float(mm.group(i)) for i in range(2, 6)]
        out.append({'path': os.path.relpath(p, os.path.dirname(root)).replace('\\', '/'), 'kind': kind, 'shader': g, 'tex': tex, 'f': fl, 'c': col})
json.dump(out, open(OUT, 'w'), indent=1)
print(len(out), 'materials;', sum(1 for o in out if o['kind'] == 'lit'), 'lit,', sum(1 for o in out if o['kind'] == 'particle'), 'particle,', sum(1 for o in out if o['kind'] == 'other'), 'other')
for o in out:
    if o['kind'] == 'other': print('other:', o['path'], o['shader'])


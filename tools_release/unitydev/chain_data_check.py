"""unity-build chain_data_check.py: is the data for the 16:00 chain complete? (V9 scatter, V10 horizon, V13 contact, look.json, V4 patches, md5 Data == StreamingAssets).
Reads only. usage: python chain_data_check.py"""
import hashlib, os, re, glob, json
R = (os.environ.get('FK_ROOT') or os.path.join(os.path.expanduser('~'), 'Documents', 'unity_test_hdrp')) + r'/'
def md5(p): return hashlib.md5(open(p, 'rb').read()).hexdigest()
bad = 0
# V13: md5_after.txt lists the final state of the files it touched
n = d = 0
for l in open(R + '_agents/laneV13/md5_after.txt'):
    m = re.match(r'(\w{32})\s+\*?(.+)', l.strip())
    if not m: continue
    h, p = m.groups(); fp = R + p.replace('\\', '/')
    if not os.path.exists(fp): print('V13 missing', p); bad += 1; continue
    n += 1
    if md5(fp) != h: d += 1; print('V13 differs from kit md5_after:', p)
print('V13 files checked', n, 'differ', d)
# Data == StreamingAssets for every shared json
mis = [os.path.basename(f) for f in glob.glob(R + 'Assets/Data/*.json') if os.path.exists(R + 'Assets/StreamingAssets/' + os.path.basename(f)) and md5(f) != md5(R + 'Assets/StreamingAssets/' + os.path.basename(f))]
print('Data != StreamingAssets:', mis or 'none')
# V10 / V9 rows in the layouts
tot = {}
for f in sorted(glob.glob(R + 'Assets/Data/props_layout*.json')):
    t = open(f, encoding='utf-8-sig').read(); rows = len(re.findall(r'"v10sky|v10sky', t)); dis = len(re.findall(r'"disabled"\s*:\s*true', t))
    j = json.loads(t); cnt = 0; v9 = 0; v10 = 0; v10off = 0
    def walk(o):
        global cnt, v9, v10, v10off
        if isinstance(o, dict):
            s = json.dumps(o)
            if 'v10sky' in str(o.get('tag', '')) or 'v10sky' in str(o.get('id', '')) or 'v10sky' in str(o.get('name', '')):
                v10 += 1; v10off += 1 if o.get('disabled') else 0
            if 'v9' in str(o.get('tag', '')).lower() or str(o.get('id', '')).lower().startswith('v9') or 'v9_' in str(o.get('name', '')).lower(): v9 += 1
            for v in o.values(): walk(v)
        elif isinstance(o, list):
            for v in o: walk(v)
    walk(j)
    print('%-28s v10 rows %4d (disabled %d)  v9 rows %4d' % (os.path.basename(f), v10, v10off, v9))
for f in ('look.json', 'ground_patches.json', 'wet_decals.json', 'prop_grime_decals.json', 'look_zones.json', 'accent_lights.json', 'character_sep.json', 'zone_sky.json', 'contact_shadows.json'):
    p = R + 'Assets/Data/' + f
    if os.path.exists(p):
        try: e = json.load(open(p, encoding='utf-8-sig')).get('enabled', '-')
        except Exception as ex: e = 'ERR %s' % ex
        print('%-26s enabled=%s' % (f, e))
    else: print('%-26s (absent)' % f)

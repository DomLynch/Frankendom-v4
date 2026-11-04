import json, struct, io, sys, os, glob, shutil
from PIL import Image

# shrink the embedded textures of Hades prop GLBs by LOD (LOD0 2048, LOD1 1024, LOD2 512); originals are backed up under Tools/props_orig
SIZES = {'LOD0': 2048, 'LOD1': 1024, 'LOD2': 512}
BK = os.path.join(os.path.dirname(__file__), 'props_orig')

def load(p):
    d = open(p, 'rb').read()
    n = struct.unpack('<I', d[12:16])[0]
    j = json.loads(d[20:20 + n])
    off = 20 + n
    bl = struct.unpack('<I', d[off:off + 4])[0]
    return j, d[off + 8:off + 8 + bl]

def save(j, b, p):
    js = json.dumps(j, separators=(',', ':')).encode()
    js += b' ' * ((4 - len(js) % 4) % 4)
    b = bytes(b) + b'\0' * ((4 - len(b) % 4) % 4)
    total = 12 + 8 + len(js) + 8 + len(b)
    open(p, 'wb').write(struct.pack('<III', 0x46546C67, 2, total) + struct.pack('<II', len(js), 0x4E4F534A) + js + struct.pack('<II', len(b), 0x004E4942) + b)

def shrink(p):
    lod = 'LOD0' if 'LOD0' in p else 'LOD1' if 'LOD1' in p else 'LOD2'
    mx = SIZES[lod]
    j, b = load(p)
    imgs = j.get('images', [])
    if not imgs: return 0
    views = j['bufferViews']
    keep = []   # (bufferView index) rebuilt data
    newdata = {}
    for im in imgs:
        bv = im['bufferView']; v = views[bv]
        raw = b[v.get('byteOffset', 0):v.get('byteOffset', 0) + v['byteLength']]
        img = Image.open(io.BytesIO(raw)); img.load()
        if max(img.size) > mx:
            s = mx / max(img.size); img = img.resize((max(1, int(img.width * s)), max(1, int(img.height * s))), Image.LANCZOS)
        out = io.BytesIO(); img.save(out, 'PNG', optimize=False); newdata[bv] = out.getvalue(); im['mimeType'] = 'image/png'
    # rebuild the buffer: all views in order, replacing image views
    nb = bytearray();
    for i, v in enumerate(views):
        data = newdata[i] if i in newdata else b[v.get('byteOffset', 0):v.get('byteOffset', 0) + v['byteLength']]
        while len(nb) % 4: nb.append(0)
        v['byteOffset'] = len(nb); v['byteLength'] = len(data); nb += data
    j['buffers'][0]['byteLength'] = len(nb)
    save(j, nb, p)
    return len(imgs)

if __name__ == '__main__':
    root = sys.argv[1]
    for p in sorted(glob.glob(os.path.join(root, '**', '*.glb'), recursive=True)):
        rel = os.path.relpath(p, root); bk = os.path.join(BK, rel)
        if not os.path.exists(bk):
            os.makedirs(os.path.dirname(bk), exist_ok=True); shutil.copy2(p, bk)
        shutil.copy2(bk, p)   # always shrink from the original
        n = shrink(p)
        print(rel, n, round(os.path.getsize(p) / 1e6, 1), 'MB')

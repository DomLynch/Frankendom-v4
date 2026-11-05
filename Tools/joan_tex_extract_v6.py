"""Extract Joan's textures from Assets/Characters/Joan/Joan_combat.glb for the JoanSwap materials (v82: joan_v6_mixamo_dark = Mixamo body + Tripo 400k head).
body (Ch13_body): base + normal -> joan_body_base.jpg / joan_body_normal.jpg (q92, max 4096)
head (tripo_material_*): base + normal -> joan_head_base.jpg / joan_head_normal.jpg (hair is part of the head texture: opaque, no alpha)"""
import json, struct, io, os
from PIL import Image
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
G = os.path.join(ROOT, 'Assets', 'Characters', 'Joan', 'Joan_combat.glb'); OUT = os.path.join(ROOT, 'Assets', 'Characters', 'Joan', 'Tex')
os.makedirs(OUT, exist_ok=True)
d = open(G, 'rb').read(); n = struct.unpack('<I', d[12:16])[0]; j = json.loads(d[20:20 + n]); off = 20 + n; bl = struct.unpack('<I', d[off:off + 4])[0]; b = d[off + 8:off + 8 + bl]
def img(i):
    im = j['images'][i]; bv = j['bufferViews'][im['bufferView']]; o = bv.get('byteOffset', 0)
    return Image.open(io.BytesIO(b[o:o + bv['byteLength']]))
def tex_img(t): return img(j['textures'][t['index']]['source'])
def save_jpg(im, name, mx=4096):
    im = im.convert('RGB')
    if max(im.size) > mx: im = im.resize((mx, mx), Image.LANCZOS)
    p = os.path.join(OUT, name); im.save(p, quality=92); print('wrote', p, im.size)
for prefix, tex in (('Ch13_body', 'joan_body'), ('tripo_material', 'joan_head')):
    m = next((m for m in j['materials'] if m['name'].startswith(prefix)), None)
    if m is None: print('NO MATERIAL', prefix); continue
    save_jpg(tex_img(m['pbrMetallicRoughness']['baseColorTexture']), tex + '_base.jpg')
    if 'normalTexture' in m: save_jpg(tex_img(m['normalTexture']), tex + '_normal.jpg')

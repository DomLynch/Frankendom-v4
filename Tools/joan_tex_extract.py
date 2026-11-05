"""Extract Joan's PNG textures from Assets/Characters/Joan/Joan_combat.glb for JoanSwap materials (char_tex_install.py only handles JPEG GLB images).
body: base (joan_chef_diffuse_4k) + normal (joan_chef_normal_4k) -> joan_body_base.jpg / joan_body_normal.jpg (q92, max 4096)
hair: base with alpha (joan_chef_hair_2k) -> joan_hair_base.png (alpha kept for alpha clip), normal (Ch13_1002_Normal) -> joan_hair_normal.jpg"""
import json, struct, io, os
from PIL import Image
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
G = os.path.join(ROOT, 'Assets', 'Characters', 'Joan', 'Joan_combat.glb'); OUT = os.path.join(ROOT, 'Assets', 'Characters', 'Joan', 'Tex')
os.makedirs(OUT, exist_ok=True)
d = open(G, 'rb').read(); n = struct.unpack('<I', d[12:16])[0]; j = json.loads(d[20:20 + n]); off = 20 + n; bl = struct.unpack('<I', d[off:off + 4])[0]; b = d[off + 8:off + 8 + bl]
def img(i):
    im = j['images'][i]; bv = j['bufferViews'][im['bufferView']]; o = bv.get('byteOffset', 0)
    return Image.open(io.BytesIO(b[o:o + bv['byteLength']]))
mats = {m['name']: m for m in j['materials']}
def tex_img(t): return img(j['textures'][t['index']]['source'])
body = mats['Ch13_body']; hair = mats['Ch13_hair']
def save_jpg(im, name, mx=4096):
    im = im.convert('RGB')
    if max(im.size) > mx: im = im.resize((mx, mx), Image.LANCZOS)
    p = os.path.join(OUT, name); im.save(p, quality=92); print('wrote', p, im.size)
save_jpg(tex_img(body['pbrMetallicRoughness']['baseColorTexture']), 'joan_body_base.jpg')
save_jpg(tex_img(body['normalTexture']), 'joan_body_normal.jpg')
hb = tex_img(hair['pbrMetallicRoughness']['baseColorTexture']).convert('RGBA'); p = os.path.join(OUT, 'joan_hair_base.png'); hb.save(p); print('wrote', p, hb.size, 'alpha min/max', hb.getchannel('A').getextrema())
save_jpg(tex_img(hair['normalTexture']), 'joan_hair_normal.jpg')

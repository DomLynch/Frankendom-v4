import json, struct, sys, os
def dump(glb, outdir, tag):
    d = open(glb, 'rb').read()
    n = struct.unpack('<I', d[12:16])[0]
    j = json.loads(d[20:20+n])
    off = 20 + n
    bl = struct.unpack('<I', d[off:off+4])[0]
    binb = d[off+8:off+8+bl]
    os.makedirs(outdir, exist_ok=True)
    def img(texinfo, name):
        if texinfo is None: return None
        im = j['images'][j['textures'][texinfo['index']]['source']]
        bv = j['bufferViews'][im['bufferView']]
        data = binb[bv.get('byteOffset', 0):bv.get('byteOffset', 0)+bv['byteLength']]
        ext = 'jpg' if im.get('mimeType') == 'image/jpeg' else 'png'
        fn = f'{tag}_{name}.{ext}'
        open(os.path.join(outdir, fn), 'wb').write(data)
        return fn
    out = {}
    for ni, node in enumerate(j['nodes']):
        if 'mesh' not in node: continue
        mesh = j['meshes'][node['mesh']]
        for pr in mesh['primitives']:
            mat = j['materials'][pr['material']]
            pbr = mat.get('pbrMetallicRoughness', {})
            key = node['name']
            out[key] = {
                'mat': mat.get('name'),
                'base': img(pbr.get('baseColorTexture'), key + '_base'),
                'normal': img(mat.get('normalTexture'), key + '_normal'),
                'mr': img(pbr.get('metallicRoughnessTexture'), key + '_mr'),
                'emissive': img(mat.get('emissiveTexture'), key + '_em'),
                'color': pbr.get('baseColorFactor'),
                'metal': pbr.get('metallicFactor'), 'rough': pbr.get('roughnessFactor'),
            }
    json.dump(out, open(os.path.join(outdir, tag + '_map.json'), 'w'), indent=1)
    print(tag, json.dumps(out, indent=1))
K = r'C:\Users\dom12\Documents\kits_out\3d'
U = r'C:\Users\dom12\Documents\unity_test\Assets\Characters'
dump(K + r'\crimson-regent\v4\assembled_v4f.glb', U + r'\Dracula\Tex', 'dracula')
dump(K + r'\pitborn\staged\Pitborn_A_rigged.glb', U + r'\Pitborn\Tex', 'pitborn')

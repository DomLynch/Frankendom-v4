"""review glb for the stills: Joan body (mesh + skin, animations dropped) + selected clips of the built set files. CPU, numpy.
  python make_render_glb.py <out.glb> <body.glb> <setfile.glb:clip> [...]"""
import sys, os, types
import numpy as np
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE); sys.dont_write_bytecode = True
import clip_lib as L, clip_build as CB
out, body = sys.argv[1], sys.argv[2]
j0, b0 = L.load(body); j, b = CB.compact_without_animations(j0, b0)
n2i = {n['name']: i for i, n in enumerate(j['nodes']) if 'name' in n}
clips = []
for spec in sys.argv[3:]:
    f, name = spec.rsplit(':', 1); js, bs = L.load(f); rg = L.Rig(js, bs); ch = rg.anims[name]
    c = types.SimpleNamespace(rot={}, hips_t=None, times=None)
    for (n, p), (ts, vs) in ch.items():
        nm = rg.nodes[n]['name']
        if p == 'rotation': c.rot[nm] = vs
        elif nm == 'mixamorig:Hips': c.hips_t = vs
        if c.times is None: c.times = ts
    clips.append((name, c))
CB.write_clips(j, b, n2i, clips); L.save(j, b, out); print('wrote', out, os.path.getsize(out))

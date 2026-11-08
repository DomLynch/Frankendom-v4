"""BOWART bow_mesh_gates.py: run the existing CLIPBUILD mesh/knee/neck gates (Tools/clip_gates.py, CPU numpy) on the new bow clips.
  python bow_mesh_gates.py <proj> <preview.glb> <report.json out> [samples]
Uses clip_gates.Metrics / Mesh / knee_for / clip_metrics with the numbers of clip_build.json "gates" (kneeMedialMaxM, headExtensionMaxDeg, neckExtensionMaxDeg, armClearanceMeshMinM, footFloorMaxM).
Toe-yaw and pose-vs-raw gates do not apply (the side-on stance turns the feet on purpose; there is no raw Mixamo source)."""
import sys, os, json
proj, glb, out = sys.argv[1:4]; n = int(sys.argv[4]) if len(sys.argv) > 4 else 16
sys.path.insert(0, os.path.join(proj, 'Tools'))
import clip_lib as L, clip_gates as G
g = json.load(open(os.path.join(proj, 'Assets', 'Data', 'clip_build.json'), encoding='utf-8-sig'))['gates']
bc = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'Assets', 'Data', 'bow_clips.json'), encoding='utf-8-sig'))['gates']
if 'armGapMinM' in bc: g['armClearanceMeshMinM'] = bc['armGapMinM']   # bow clips: the draw arm rests against the torso for the first frames of the nock (gap >= this, i.e. no sleeve vertex through the torso mesh)
j, b = L.load(glb); rig = L.Rig(j, b); M = G.Metrics(rig); mesh = G.Mesh(rig, j, b)
names = [a['name'] for a in j['animations'] if a['name'].startswith('bow_')]
rep = {}; bad = []
for st in names:
    m = G.clip_metrics(M, mesh, st, g, n); m['knee'] = G.knee_for(j, b, st)
    rep[st] = {'kneeMedialM': round(m['knee'], 4), 'headExtDeg': round(m['head'], 1), 'neckExtDeg': round(m['neck'], 1), 'armGapL': round(m['armL'], 4), 'armGapR': round(m['armR'], 4),
               'floorMin': round(m['floorMin'], 4), 'floorMax': round(m['floorMax'], 4)}
    if m['knee'] > g['kneeMedialMaxM']: bad.append('%s knee inward %.3f > %.3f' % (st, m['knee'], g['kneeMedialMaxM']))
    if m['head'] > g['headExtensionMaxDeg']: bad.append('%s head extension %.1f > %.0f' % (st, m['head'], g['headExtensionMaxDeg']))
    if m['neck'] > g['neckExtensionMaxDeg']: bad.append('%s neck extension %.1f > %.0f' % (st, m['neck'], g['neckExtensionMaxDeg']))
    if min(m['armL'], m['armR']) < g['armClearanceMeshMinM']: bad.append('%s sleeve-torso gap %.4f/%.4f < %.4f' % (st, m['armL'], m['armR'], g['armClearanceMeshMinM']))
    if m['floorMin'] < -g['footFloorMaxM']: bad.append('%s toe %.3f below floor' % (st, m['floorMin']))
    print(st, rep[st])
json.dump({'clips': rep, 'failed': bad, 'limits': {k: g[k] for k in ('kneeMedialMaxM', 'headExtensionMaxDeg', 'neckExtensionMaxDeg', 'armClearanceMeshMinM', 'footFloorMaxM')}}, open(out, 'w'), indent=1)
print('MESH GATES', 'PASS' if not bad else 'FAIL: ' + '; '.join(bad))

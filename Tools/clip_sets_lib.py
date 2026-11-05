"""clip_sets_lib.py (HCKIT5 v95, HC4 K5): shared clip sets for the bake scripts. Python twin of Assets/Editor/ClipSets.cs.
attack_clips.json / dodge_clips.json carry "clipSets": [{id, referenceState, posture?, outGlb: "{folder}/{id}_attacks_v2.glb", outClipDir, clips[]}], a roster row names one with "clipSet".
expand_bake(cfg, kind) = the explicit "bake" rows + one row built from the roster for every roster character that has a clipSet and no explicit row."""
import json, os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # unity_test_hdrp (this file lives in Tools/)


def _jload(p):
    return json.load(open(p, encoding='utf-8-sig'))


def roster(root=None):
    return _jload(os.path.join(root or ROOT, 'Assets', 'Data', 'roster.json'))


def build_entry(row, cset):
    folder = os.path.dirname(row['modelGlb']).replace(chr(92), '/') if row.get('modelGlb') else 'Assets/Characters/' + row['id']
    sub = lambda s: s.replace('{id}', row['id']).replace('{folder}', folder)
    e = {'character': row['id'], 'controller': row['controller'], 'baseGlb': row['modelGlb'], 'outGlb': sub(cset['outGlb']),
         'outClipDir': sub(cset['outClipDir']), 'referenceState': cset.get('referenceState', 'guard_idle'), 'clips': cset['clips']}
    if cset.get('posture'): e['posture'] = True
    return e


def expand_bake(cfg, kind='attack', root=None, ignore_explicit=False):
    out = [] if ignore_explicit else list(cfg.get('bake', []))
    have = {e['character'] for e in out}
    sets = {s['id']: s for s in cfg.get('clipSets', [])}
    for row in roster(root).get('characters', []):
        cs = row.get('clipSet', '')
        if not cs or row['id'] in have: continue
        if cs not in sets:
            print('clip_sets_lib(%s): roster %s names clipSet %s but the json has no such set' % (kind, row['id'], cs)); continue
        out.append(build_entry(row, sets[cs]))
    return out

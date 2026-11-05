"""paths.py (HCKIT5 v95, HC4 K9): python twin of Assets/Editor/ProjectPaths.cs. Tools/paths.json holds the machine paths; ${name} tokens in json/tools resolve through it.
   from paths import resolve, get ;  resolve('${kitsOut}/3d/x.glb')   (no paths.json / key missing -> the defaults below = the literals the project used before)"""
import json, os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULTS = {'projectRoot': '', 'kitsOut': 'C:/Users/dom12/Documents/kits_out', 'unityExe': 'C:/Program Files/Unity/Hub/Editor/6000.6.7f1/Editor/Unity.exe',
            'blenderExe': 'C:/Program Files/Blender Foundation/Blender 5.2/blender.exe', 'handoverDir': 'C:/HANDOVER-Macbook'}
_cfg = None


def _load():
    global _cfg
    if _cfg is None:
        _cfg = dict(DEFAULTS)
        p = os.path.join(ROOT, 'Tools', 'paths.json')
        if os.path.exists(p):
            try: _cfg.update({k: v for k, v in json.load(open(p, encoding='utf-8-sig')).items() if isinstance(v, str) and v and not k.startswith('_')})
            except Exception as e: print('paths.py: Tools/paths.json unreadable (%s), defaults' % e)
    return _cfg


def get(key):
    v = _load().get(key, '')
    return v if v else (ROOT.replace(chr(92), '/') if key == 'projectRoot' else '')


def resolve(s):
    if not isinstance(s, str) or '${' not in s: return s
    for k in DEFAULTS: s = s.replace('${%s}' % k, get(k))
    return s

"""Bake the dodge clips of Assets/Data/dodge_clips.json onto the character rig (animation-only GLB). Reuses bake_attack_clips.bake (same retarget).
   python Tools/bake_dodge_clips.py [Dracula]"""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bake_attack_clips as B
from clip_sets_lib import expand_bake   # HCKIT5 v95
cfg = json.load(open(os.path.join(B.ROOT, 'Assets', 'Data', 'dodge_clips.json'), encoding='utf-8-sig'))
only = sys.argv[1] if len(sys.argv) > 1 else None
rep = []
for e in expand_bake(cfg, 'dodge'):   # HCKIT5 v95 explicit bake rows + roster clipSet rows
    if only and e['character'] != only: continue
    B.bake(e, rep)
json.dump(rep, open(os.path.join(B.ROOT, 'Tools', 'dodge_bake_report.json'), 'w'), indent=1)

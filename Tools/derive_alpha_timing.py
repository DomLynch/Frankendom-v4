#!/usr/bin/env python
"""Derive the ALPHA playback timing from the real data and write it into
  Assets/Data/alpha_playback.json  ("timing" block)  and  Assets/Data/duel_rules.json (camera.playback / camera slow-mo / rules.fallbackBeatSeconds).
Sources: move-defs.json (windup/active/recovery TICKS at 60 Hz), attack_clips.json (contactActiveFrac), dodge.json, duel_rules.json camera.playback (lead/trade/reaction ticks).
Usage: python Tools/derive_alpha_timing.py [--dry]   (re-run after changing any of those files; mirrors both files to Assets/StreamingAssets)"""
import json, os, sys, shutil
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__))); A = os.path.join(ROOT, 'Assets'); D = os.path.join(A, 'Data')
def jl(p):
    with open(p, encoding='utf-8-sig') as f: return json.load(f)
def jw(p, o):
    with open(p, 'w', encoding='utf-8') as f: json.dump(o, f, indent=1, ensure_ascii=False)
HZ = 60.0
moves = {m['id']: m for m in jl(os.path.join(D, 'move-defs.json'))['moves']}
frac = jl(os.path.join(D, 'attack_clips.json'))['defaults']['contactActiveFrac']
dj = jl(os.path.join(D, 'dodge.json'))
duel = jl(os.path.join(D, 'duel_rules.json')); pb = duel['camera']['playback']
play = jl(os.path.join(D, 'alpha_playback.json'))
# ADMove -> MoveTable id (the one ADPlayback presses; Light = light_left/right: same numbers)
MAP = [('Light', 'light_left'), ('Slash', 'light_left'), ('Stab', 'thrust'), ('Heavy', 'heavy_overhead'), ('Kick', 'kick'), ('Special', 'skill_cleave')]
SAFETY = 1.25
lead, gap, settle = pb['leadTicks'], pb['tradeGapTicks'], pb['settleSeconds']
# longest reaction after a contact (ticks): stagger/guard break/parry/clash/kick/knockdown (Down = posture.stun 90, get_up at 54)
react = max(pb['hitStaggerHeavy'] + pb['critStaggerExtra'], pb['guardBreakTicks'], pb['parryStaggerTicks'], pb['clashStaggerTicks'], pb['kickStaggerTicks'], 90)
rm = moves['riposte']   # a parry beat chains a riposte
rip = pb['riposteDelayTicks'] + 1 + rm['windup'] + max(1, round(frac * rm['active'])) + rm['active'] + rm['recovery']
mt = []
for name, mid in MAP:
    m = moves[mid]; w, a, r = m['windup'], m['active'], m['recovery']; off = max(1, round(frac * a))
    contact_tick = 1 + w + off
    total = (w + a + r) / HZ
    worst = lead + gap + contact_tick + (a + r - off) + max(react, rip)      # press .. contact .. end of the move .. longest reaction (or the riposte chain)
    timeout = round((worst / HZ + settle) * SAFETY, 2)
    mt.append({'move': name, 'id': mid, 'windup': w, 'active': a, 'recovery': r, 'contactTick': contact_tick, 'contactS': round(contact_tick / HZ, 3),
               'totalS': round(total, 3), 'beatTimeoutS': timeout, 'slowFromS': round(max(0, contact_tick / HZ - 0.1), 3), 'slowToS': round(contact_tick / HZ + 0.1, 3)})
dt = []
for k in ('roll', 'sidestep', 'backstep'):
    r = dj[k]; dt.append({'kind': k.capitalize(), 'ticks': r['ticks'], 'iframeStart': r['iframeStart'], 'iframeEnd': r['iframeEnd'], 'seconds': round(r['ticks'] / HZ, 3)})
# typical beat: lead + trade gap + whole move + a light stagger + settle + walking back (~0.5 s)
typ = [(lead + gap + 1 + x['windup'] + x['active'] + x['recovery'] + pb['hitStaggerLight']) / HZ + settle + 0.5 for x in mt]
typ_beat = round(sum(typ) / len(typ), 2); max_beat = round(max(x['beatTimeoutS'] for x in mt), 2)
watch_extra = 2 * pb['repositionSeconds'] + 3.0
timing = {'tickHz': int(HZ), 'slowWindowS': 0.1, 'moves': mt, 'defence': dt,
          'round': {'beats': 3, 'typicalBeatS': typ_beat, 'maxBeatS': max_beat, 'roundTypicalS': round(3 * typ_beat, 2), 'roundMaxS': round(3 * (min(max_beat, 7.0) + watch_extra), 1), 'replayStartDelayS': 0.4}}
play['timing'] = timing
# duel_rules.json: derived values only (everything else stays as the lanes set it)
pb['maxBeatSeconds'] = min(7.0, max(max_beat, 3.0)); pb['critSlowLeadTicks'] = 6          # +-0.1 s window around the contact
pb['useFinishers'] = False; pb.setdefault('idleWaitSeconds', 3.0); pb.setdefault('finisherMaxSeconds', 20.0)
duel['camera']['critSlow']['duration'] = 0.75; duel['camera']['parrySlow']['duration'] = 0.43   # unscaled s: 0.2 s of sim time at scale 0.2 / 0.35
duel['rules']['fallbackBeatSeconds'] = typ_beat
print(json.dumps(timing['round']))
for x in mt: print(x['move'], x['id'], 'contact %.3fs total %.3fs timeout %.2fs' % (x['contactS'], x['totalS'], x['beatTimeoutS']))
if '--dry' in sys.argv: sys.exit(0)
jw(os.path.join(D, 'alpha_playback.json'), play); jw(os.path.join(D, 'duel_rules.json'), duel)
sa = os.path.join(A, 'StreamingAssets')
if os.path.isdir(sa):
    for n in ('alpha_playback.json', 'duel_rules.json'): shutil.copyfile(os.path.join(D, n), os.path.join(sa, n))
print('written + mirrored to StreamingAssets')

#!/usr/bin/env python
"""Validate Assets/Data/alpha_playback.json against the real Animator controllers.
Usage: python Tools/validate_alpha_playback.py [--table Assets/Data/alpha_playback.json]
Fails (exit 1) on any state name that does not exist in the character's controller, on press ids that are not in move-defs.json,
and on attack_clips.json / dodge.json / duel_rules.json camera.playback clip names missing from a controller. Warns on pending rows."""
import json, re, sys, os
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
A = os.path.join(ROOT, 'Assets')
def jl(p):
    with open(p, encoding='utf-8-sig') as f: return json.load(f)
def states(ctrl):
    txt = open(os.path.join(A, 'Characters', ctrl + '.controller'), encoding='utf-8').read()
    out = set()
    for blk in re.split(r'(?m)^--- ', txt):
        if blk.startswith('!u!1102'):
            m = re.search(r'(?m)^  m_Name: (.+)$', blk)
            if m: out.add(m.group(1).strip())
    return out
def main():
    tp = os.path.join(A, 'Data', 'alpha_playback.json')
    if '--table' in sys.argv: tp = sys.argv[sys.argv.index('--table') + 1]
    t = jl(tp); fails = []; warns = []
    S = {c: states(c) for c in ('Dracula', 'Pitborn')}
    for c, s in S.items(): print('%s: %d states' % (c, len(s)))
    mv = {m['id'] for m in jl(os.path.join(A, 'Data', 'move-defs.json'))['moves']}
    valid_press = mv | {'special1', 'special2', 'kick', 'thrust', 'light', 'heavy', 'roll', 'sidestep'}
    for r in t['rows']:
        ch = r['character']
        if ch not in S: fails.append('unknown character ' + ch); continue
        tag = '%s %s %s' % (ch, r['group'], r['key'])
        names = [r.get('state', ''), r.get('fallback', '')] + list(r.get('states', [])) + list(r.get('fallbackStates', []))
        for n in names:
            if not n: continue
            if n not in S[ch]:
                (warns if r.get('pending') or r.get('deferred') else fails).append('%s: state "%s" not in %s.controller' % (tag, n, ch))
        if r['group'] == 'move':
            if not r.get('press'): fails.append(tag + ': no press id')
            for p in r.get('press', []):
                if p not in valid_press: fails.append('%s: press id "%s" unknown' % (tag, p))
        if r['group'] in ('defence', 'hit', 'reaction') and not r.get('state'): fails.append(tag + ': empty state')
        if r.get('pending') and not r.get('state'): warns.append(tag + ': pending (no clip yet)')
    # roll: at least one of state/fallback must exist for each character
    for r in t['rows']:
        if r['key'] == 'Roll' and r['state'] not in S[r['character']] and r['fallback'] not in S[r['character']]:
            fails.append(r['character'] + ' Roll: neither state nor fallback exists')
    # the sim's own tables
    ac = jl(os.path.join(A, 'Data', 'attack_clips.json'))
    for row in ac['rows']:
        chs = [row['character']] if row['character'] else list(S)
        for ch in chs:
            if row['clip'] not in S[ch] and row.get('fallbackClip', '') not in S[ch]:
                fails.append('attack_clips %s %s: neither %s nor %s in controller' % (ch, row['move'], row['clip'], row.get('fallbackClip')))
    dj = jl(os.path.join(A, 'Data', 'dodge.json'))
    for ch in S:
        for k, keys in (('roll', ['clip']), ('sidestep', ['clipLeft', 'clipRight', 'clipBack']), ('backstep', ['clip'])):
            for kk in keys:
                n = dj[k].get(kk, '')
                if not n: continue
                if n not in S[ch]:
                    if k == 'roll': warns.append('dodge.json %s.%s "%s" missing in %s (DuelFighter falls back to roll_forward)' % (k, kk, n, ch))
                    else: fails.append('dodge.json %s.%s "%s" missing in %s' % (k, kk, n, ch))
    pb = jl(os.path.join(A, 'Data', 'duel_rules.json'))['camera']['playback']
    for k in ('hitLightClip', 'hitBackClip', 'hitHeavyClip', 'staggerClip'):
        for ch in S:
            if pb.get(k) and pb[k] not in S[ch]: fails.append('duel_rules camera.playback.%s "%s" missing in %s' % (k, pb[k], ch))
    for ch in S:
        for n in ('idle', 'walk', 'death', 'knockdown', 'get_up', 'guard_idle', 'guard_hit', 'parry'):
            if n not in S[ch]: fails.append('base state %s missing in %s (DuelFighter plays it by name)' % (n, ch))
    for w in warns: print('WARN ' + w)
    for f in fails: print('FAIL ' + f)
    print('%d rows, %d warnings, %d failures' % (len(t['rows']), len(warns), len(fails)))
    sys.exit(1 if fails else 0)
main()

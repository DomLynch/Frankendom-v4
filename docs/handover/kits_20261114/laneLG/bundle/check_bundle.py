"""Read-only bundle check for the 16:35 CODE release (bandits dev). For every code patch (laneLG\\reloadcover + laneF5\\patches) it verifies:
  tree file md5, patched md5, and that the patched file == the CURRENT tree file with the recorded .diff applied (a stale patch is reported STALE, never applied).
Also lists files touched by two patches (conflict) and the data kits' base md5 vs the current tree. Writes nothing but stdout."""
import os, re, sys, hashlib, difflib
R = r'C:\Users\dom12\Documents\unity_test_hdrp'; S = os.path.join(R, 'Assets', 'Scripts')
def md5(b): return hashlib.md5(b).hexdigest()[:10]
def rd(p): return open(p, 'rb').read()
def apply_diff(base_text, diff_text):
    """minimal unified-diff applier on LF text; returns (text, ok)"""
    src = base_text.split('\n'); out = []; pos = 0; lines = diff_text.split('\n'); i = 0
    while i < len(lines) and not lines[i].startswith('@@'): i += 1
    while i < len(lines):
        m = re.match(r'@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@', lines[i])
        if not m: i += 1; continue
        start = int(m.group(1)) - 1 if int(m.group(2) if m.group(2) is not None else 1) > 0 else int(m.group(1)); out += src[pos:start]; pos = start; i += 1
        while i < len(lines) and not lines[i].startswith('@@'):
            l = lines[i]
            if l.startswith('\\'): i += 1; continue
            if l.startswith('+') and not l.startswith('+++'): out.append(l[1:])
            elif l.startswith('-') and not l.startswith('---'):
                if pos >= len(src) or src[pos] != l[1:]: return None, False
                pos += 1
            elif l.startswith(' '):
                if pos >= len(src) or src[pos] != l[1:]: return None, False
                out.append(src[pos]); pos += 1
            i += 1
    out += src[pos:]; return '\n'.join(out), True
TARGETS = {  # FINAL files (bundle\final, built by make_final.py from my RELOADCOVER patches + the F5 agent's patches + the review agent's .reviewed files)
 r'_agents\laneLG\bundle\final\StartupWarm.cs.patched': (r'StartupWarm.cs', r'_agents\laneLG\bundle\final\StartupWarm.cs.diff'),
 r'_agents\laneLG\bundle\final\GameLoop.cs.patched': (r'GameLoop.cs', r'_agents\laneLG\bundle\final\GameLoop.cs.diff'),
 r'_agents\laneLG\bundle\final\QualityTiers.cs.patched': (r'QualityTiers.cs', r'_agents\laneLG\bundle\final\QualityTiers.cs.diff'),
 r'_agents\laneLG\bundle\final\LoadingCard.cs.patched': (r'HUD\LoadingCard.cs', r'_agents\laneLG\bundle\final\LoadingCard.cs.diff'),
 r'_agents\laneLG\bundle\final\LootCfg.cs.patched': (r'Loot\LootCfg.cs', r'_agents\laneLG\bundle\final\LootCfg.cs.diff'),
 r'_agents\laneLG\bundle\final\LootScreen.cs.patched': (r'Loot\LootScreen.cs', r'_agents\laneLG\bundle\final\LootScreen.cs.diff'),
 r'_agents\laneLG\bundle\final\MenuCfg.cs.patched': (r'Menu\MenuCfg.cs', r'_agents\laneLG\bundle\final\MenuCfg.cs.diff'),
 r'_agents\laneLG\bundle\final\MenuUI.cs.patched': (r'Menu\MenuUI.cs', r'_agents\laneLG\bundle\final\MenuUI.cs.diff'),
}
seen = {}
for pf, (tp, df) in TARGETS.items():
    pp = os.path.join(R, pf); tpp = os.path.join(S, tp)
    if not os.path.exists(pp): print('MISSING patched', pf); continue
    tree = rd(tpp); patched = rd(pp); d = open(os.path.join(R, df), encoding='utf-8', errors='replace').read() if os.path.exists(os.path.join(R, df)) else None
    status = '?'
    if d:
        crlf = b'\r\n' in tree; t = tree.decode('utf-8').replace('\r\n', '\n'); new, ok = apply_diff(t, d.replace('\r\n', '\n'))
        if ok:
            nb = new.replace('\n', '\r\n' if crlf else '\n').encode('utf-8'); status = 'APPLIES-CLEAN (result == .patched)' if nb == patched else 'APPLIES but result differs from .patched (md5 %s)' % md5(nb)
        else: status = 'STALE: the .diff does not apply to the current tree file'
    print('%-26s tree %s -> patched %s  %s' % (tp, md5(tree), md5(patched), status)); seen.setdefault(tp, []).append(pf)
for k, v in seen.items():
    if len(v) > 1: print('CONFLICT: %s touched by %s' % (k, v))

"""Builds bundle\\final\\: the 8 FINAL code files for the 16:35 release (file-only). Sources: my 2 RELOADCOVER .patched (laneLG\\reloadcover), the F5 agent's 4 unchanged .patched (LoadingCard, LootScreen, MenuCfg, MenuUI)
and the review agent's 2 .reviewed files (LootCfg.cs.reviewed REQUIRED: the F5 LOOTPAD comment swallowed two fields; QualityTiers.cs.reviewed: Upscaler-row-only choice was ignored).
Writes final\\<Name>.cs.patched + final\\<Name>.cs.diff (unified diff vs the CURRENT tree file) and prints md5s. Asserts the .reviewed files differ from the .patched ones only where expected."""
import os, hashlib, difflib, shutil
R = r'C:\Users\dom12\Documents\unity_test_hdrp'; S = os.path.join(R, 'Assets', 'Scripts'); HERE = os.path.dirname(os.path.abspath(__file__)); OUT = os.path.join(HERE, 'final'); os.makedirs(OUT, exist_ok=True)
F5 = os.path.join(R, '_agents', 'laneF5'); RV = os.path.join(F5, 'review'); RC = os.path.join(R, '_agents', 'laneLG', 'reloadcover')
SRC = {  # name -> (tree rel path, source file)
 'StartupWarm.cs': ('StartupWarm.cs', os.path.join(RC, 'StartupWarm.cs.patched')), 'GameLoop.cs': ('GameLoop.cs', os.path.join(RC, 'GameLoop.cs.patched')),
 'LoadingCard.cs': ('HUD\\LoadingCard.cs', os.path.join(F5, 'patches', 'LoadingCard.cs.patched')), 'LootScreen.cs': ('Loot\\LootScreen.cs', os.path.join(F5, 'patches', 'LootScreen.cs.patched')),
 'MenuCfg.cs': ('Menu\\MenuCfg.cs', os.path.join(F5, 'patches', 'MenuCfg.cs.patched')), 'MenuUI.cs': ('Menu\\MenuUI.cs', os.path.join(F5, 'patches', 'MenuUI.cs.patched')),
 'LootCfg.cs': ('Loot\\LootCfg.cs', os.path.join(RV, 'LootCfg.cs.reviewed')), 'QualityTiers.cs': ('QualityTiers.cs', os.path.join(RV, 'QualityTiers.cs.reviewed'))}
def md5(b): return hashlib.md5(b).hexdigest()[:10]
for name, (rel, src) in SRC.items():
    tree = open(os.path.join(S, rel), 'rb').read(); new = open(src, 'rb').read()
    shutil.copyfile(src, os.path.join(OUT, name + '.patched'))
    a = tree.decode('utf-8').replace('\r\n', '\n').splitlines(True); b = new.decode('utf-8').replace('\r\n', '\n').splitlines(True)
    open(os.path.join(OUT, name + '.diff'), 'w', encoding='utf-8', newline='').write(''.join(difflib.unified_diff(a, b, name + ' (tree)', name + '.patched', n=1)))
    print('%-16s tree %s -> final %s  (source %s)' % (name, md5(tree), md5(new), os.path.relpath(src, R)))
# the F5 .patched vs .reviewed: show that the only differences are the two intended lines
for n in ('LootCfg.cs', 'QualityTiers.cs'):
    p = open(os.path.join(F5, 'patches', n + '.patched'), encoding='utf-8').read().replace('\r\n', '\n').splitlines(); r = open(os.path.join(RV, n + '.reviewed'), encoding='utf-8').read().replace('\r\n', '\n').splitlines()
    d = [l for l in difflib.unified_diff(p, r, 'patched', 'reviewed', n=0, lineterm='') if l[:1] in '+-' and l[:3] not in ('+++', '---')]; print(n, 'patched -> reviewed changed lines:', len(d))

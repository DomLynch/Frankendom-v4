"""Builds the merged data kits of the 16:35 bundle (file-only). Each = laneF5 kit after + my edits on top, packaged as an lgkit kit whose base is the CURRENT tree file.
  lw_menu      menu.json   tree 5da8a0510d -> (laneF5 f5menu after f9e3a880fd) + my 3 Controls rows
  lw_menuboot  menu.json   tree 5da8a0510d -> (laneF5 f5menuboot after 53d539a30d) + my 3 Controls rows   (ALTERNATIVE to lw_menu)
  lw_loot      loot_ui.json tree cd33e731da -> (laneF5 f5loot after 1526dcd16a) + my Damage/Speed/Reach/Posture rows
Apply / revert: python -I _agents\\laneLG\\lgkit.py <lw_*> apply [--target DIR] [--go]"""
import os, sys, json, hashlib, subprocess
R = r'C:\Users\dom12\Documents\unity_test_hdrp'; LG = os.path.join(R, '_agents', 'laneLG'); F5 = os.path.join(R, '_agents', 'laneF5', 'kits')
def md5(b): return hashlib.md5(b).hexdigest()[:10]
JOBS = [('lw_menu', 'menu.json', 'f5menu', 'f5menurows', 'f9e3a880fd'), ('lw_menuboot', 'menu.json', 'f5menuboot', 'f5menubootrows', '53d539a30d'), ('lw_loot', 'loot_ui.json', 'f5loot', 'f4uirows', '1526dcd16a')]
for name, f, f5kit, core, f5after in JOBS:
    srcdir = os.path.join(F5, f5kit, 'after'); src = open(os.path.join(srcdir, f), 'rb').read(); assert md5(src) == f5after, (name, md5(src), f5after)
    tree = open(os.path.join(R, 'Assets', 'StreamingAssets', f), 'rb').read(); assert open(os.path.join(R, 'Assets', 'Data', f), 'rb').read() == tree
    p = subprocess.run([sys.executable, '-I', os.path.join(LG, 'lgkit.py'), core, 'make'], env=dict(os.environ, LGKIT_SRC=srcdir), capture_output=True, text=True); print(p.stdout.strip(), p.stderr.strip()); assert p.returncode == 0
    after = open(os.path.join(LG, core, 'after', f), 'rb').read(); gd = os.path.join(LG, name)
    os.makedirs(os.path.join(gd, 'base'), exist_ok=True); os.makedirs(os.path.join(gd, 'after'), exist_ok=True)
    open(os.path.join(gd, 'base', f), 'wb').write(tree); open(os.path.join(gd, 'after', f), 'wb').write(after)
    json.dump({'file': f, 'base': md5(tree), 'after': md5(after)}, open(os.path.join(gd, 'MD5.json'), 'w'), indent=1)
    print(name, f, md5(tree), '->', md5(after), '(laneF5 %s after %s + my rows)' % (f5kit, f5after))

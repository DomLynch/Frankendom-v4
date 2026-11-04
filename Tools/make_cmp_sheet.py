import sys, os
from PIL import Image, ImageDraw, ImageFont

root = r'C:\Users\dom12\Documents\unity_test\Shots'
fps = {}
for a in sys.argv[2:]:
    k, v = a.split('='); fps[k] = v

def font(sz):
    for p in (r'C:\Windows\Fonts\segoeui.ttf', r'C:\Windows\Fonts\arial.ttf'):
        if os.path.exists(p): return ImageFont.truetype(p, sz)
    return ImageFont.load_default()

def tile(name, f, label, crop=None, size=(760, 480)):
    im = Image.open(os.path.join(root, '_cmp', name, f)).convert('RGB')
    if crop: im = im.crop(crop)
    im = im.resize(size, Image.LANCZOS)
    d = ImageDraw.Draw(im); ft = font(22)
    d.rectangle([0, 0, size[0], 34], fill=(0, 0, 0)); d.text((10, 5), label, fill=(255, 255, 255), font=ft)
    return im

crop = (620, 180, 1340, 630)   # the fight: both fighters and the weapon arc
tiles = [
    tile('A', 'cmp_hit.png', f'A  trail = V3 cream, rim 10   fps {fps.get("A","?")}', crop),
    tile('B', 'cmp_hit.png', f'B  trail = Goose red ember, rim 10   fps {fps.get("B","?")}', crop),
    tile('C', 'cmp_hit.png', f'C  rim OFF (cream)   fps {fps.get("C","?")}', crop),
    tile('D', 'cmp_hit.png', f'D  rim 10 (cream)   fps {fps.get("D","?")}', crop),
    tile('E', 'cmp_hit.png', f'E  rim 35 (cream)   fps {fps.get("E","?")}', crop),
    tile('A', 'cmp_wide.png', f'A  wide gameplay, cream   fps {fps.get("A","?")}', None),
    tile('B', 'cmp_wide.png', f'B  wide gameplay, ember   fps {fps.get("B","?")}', None),
]
W, H = 760, 480
sheet = Image.new('RGB', (W * 2, H * 4), (20, 20, 20))
pos = [(0, 0), (1, 0), (0, 1), (1, 1), (0, 2), (0, 3), (1, 3)]
for t, (cx, cy) in zip(tiles, pos): sheet.paste(t, (cx * W, cy * H))
d = ImageDraw.Draw(sheet); d.text((W + 20, 2 * H + 200), 'same hit frame (fixed seed, deterministic 60 Hz step)', fill=(200, 200, 200), font=font(22))
sheet.save(sys.argv[1]); print(sheet.size)

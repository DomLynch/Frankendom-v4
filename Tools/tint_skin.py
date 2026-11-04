from PIL import Image, ImageEnhance
import numpy as np, sys
src={"skin_A":"08_13_37","skin_B":"08_14_04","skin_C":"08_14_19"}
for n,t in src.items():
    im=Image.open(rf"C:\Users\dom12\Downloads\ChatGPT Image Nov 4, 2026, {t} AM.png").convert("RGB")
    a=np.asarray(im).astype(np.float32)/255
    g=a.mean(axis=2,keepdims=True)
    a=g+(a-g)*0.72            # desaturate
    a=a*np.array([1.0,0.97,0.95])  # cool slightly
    a=0.5+(a-0.5)*1.0
    a=a+0.08*(1-a)            # lift toward pale
    a=np.clip(a,0,1)
    Image.fromarray((a*255).astype(np.uint8)).save(rf"Assets\Characters\Dracula\SkinTiles\{n}_pale.png")


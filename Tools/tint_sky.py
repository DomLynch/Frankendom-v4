import numpy as np
from PIL import Image
Image.MAX_IMAGE_PIXELS=None
src=r'C:\Users\dom12\Documents\unity_test_hdrp\Assets\VFX\Gateway\sky_blacksun_8k_s15.png'
dst=r'C:\Users\dom12\Documents\unity_test_hdrp\Assets\VFX\Gateway\sky_blacksun_8k_s15t.png'
a=np.asarray(Image.open(src).convert('RGB')).astype(np.float32)
a*=np.array([1.0,0.92,0.89],dtype=np.float32)   # slight warm/crimson tint (~10%)
Image.fromarray(np.clip(a,0,255).astype(np.uint8)).save(dst); print('ok')

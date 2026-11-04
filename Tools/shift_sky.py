import numpy as np
from PIL import Image
Image.MAX_IMAGE_PIXELS=None
src=r'C:\Users\dom12\Documents\unity_test_hdrp\Assets\VFX\Gateway\rift_blacksun_8k.png'
dst=r'C:\Users\dom12\Documents\unity_test_hdrp\Assets\VFX\Gateway\sky_blacksun_8k_s15.png'
im=Image.open(src).convert('RGB'); a=np.asarray(im); h=a.shape[0]
shift=int(round(h*15.0/180.0))   # move the whole picture down by 15 deg of latitude: the eclipse drops into the frame
out=np.empty_like(a); out[shift:]=a[:h-shift]; out[:shift]=a[0:1]   # replicate the (dark, faded) top row into the new top band
Image.fromarray(out).save(dst); print(a.shape, shift)

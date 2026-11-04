import numpy as np
from PIL import Image
N=1024
rng=np.random.default_rng(7)
def band(freq_lo, freq_hi, seed):
    r=np.random.default_rng(seed); f=np.fft.fftfreq(N)[:,None]**2+np.fft.fftfreq(N)[None,:]**2; f=np.sqrt(f)*N
    spec=(r.normal(size=(N,N))+1j*r.normal(size=(N,N)))
    mask=((f>=freq_lo)&(f<freq_hi)).astype(float)*(1.0/np.maximum(f,1))
    h=np.real(np.fft.ifft2(spec*mask)); return h/np.abs(h).max()
h=1.0*band(2,6,1)+0.45*band(10,28,2)   # two octaves: slow swell + fine ripple, tileable by construction (FFT)
gy,gx=np.gradient(h); k=14.0
nx=-gx*k; ny=-gy*k; nz=np.ones_like(h); l=np.sqrt(nx*nx+ny*ny+nz*nz); nx/=l; ny/=l; nz/=l
img=np.stack([(nx*0.5+0.5),(ny*0.5+0.5),(nz*0.5+0.5)],-1); Image.fromarray((img*255).astype(np.uint8)).save(r'C:\Users\dom12\Documents\unity_test_hdrp\Assets\HDRP\water_normal.png'); print('saved')

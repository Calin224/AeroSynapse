"""Original large-format terrazzo and apron maps for the architectural terminal."""
from pathlib import Path
import numpy as np
from PIL import Image, ImageFilter

root=Path(__file__).resolve().parent/'materials'
rng=np.random.default_rng(130405)
size=1024
grain=rng.normal(0,1.6,(size,size))
flecks=rng.random((size,size))
grain += np.where(flecks>.992,14,np.where(flecks<.008,-18,0))
tile=np.zeros((size,size))
tile[:512,:512]=-2;tile[512:,512:]=2
rgb=np.clip(np.array([177,181,177])[None,None,:]+(grain+tile)[:,:,None],0,255).astype('uint8')
rgb[[0,511,512,1023],:]=[113,122,125]
rgb[:,[0,511,512,1023]]=[113,122,125]
Image.fromarray(rgb).save(root/'terminal-terrazzo.jpg',quality=88,subsampling=0,optimize=True)
rough=np.clip(74+rng.normal(0,1.5,(512,512)),0,255).astype('uint8')
Image.fromarray(rough).save(root/'terminal-terrazzo-rough.png',optimize=True)
height=np.asarray(Image.fromarray(rng.integers(124,132,(512,512),dtype='uint8')).filter(ImageFilter.GaussianBlur(1)),dtype=float)
dy,dx=np.gradient(height)
normal=np.stack([128-dx*4,128-dy*4,np.full_like(dx,254)],axis=-1)
Image.fromarray(np.clip(normal,0,255).astype('uint8')).save(root/'terminal-terrazzo-normal.png',optimize=True)
asphalt=np.clip(79+rng.normal(0,5,(512,512)),0,255).astype('uint8')
Image.fromarray(asphalt).save(root/'terminal-apron.jpg',quality=86,optimize=True)
print('Generated original terrazzo/roughness/apron maps')

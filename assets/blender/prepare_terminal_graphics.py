"""Generate original carpet and display maps; run with the bundled Python/Pillow."""
from pathlib import Path
import json, hashlib
import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT=Path(__file__).resolve().parent/'materials'
rng=np.random.default_rng(1304)
y,x=np.mgrid[0:512,0:512]
grain=rng.normal(0,5,(512,512))+2*np.sin(x*1.57)+2*np.cos(y*1.57)
rgb=np.clip(np.array([39,50,57])[None,None,:]+grain[:,:,None],0,255).astype('uint8')
Image.fromarray(rgb).save(ROOT/'terminal-carpet.jpg',quality=90,subsampling=0)

atlas=Image.new('RGB',(1024,1024),'#0b1624')
draw=ImageDraw.Draw(atlas)
bold=lambda size:ImageFont.truetype(str(ROOT/'Lato-Bold.ttf'),size)
regular=lambda size:ImageFont.truetype(str(ROOT/'Lato-Regular.ttf'),size)
destinations=['BUCHAREST','LONDON','ROME','MILAN','PARIS']
flight_codes=['AS 201','AS 305','AS 412','AS 508','AS 613']
for row in range(8):
    yy=row*128; gate=row<5
    accent='#ddae54' if gate else '#6abfc4'
    draw.rectangle((0,yy,1023,yy+127),fill='#101d2c')
    draw.rectangle((0,yy,13,yy+127),fill=accent)
    code=f'G-{row+1}' if gate else f'CP-{row-4}'
    draw.text((34,yy+10),code,font=bold(83),fill='#eef3f4')
    draw.line((240,yy+20,240,yy+108),fill='#5b6b7a',width=2)
    draw.text((270,yy+13),'BOARDING GATE' if gate else 'SECURITY CONTROL',font=bold(32),fill=accent)
    draw.text((270,yy+59),f'{flight_codes[row]}  /  {destinations[row]}' if gate else 'PREGATESTE DOCUMENTELE SI BAGAJELE',font=regular(25),fill='#eef3f4')
    draw.text((270,yy+97),'AEROSYNAPSE  /  DEMONSTRATIVE TERMINAL',font=regular(17),fill='#a9bac8')
atlas.save(ROOT/'terminal-display-atlas.png',optimize=True)

credits=json.loads((ROOT/'asset-credits.json').read_text())
for asset in credits['textures']:
    for item in asset['maps']:
        path=ROOT/item['file']
        item['runtimeResolution']=list(Image.open(path).size)
        item['runtimeBytes']=path.stat().st_size
        item['runtimeSha256']=hashlib.sha256(path.read_bytes()).hexdigest()
credits['originalGraphics']=['terminal-carpet.jpg','terminal-display-atlas.png']
(ROOT/'asset-credits.json').write_text(json.dumps(credits,ensure_ascii=False,indent=2))
print('Generated carpet and eight high-resolution airport display panels')

"""Regenerate RimLayers' original 128px elevator and roof sprites (developer-only Pillow)."""
from pathlib import Path
from PIL import Image, ImageDraw
root = Path(__file__).parent
for name, color in [("LiftSingle", (78,145,159)), ("LiftVertical", (154,124,75))]:
    im=Image.new("RGBA",(128,128));d=ImageDraw.Draw(im)
    d.rounded_rectangle((5,5,122,122),radius=7,fill=(27,30,31),outline=(15,17,18),width=4)
    d.rectangle((12,12,115,115),fill=(91,94,93));d.rectangle((20,22,107,105),fill=(34,40,42),outline=(14,19,20),width=4)
    d.rectangle((27,28,62,99),fill=color,outline=(19,25,27),width=3)
    d.rectangle((66,28,100,99),fill=tuple(int(c*.78) for c in color),outline=(19,25,27),width=3)
    for x in [16,111]:
        for y in [17,110]:d.ellipse((x-3,y-3,x+3,y+3),fill=(202,202,183),outline=(34,34,31))
    d.line((25,27,101,27),fill=(190,209,207),width=3)
    d.polygon([(49,48),(57,37),(65,48),(60,48),(60,59),(54,59),(54,48)],fill=(229,229,207))
    if name=="LiftVertical":d.polygon([(72,82),(80,93),(88,82),(83,82),(83,71),(77,71),(77,82)],fill=(229,229,207))
    path=root/f"Things/Building/RimLayers/{name}.png";path.parent.mkdir(parents=True,exist_ok=True);im.save(path)
for name, base in [("RoofSurface",(111,117,119)),("ShipRoofSurface",(82,104,119))]:
    im=Image.new("RGBA",(128,128),base+(255,));d=ImageDraw.Draw(im)
    if name=="RoofSurface":
        for y in range(0,128,32):
            d.line((0,y,127,y),fill=(67,74,77),width=3);d.line((0,y+3,127,y+3),fill=(141,146,145),width=2)
        d.line((0,0,0,127),fill=(67,74,77),width=3)
    else:
        d.rectangle((2,2,125,125),outline=(43,57,66),width=4);d.line((7,7,120,7),fill=(147,162,170),width=3)
        for x,y in [(10,10),(117,10),(10,117),(117,117)]:d.ellipse((x-2,y-2,x+2,y+2),fill=(178,184,178))
    path=root/f"Roof/{name}.png";path.parent.mkdir(parents=True,exist_ok=True);im.save(path)

"""Regenerate RimLayers' original 128px elevator and roof sprites (developer-only Pillow).

Also derives the "down" variants from their "up" counterparts, so the arrow direction on a
building always matches the direction it actually travels. Stairs.png is hand-authored and is
only read here, never written.
"""
from pathlib import Path
from PIL import Image, ImageDraw
root = Path(__file__).parent

# Arrow polygon in closed form, so the down variant is an exact vertical mirror and not a
# hand-copied second set of numbers.
UP_ARROW = [(49, 48), (57, 37), (65, 48), (60, 48), (60, 59), (54, 59), (54, 48)]
DOWN_ARROW = [(49, 48), (57, 59), (65, 48), (60, 48), (60, 37), (54, 37), (54, 48)]

for name, color in [("LiftSingle", (78, 145, 159)), ("LiftVertical", (154, 124, 75))]:
    doors = [("LiftSingleDown", color, DOWN_ARROW)] if name == "LiftSingle" else []
    doors.append((name, color, UP_ARROW))
    for out_name, out_color, arrow in doors:
        im = Image.new("RGBA", (128, 128)); d = ImageDraw.Draw(im)
        d.rounded_rectangle((5, 5, 122, 122), radius=7, fill=(27, 30, 31), outline=(15, 17, 18), width=4)
        d.rectangle((12, 12, 115, 115), fill=(91, 94, 93)); d.rectangle((20, 22, 107, 105), fill=(34, 40, 42), outline=(14, 19, 20), width=4)
        d.rectangle((27, 28, 62, 99), fill=out_color, outline=(19, 25, 27), width=3)
        d.rectangle((66, 28, 100, 99), fill=tuple(int(c * .78) for c in out_color), outline=(19, 25, 27), width=3)
        for x in [16, 111]:
            for y in [17, 110]: d.ellipse((x - 3, y - 3, x + 3, y + 3), fill=(202, 202, 183), outline=(34, 34, 31))
        d.line((25, 27, 101, 27), fill=(190, 209, 207), width=3)
        d.polygon(arrow, fill=(229, 229, 207))
        if name == "LiftVertical": d.polygon([(72, 82), (80, 93), (88, 82), (83, 82), (83, 71), (77, 71), (77, 82)], fill=(229, 229, 207))
        path = root / f"Things/Building/RimLayers/{out_name}.png"; path.parent.mkdir(parents=True, exist_ok=True)
        im.save(path)

# Stairs.png is hand-authored: flip only the arrow pixels (fill + its own outline) inside the
# arrow's bounding box, and fill the vacated pixels from the nearest same-row non-arrow pixel so
# the horizontal plank banding stays continuous. A whole-image flip leaves a visible rectangular
# seam because the banding is not vertically symmetric.
stairs = root / "Things/Building/RimLayers/Stairs.png"
im = Image.open(stairs).convert("RGBA"); px = im.load(); w, h = im.size
arrow = {(x, y) for y in range(h) for x in range(w)
         if px[x, y][3] and px[x, y][:3] in {(222, 211, 154), (61, 56, 40)}}
x0, x1 = min(p[0] for p in arrow), max(p[0] for p in arrow)
y0, y1 = min(p[1] for p in arrow), max(p[1] for p in arrow)
out = im.copy(); q = out.load()
for y in range(y0, y1 + 1):
    row = [x for x in range(w) if (x, y) not in arrow]
    for x in range(x0, x1 + 1):
        if (x, y) in arrow: q[x, y] = px[min(row, key=lambda c: abs(c - x)), y]
for (x, y) in arrow: q[x, y0 + (y1 - y)] = px[x, y]
out.save(root / "Things/Building/RimLayers/StairsDown.png")

for name, base in [("RoofSurface", (111, 117, 119)), ("ShipRoofSurface", (82, 104, 119))]:
    im = Image.new("RGBA", (128, 128), base + (255,)); d = ImageDraw.Draw(im)
    if name == "RoofSurface":
        for y in range(0, 128, 32):
            d.line((0, y, 127, y), fill=(67, 74, 77), width=3); d.line((0, y + 3, 127, y + 3), fill=(141, 146, 145), width=2)
        d.line((0, 0, 0, 127), fill=(67, 74, 77), width=3)
    else:
        d.rectangle((2, 2, 125, 125), outline=(43, 57, 66), width=4); d.line((7, 7, 120, 7), fill=(147, 162, 170), width=3)
        for x, y in [(10, 10), (117, 10), (10, 117), (117, 117)]: d.ellipse((x - 2, y - 2, x + 2, y + 2), fill=(178, 184, 178))
    path = root / f"Roof/{name}.png"; path.parent.mkdir(parents=True, exist_ok=True); im.save(path)

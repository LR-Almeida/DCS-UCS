"""Draws the DCS-UCS icons: two sync arrows around a three-blade rotor, on a dark blue rounded square.

Variants (badge in the lower right corner): none = UCS-Client and installer, "server" = UCS-Server
(network nodes), "profilegen" = UCS-ProfileGen (profile sheet).
Usage: make_icon.py ico <out.ico> [server|profilegen]   |   make_icon.py preview <out.png>
"""
import math, sys
from PIL import Image, ImageDraw

S = 1024
BG_TOP, BG_BOTTOM = (22, 42, 78), (10, 22, 44)
ARROW = (255, 176, 32)      # amber
ROTOR = (235, 242, 250)     # near white

def render(size_out, path=None, badge=None):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    # rounded square with vertical gradient
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        k = y / (S - 1)
        c = tuple(int(BG_TOP[i] * (1 - k) + BG_BOTTOM[i] * k) for i in range(3)) + (255,)
        gd.line([(0, y), (S, y)], fill=c)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([24, 24, S - 24, S - 24], radius=200, fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)

    cx = cy = S / 2
    # two circular arrows (sync)
    r_out, w = 360, 78
    box = [cx - r_out, cy - r_out, cx + r_out, cy + r_out]
    for start in (200, 20):                       # two arcs of ~140 degrees
        d.arc(box, start=start, end=start + 140, fill=ARROW, width=w)
        # arrow head at the end of the arc
        a = math.radians(start + 140)
        tip_r = r_out - w / 2
        px, py = cx + tip_r * math.cos(a), cy + tip_r * math.sin(a)
        tang = a + math.pi / 2                    # direction of travel (clockwise in screen coords)
        L, Wd = 150, 120
        tip = (px + L * 0.55 * math.cos(tang), py + L * 0.55 * math.sin(tang))
        back = (px - L * 0.45 * math.cos(tang), py - L * 0.45 * math.sin(tang))
        nx, ny = math.cos(a), math.sin(a)
        p1 = (back[0] + Wd * nx, back[1] + Wd * ny)
        p2 = (back[0] - Wd * nx, back[1] - Wd * ny)
        d.polygon([tip, p1, p2], fill=ARROW)

    # three-blade rotor
    blade_len, blade_w = 255, 66
    for k in range(3):
        a = math.radians(-90 + k * 120)
        ex, ey = cx + blade_len * math.cos(a), cy + blade_len * math.sin(a)
        d.line([(cx, cy), (ex, ey)], fill=ROTOR, width=blade_w)
        d.ellipse([ex - blade_w / 2, ey - blade_w / 2, ex + blade_w / 2, ey + blade_w / 2], fill=ROTOR)
    hub = 86
    d.ellipse([cx - hub, cy - hub, cx + hub, cy + hub], fill=ROTOR)
    d.ellipse([cx - 34, cy - 34, cx + 34, cy + 34], fill=BG_TOP)

    if badge:
        draw_badge(d, badge)

    out = img.resize((size_out, size_out), Image.LANCZOS)
    if path: out.save(path)
    return out

BADGE_FILL = {"server": (34, 160, 96), "profilegen": (130, 82, 200)}
WHITE = (255, 255, 255)

def draw_badge(d, kind):
    bx, by, br = 752, 752, 226          # circle in the lower right corner
    d.ellipse([bx - br - 22, by - br - 22, bx + br + 22, by + br + 22], fill=(10, 22, 44))   # separating ring
    d.ellipse([bx - br, by - br, bx + br, by + br], fill=BADGE_FILL[kind])
    if kind == "server":
        # three network nodes linked in a triangle
        nodes = [(bx, by - 112), (bx - 108, by + 78), (bx + 108, by + 78)]
        for i in range(3):
            d.line([nodes[i], nodes[(i + 1) % 3]], fill=WHITE, width=30)
        for x, y in nodes:
            d.ellipse([x - 50, y - 50, x + 50, y + 50], fill=WHITE)
    else:
        # profile sheet with a folded corner and text lines
        x0, y0, x1, y1, fold = bx - 100, by - 135, bx + 100, by + 135, 62
        d.polygon([(x0, y0), (x1 - fold, y0), (x1, y0 + fold), (x1, y1), (x0, y1)], fill=WHITE)
        d.polygon([(x1 - fold, y0), (x1 - fold, y0 + fold), (x1, y0 + fold)], fill=(200, 185, 235))
        for k, w in enumerate((130, 130, 90)):
            yy = y0 + 110 + k * 52
            d.line([(x0 + 35, yy), (x0 + 35 + w, yy)], fill=BADGE_FILL[kind], width=24)

if __name__ == "__main__":
    mode = sys.argv[1]
    if mode == "preview":
        sheet = Image.new("RGBA", (256 + 128 + 64 + 48 + 32 + 16 + 7 * 20, 3 * 276), (240, 240, 240, 255))
        for row, badge in enumerate((None, "server", "profilegen")):
            x = 10
            for sz in (256, 128, 64, 48, 32, 16):
                im = render(sz, badge=badge)
                sheet.paste(im, (x, 10 + row * 276), im)
                x += sz + 20
        sheet.save(sys.argv[2])
    else:
        badge = sys.argv[3] if len(sys.argv) > 3 else None
        big = render(256, badge=badge)
        big.save(sys.argv[2], format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
        render(512, sys.argv[2].replace(".ico", ".png"), badge=badge)

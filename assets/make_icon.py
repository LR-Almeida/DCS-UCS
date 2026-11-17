"""Draws the DCS-UCS icon: two sync arrows around a three-blade rotor, on a dark blue rounded square."""
import math, sys
from PIL import Image, ImageDraw

S = 1024
BG_TOP, BG_BOTTOM = (22, 42, 78), (10, 22, 44)
ARROW = (255, 176, 32)      # amber
ROTOR = (235, 242, 250)     # near white

def render(size_out, path=None):
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

    out = img.resize((size_out, size_out), Image.LANCZOS)
    if path: out.save(path)
    return out

if __name__ == "__main__":
    mode = sys.argv[1]
    if mode == "preview":
        sheet = Image.new("RGBA", (256 + 128 + 64 + 48 + 32 + 16 + 7 * 20, 276), (240, 240, 240, 255))
        x = 10
        for sz in (256, 128, 64, 48, 32, 16):
            sheet.paste(render(sz), (x, 10), render(sz))
            x += sz + 20
        sheet.save(sys.argv[2])
    else:
        big = render(256)
        big.save(sys.argv[2], format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
        render(512, sys.argv[2].replace(".ico", ".png"))

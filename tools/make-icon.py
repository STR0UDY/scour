"""Draws Scour's icon: a teal rounded square with a white sparkle.

Usage: python tools/make-icon.py   (needs Pillow)
Writes src/Scour.App/Assets/scour.ico and docs/assets/icon.png.
"""
import os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = 1024


def lerp(a, b, t):
    return tuple(round(x + (y - x) * t) for x, y in zip(a, b))


def sparkle(draw, cx, cy, r, waist, fill):
    """A four-point star: long arms, pinched waist."""
    pts = []
    for i in range(8):
        import math
        angle = math.pi / 4 * i - math.pi / 2
        radius = r if i % 2 == 0 else r * waist
        pts.append((cx + radius * math.cos(angle), cy + radius * math.sin(angle)))
    draw.polygon(pts, fill=fill)


def draw_icon():
    top, bottom = (20, 184, 166), (13, 110, 140)  # teal → deep teal
    gradient = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(gradient)
    for y in range(S):
        gd.line([(0, y), (S, y)], fill=lerp(top, bottom, y / S) + (255,))

    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([40, 40, S - 40, S - 40], radius=220, fill=255)

    icon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    icon.paste(gradient, (0, 0), mask)

    d = ImageDraw.Draw(icon)
    white = (255, 255, 255, 255)
    sparkle(d, 470, 560, 330, 0.22, white)
    sparkle(d, 760, 290, 140, 0.25, (255, 255, 255, 230))
    sparkle(d, 300, 250, 70, 0.3, (255, 255, 255, 200))
    return icon


def main():
    icon = draw_icon()
    ico_path = os.path.join(ROOT, "src", "Scour.App", "Assets", "scour.ico")
    os.makedirs(os.path.dirname(ico_path), exist_ok=True)
    icon.save(ico_path, sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])

    icon.resize((64, 64), Image.LANCZOS).save(os.path.join(ROOT, "src", "Scour.App", "Assets", "scour.png"))

    png_path = os.path.join(ROOT, "docs", "assets", "icon.png")
    os.makedirs(os.path.dirname(png_path), exist_ok=True)
    icon.resize((256, 256), Image.LANCZOS).save(png_path)
    print("wrote", ico_path, "and", png_path)


if __name__ == "__main__":
    main()

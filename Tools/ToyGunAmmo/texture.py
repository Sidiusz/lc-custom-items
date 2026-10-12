"""Текстура пачки пулек для игрушечного пистолета: оранжевый картон, надпись,
рисунок жёлтых пулек. Запуск из корня репозитория: python3 Tools/ToyGunAmmo/texture.py
Пишет Assets/Sources/Art/Textures/ToyGunAmmo/ToyGunAmmo.png (нужен Pillow).
"""
import os
import random
import sys

from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from layout import RECTS, TEXTURE_SIZE  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "Assets", "Sources", "Art", "Textures", "ToyGunAmmo", "ToyGunAmmo.png")

ORANGE = (242, 140, 40)
ORANGE_DARK = (205, 105, 25)
BLUE = (40, 70, 140)
WHITE = (250, 250, 245)
YELLOW = (255, 215, 60)


def font(size, names=("DejaVuSans-Bold.ttf", "LiberationSans-Bold.ttf", "arialbd.ttf")):
    for root in ("/usr/share/fonts", "C:/Windows/Fonts", "/Library/Fonts", "/System/Library/Fonts"):
        for dirpath, _, files in os.walk(root):
            for n in names:
                if n in files:
                    return ImageFont.truetype(os.path.join(dirpath, n), size)
    return ImageFont.load_default()


def centered(draw, box, text, fnt, fill):
    x0, y0, x1, y1 = box
    l, t, r, b = draw.textbbox((0, 0), text, font=fnt)
    draw.text(((x0 + x1 - (r - l)) / 2 - l, (y0 + y1 - (b - t)) / 2 - t), text, font=fnt, fill=fill)


def pellets(draw, box, rng, count, radius):
    x0, y0, x1, y1 = box
    for _ in range(count):
        cx, cy = rng.uniform(x0 + radius, x1 - radius), rng.uniform(y0 + radius, y1 - radius)
        draw.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill=YELLOW, outline=ORANGE_DARK)
        h = radius * 0.35
        draw.ellipse((cx - radius * 0.5 - h, cy - radius * 0.5 - h, cx - radius * 0.5 + h, cy - radius * 0.5 + h),
                     fill=WHITE)


def label(draw, rect, rng):
    x0, y0, x1, y1 = rect
    w, h = x1 - x0, y1 - y0
    draw.rectangle(rect, fill=ORANGE)
    draw.rectangle((x0, y0, x1, y0 + h * 0.3), fill=BLUE)
    centered(draw, (x0, y0, x1, y0 + h * 0.3), "TOY PELLETS", font(int(h * 0.19)), WHITE)
    # Окошко с пульками
    win = (x0 + w * 0.06, y0 + h * 0.38, x0 + w * 0.5, y1 - h * 0.1)
    draw.rounded_rectangle(win, radius=8, fill=(250, 235, 200), outline=BLUE, width=3)
    pellets(draw, (win[0] + 4, win[1] + 4, win[2] - 4, win[3] - 4), rng, 26, h * 0.06)
    centered(draw, (x0 + w * 0.5, y0 + h * 0.38, x1, y0 + h * 0.66), "6 mm", font(int(h * 0.2)), BLUE)
    centered(draw, (x0 + w * 0.5, y0 + h * 0.66, x1, y1 - h * 0.08), "BB", font(int(h * 0.16)), WHITE)


def side(draw, rect, rng):
    x0, y0, x1, y1 = rect
    draw.rectangle(rect, fill=ORANGE)
    h = y1 - y0
    draw.rectangle((x0, y0, x1, y0 + h * 0.3), fill=BLUE)
    pellets(draw, (x0 + 8, y0 + h * 0.4, x1 - 8, y1 - 8), rng, 6, h * 0.07)


def flap(draw, rect):
    x0, y0, x1, y1 = rect
    draw.rectangle(rect, fill=ORANGE)
    # Линия клапана и надпись
    draw.line((x0, (y0 + y1) / 2, x1, (y0 + y1) / 2), fill=ORANGE_DARK, width=3)
    centered(draw, (x0, y0, x1, (y0 + y1) / 2), "OPEN HERE", font(int((y1 - y0) * 0.2)), BLUE)


def main():
    rng = random.Random(42)
    img = Image.new("RGB", (TEXTURE_SIZE, TEXTURE_SIZE), ORANGE)
    draw = ImageDraw.Draw(img)
    label(draw, RECTS["front"], rng)
    label(draw, RECTS["back"], rng)
    side(draw, RECTS["right"], rng)
    side(draw, RECTS["left"], rng)
    flap(draw, RECTS["top"])
    draw.rectangle(RECTS["bottom"], fill=ORANGE_DARK)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    img.save(OUT)
    print("Текстура:", OUT)


if __name__ == "__main__":
    main()

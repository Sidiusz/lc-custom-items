"""Текстура бутылки Егермейстера (512x512) для Assets/Sources/Art/Textures/Jagermeister.

Запуск: python texture.py   (нужен Pillow: pip install pillow)

Раскладка UV, которую ожидает model.py:
  u: обхват бутылки, 0.5 = перед (этикетка), шов сзади;
  v 0.00-0.80: стекло от дна (0) до верха горлышка (0.80);
  v 0.82-1.00: крышка.

Альфа: стекло полупрозрачное (сквозь него видна жидкость), этикетка, лента на
горлышке и крышка непрозрачные. Материал стекла в Unity прозрачный (ItemAssets.cs).
"""
import math
import os
import random

from PIL import Image, ImageDraw, ImageFilter, ImageFont

SIZE = 512
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "Assets", "Sources", "Art", "Textures", "Jagermeister", "Jagermeister.png")

GLASS = (34, 78, 44)
GLASS_LIGHT = (90, 140, 88)
GLASS_ALPHA = 120
CAP = (22, 34, 24)
ORANGE = (222, 112, 30)
CREAM = (236, 220, 178)
INK = (28, 36, 22)
STAG = (120, 74, 36)

random.seed(7)


def font(size, bold=True):
    names = ["DejaVuSerif-Bold.ttf", "DejaVuSans-Bold.ttf", "georgiab.ttf", "arialbd.ttf"]
    dirs = ["/usr/share/fonts/truetype/dejavu", "C:/Windows/Fonts", "/Library/Fonts", ""]
    for n in names:
        for d in dirs:
            try:
                return ImageFont.truetype(os.path.join(d, n), size)
            except OSError:
                pass
    return ImageFont.load_default()


def px(u, v):
    """UV -> пиксели (v растёт вверх, у картинки y вниз)."""
    return u * SIZE, (1 - v) * SIZE


def glass(img):
    d = ImageDraw.Draw(img)
    for x in range(SIZE):
        u = x / SIZE
        # Блики на четырёх скруглённых углах (u = 0.125, 0.375, ...) и лёгкая тень сзади
        k = max(0.0, math.cos((u - 0.125) * math.pi * 8)) ** 6
        back = 0.8 + 0.2 * math.cos((u - 0.5) * math.pi * 2)
        c = tuple(int((g + (l - g) * k) * back) for g, l in zip(GLASS, GLASS_LIGHT))
        d.line([(x, 0), (x, SIZE)], fill=c)


def cap(img):
    d = ImageDraw.Draw(img)
    top = px(0, 1.0)[1]
    bottom = px(0, 0.82)[1]
    d.rectangle([0, top, SIZE, bottom], fill=CAP)
    # Рифление крышки
    for i in range(48):
        x = i * SIZE / 48
        d.line([(x, top + 10), (x, bottom)], fill=(44, 60, 44), width=2)
    # Оранжевый ободок снизу и полоска сверху
    d.rectangle([0, bottom - 9, SIZE, bottom], fill=ORANGE)
    d.rectangle([0, top, SIZE, top + 8], fill=(60, 74, 58))


def neck_band(img):
    """Оранжевая лента на горлышке, видна под крышкой."""
    d = ImageDraw.Draw(img)
    y0 = px(0, NECK_BAND[0])[1]
    y1 = px(0, NECK_BAND[1])[1]
    d.rectangle([0, y0, SIZE, y1], fill=ORANGE)
    d.line([(0, y0), (SIZE, y0)], fill=INK, width=2)
    d.line([(0, y1), (SIZE, y1)], fill=INK, width=2)


NECK_BAND = (0.76, 0.70)


def label_shape():
    cx = SIZE / 2
    w = 0.24 * SIZE / 2
    top = px(0, 0.58)[1]
    bottom = px(0, 0.10)[1]
    peak = top - 22
    # Этикетка с «крышей» сверху, как у настоящей бутылки
    return cx, w, top, bottom, peak, [(cx - w, bottom), (cx - w, top), (cx, peak), (cx + w, top), (cx + w, bottom)]


def alpha_mask():
    """Непрозрачно всё, кроме стекла; на стекле этикетка и лента тоже непрозрачные."""
    mask = Image.new("L", (SIZE, SIZE), 255)
    d = ImageDraw.Draw(mask)
    d.rectangle([0, px(0, 0.80)[1], SIZE, SIZE], fill=GLASS_ALPHA)
    d.rectangle([0, px(0, NECK_BAND[0])[1], SIZE, px(0, NECK_BAND[1])[1]], fill=255)
    d.polygon(label_shape()[-1], fill=255)
    return mask


def label(img):
    d = ImageDraw.Draw(img)
    cx, w, top, bottom, peak, shape = label_shape()
    d.polygon(shape, fill=ORANGE)
    inner = [(cx - w + 6, bottom - 6), (cx - w + 6, top + 4), (cx, peak + 9),
             (cx + w - 6, top + 4), (cx + w - 6, bottom - 6)]
    d.polygon(inner, fill=CREAM, outline=INK, width=2)

    # Голова оленя с крестом между рогами
    sy = top + 70
    d.ellipse([cx - 16, sy - 6, cx + 16, sy + 30], fill=STAG, outline=INK, width=2)
    d.polygon([(cx - 10, sy + 22), (cx + 10, sy + 22), (cx, sy + 44)], fill=STAG, outline=INK)
    for s in (-1, 1):
        d.line([(cx + s * 10, sy), (cx + s * 30, sy - 34)], fill=STAG, width=4)
        d.line([(cx + s * 20, sy - 16), (cx + s * 34, sy - 14)], fill=STAG, width=3)
        d.line([(cx + s * 26, sy - 26), (cx + s * 22, sy - 44)], fill=STAG, width=3)
        d.ellipse([cx + s * 9 - 2, sy + 6, cx + s * 9 + 2, sy + 10], fill=INK)
    d.ellipse([cx - 13, sy - 52, cx + 13, sy - 26], fill=(250, 238, 160))
    d.rectangle([cx - 2, sy - 50, cx + 2, sy - 28], fill=ORANGE)
    d.rectangle([cx - 10, sy - 42, cx + 10, sy - 37], fill=ORANGE)

    # Надпись
    f = font(15)
    text = "Jägermeister"
    tw = d.textlength(text, font=f)
    d.text((cx - tw / 2, sy + 52), text, font=f, fill=INK)
    small = font(9)
    for i, t in enumerate(["KRÄUTERLIKÖR", "35% vol   0,7 L"]):
        tw = d.textlength(t, font=small)
        d.text((cx - tw / 2, sy + 80 + i * 14), t, font=small, fill=INK)
    d.line([(cx - w + 14, sy + 76), (cx + w - 14, sy + 76)], fill=ORANGE, width=2)


def grime(img):
    """Шум и потёртости, чтобы не выглядело стерильно на фоне ванильного скрапа."""
    noise = Image.effect_noise((SIZE, SIZE), 18).convert("L")
    noise = noise.filter(ImageFilter.GaussianBlur(0.6))
    dark = Image.new("RGB", (SIZE, SIZE), (10, 12, 8))
    mask = noise.point(lambda p: max(0, p - 120) * 0.6)
    img.paste(dark, (0, 0), mask)
    d = ImageDraw.Draw(img)
    for _ in range(40):
        x, y = random.uniform(0, SIZE), random.uniform(0, SIZE)
        r = random.uniform(0.8, 2.2)
        d.ellipse([x - r, y - r, x + r, y + r], fill=(40, 38, 30))


def main():
    img = Image.new("RGB", (SIZE, SIZE))
    glass(img)
    neck_band(img)
    label(img)
    cap(img)
    grime(img)
    img = img.quantize(48).convert("RGB")
    img.putalpha(alpha_mask())
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    img.save(os.path.normpath(OUT))
    print("saved", os.path.normpath(OUT))


if __name__ == "__main__":
    main()

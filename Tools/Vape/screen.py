"""
Картинка экрана вейпа, та же, что рисует VapeScreen.cs в игре (держите их одинаковыми).
Здесь она нужна для текстуры экрана по умолчанию (префаб, иконка) и для превью.

    python Tools/Vape/screen.py      # пишет Assets/Sources/Art/Textures/Vape<Модель>/Vape<Модель>Screen.png

Экраны (пиксели, начало координат сверху слева):
    Pixl 32x24   две большие семисегментные цифры заряда (максимум 99), батарейка, полоса нагрева
    Box  48x24   крупные «87%», полоса нагрева снизу
    Pod  24x64   «100» и молния, мощность «30W» (растёт с нагревом), огонь (поднимается с нагревом),
                 счётчик затяжек «00042»
"""
import os

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TEXTURES = os.path.join(ROOT, "Assets", "Sources", "Art", "Textures")


def tex_dir(style):
    """У каждого вейпа своя папка (ItemAssets ищет ассеты по имени предмета Vape<Модель>)."""
    return os.path.join(TEXTURES, f"Vape{style}")

SIZES = {"Pixl": (32, 24), "Box": (48, 24), "Pod": (24, 64)}

# Шрифт 3x5: строки сверху вниз, 1 = пиксель
FONT = {
    "0": ["111", "101", "101", "101", "111"],
    "1": ["010", "110", "010", "010", "111"],
    "2": ["111", "001", "111", "100", "111"],
    "3": ["111", "001", "111", "001", "111"],
    "4": ["101", "101", "111", "001", "001"],
    "5": ["111", "100", "111", "001", "111"],
    "6": ["111", "100", "111", "101", "111"],
    "7": ["111", "001", "010", "010", "010"],
    "8": ["111", "101", "111", "101", "111"],
    "9": ["111", "101", "111", "001", "111"],
    "%": ["101", "001", "010", "100", "101"],
    "W": ["101", "101", "101", "111", "101"],
    "-": ["000", "000", "111", "000", "000"],
}

# Семь сегментов: a верх, b верх-право, c низ-право, d низ, e низ-лево, f верх-лево, g середина
SEGMENTS = ["abcdef", "bc", "abdeg", "abcdg", "bcfg", "acdfg", "acdefg", "abc", "abcdefg", "abcdfg"]

WHITE = (235, 240, 255)
DIM = (22, 23, 28)
BLACK = (4, 4, 6)


def heat_color(k):
    """Нагрев 0..1: зелёный -> жёлтый -> красный."""
    k = max(0.0, min(1.0, k))
    if k < 0.5:
        return (int(60 + 390 * k), 220, 60)
    return (255, int(220 - 380 * (k - 0.5)), 40)


class Canvas:
    def __init__(self, w, h):
        self.w, self.h = w, h
        self.px = [[BLACK] * w for _ in range(h)]

    def set(self, x, y, c):
        if 0 <= x < self.w and 0 <= y < self.h:
            self.px[y][x] = c

    def rect(self, x, y, w, h, c):
        for yy in range(y, y + h):
            for xx in range(x, x + w):
                self.set(xx, yy, c)

    def text(self, x, y, s, c, scale=1):
        for ch in s:
            glyph = FONT.get(ch)
            if glyph:
                for gy, row in enumerate(glyph):
                    for gx, bit in enumerate(row):
                        if bit == "1":
                            self.rect(x + gx * scale, y + gy * scale, scale, scale, c)
            x += 4 * scale

    def seven(self, x, y, digit, c, w=10, h=16, t=2):
        """Большая цифра w x h, толщина сегмента t; неактивные сегменты не горят (так читается в игре)."""
        on = SEGMENTS[digit]
        mid = y + (h - t) // 2
        segs = {
            "a": (x + t, y, w - 2 * t, t),
            "d": (x + t, y + h - t, w - 2 * t, t),
            "g": (x + t, mid, w - 2 * t, t),
            "f": (x, y + t, t, mid - y - t),
            "b": (x + w - t, y + t, t, mid - y - t),
            "e": (x, mid + t, t, y + h - t - mid - t),
            "c": (x + w - t, mid + t, t, y + h - t - mid - t),
        }
        for name, (sx, sy, sw, sh) in segs.items():
            self.rect(sx, sy, sw, sh, c if name in on else BLACK)

    def image(self):
        img = Image.new("RGB", (self.w, self.h))
        img.putdata([p for row in self.px for p in row])
        return img


def battery_vertical(cv, x, y, charge, color):
    """Батарейка 4x7 стоя: контакт сверху, заливка снизу."""
    cv.rect(x + 1, y, 2, 1, color)
    cv.rect(x, y + 1, 4, 6, color)
    cv.rect(x + 1, y + 2, 2, 4, BLACK)
    fill = round(4 * charge)
    cv.rect(x + 1, y + 6 - fill, 2, fill, color)


def draw(style, charge=0.87, heat=0.0, puffs=42, inhaling=False):
    """charge 0..1, heat 0..1 (1 = перегрев), puffs — счётчик затяжек."""
    w, h = SIZES[style]
    cv = Canvas(w, h)
    pct = max(0, min(100, round(charge * 100)))
    low = (255, 70, 60) if pct <= 10 else WHITE

    if style == "Pixl":
        shown = min(99, pct)
        cv.seven(3, 3, shown // 10, low)
        cv.seven(15, 3, shown % 10, low)
        battery_vertical(cv, 27, 3, charge, low)
        # капля (жидкость) под батарейкой: светится красным при затяжке
        cv.rect(28, 13, 2, 3, (255, 60, 60) if inhaling else DIM)
        cv.rect(3, 21, 26, 2, DIM)
        cv.rect(3, 21, round(26 * heat), 2, heat_color(heat))
    elif style == "Box":
        # крупные проценты по центру, под ними полоса нагрева
        txt = f"{pct}%"
        cv.text((w - (len(txt) * 12 - 3)) // 2, 2, txt, low, 3)
        cv.rect(3, 20, w - 6, 2, DIM)
        cv.rect(3, 20, round((w - 6) * heat), 2, heat_color(heat))
    else:  # Pod
        # заряд крупно (главное на экране), под ним молния и мощность мелко
        txt = str(pct)
        cv.text(1 if len(txt) == 3 else 2, 2, txt, low, 2)
        for dx, dy in ((2, 0), (1, 1), (0, 2), (1, 2), (2, 2), (1, 3), (0, 4)):
            cv.set(2 + dx, 15 + dy, (255, 210, 60))
        watts = 20 + round(20 * heat)
        cv.text(7, 15, f"{watts}W", (150, 155, 170))
        # огонь: снизу жёлто-красный, высота растёт с нагревом
        top, bottom = 26, 57
        level = 0.25 + 0.75 * heat
        for y in range(top, bottom + 1):
            k = (bottom - y) / (bottom - top)  # 0 снизу, 1 сверху
            if k > level:
                continue
            f = k / max(level, 1e-3)
            c = (255, int(230 - 200 * f), int(60 - 60 * f))
            dark = max(0.0, (f - 0.7) / 0.3)
            c = tuple(int(v * (1 - 0.8 * dark)) for v in c)
            for x in range(1, w - 1):
                cv.set(x, y, c)
        cv.text(2, h - 6, f"{puffs % 100000:05d}", (150, 155, 170))
    return cv.image()


def save_defaults():
    paths = {}
    for style in SIZES:
        os.makedirs(tex_dir(style), exist_ok=True)
        path = os.path.join(tex_dir(style), f"Vape{style}Screen.png")
        draw(style).save(path)
        paths[style] = path
    return paths


if __name__ == "__main__":
    for style, path in save_defaults().items():
        print(path)

"""Генерирует ассеты игрушечного пистолета: low-poly модель (OBJ + MTL),
текстуру-палитру (PNG) и звук выстрела (WAV) в Assets/Items/ToyGun/.

Запуск из корня репозитория:  python3 Tools/generate_toy_gun.py [--preview out.png]
Нужны numpy и Pillow (для превью ещё matplotlib).

Оси: Y вверх, ствол смотрит в +Z, 1 единица = 1 метр. Начало координат в центре
рукояти, там, где её держит рука. Дуло в точке MUZZLE.
"""
import argparse
import math
import os
import wave

import numpy as np
from PIL import Image

OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "Assets", "Items", "ToyGun")
MODEL_NAME = "ToyGunModel"
TEXTURE_NAME = "ToyGunTexture"
SOUND_NAME = "ToyGunShot"

# ---- Текстура: атлас 4x4 ячейки по 16 px, каждая ячейка свой цвет ----

TEX_SIZE = 64
CELL = 16
PALETTE = {
    "green": (60, 190, 75),       # корпус
    "green_dark": (35, 130, 50),  # ствол, детали
    "orange": (255, 120, 20),     # оранжевый колпачок на дуле, как у настоящих игрушек
    "yellow": (250, 210, 40),     # рукоять, бункер с пульками
    "black": (40, 40, 45),        # спуск, прицел
    "white": (235, 235, 230),     # наклейка на боку
    "red": (220, 50, 45),         # полоска на бункере
}
CELLS = {name: (i % 4, i // 4) for i, name in enumerate(PALETTE)}


def make_texture():
    rng = np.random.default_rng(7)
    img = np.zeros((TEX_SIZE, TEX_SIZE, 3), dtype=np.float32)
    for name, (cx, cy) in CELLS.items():
        color = np.array(PALETTE[name], dtype=np.float32)
        x0, y0 = cx * CELL, cy * CELL
        # Лёгкий шум и затемнение к краю ячейки: пластик не выглядит плоской заливкой
        yy, xx = np.mgrid[0:CELL, 0:CELL]
        edge = np.minimum(np.minimum(xx, CELL - 1 - xx), np.minimum(yy, CELL - 1 - yy))
        shade = 0.88 + 0.12 * np.clip(edge / 4.0, 0, 1)
        noise = rng.normal(0, 4, (CELL, CELL, 1))
        img[y0:y0 + CELL, x0:x0 + CELL] = color * shade[..., None] + noise
    # Ячейка "white" это наклейка: белый фон с красной звёздочкой-кружком
    cx, cy = CELLS["white"]
    yy, xx = np.mgrid[0:CELL, 0:CELL]
    dot = (xx - 7.5) ** 2 + (yy - 7.5) ** 2 < 16
    img[cy * CELL:(cy + 1) * CELL, cx * CELL:(cx + 1) * CELL][dot] = PALETTE["red"]
    return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8), "RGB")


def cell_uv(name, u, v):
    """UV внутри ячейки (u, v в 0..1), с отступом от края, чтобы не ловить соседний цвет."""
    cx, cy = CELLS[name]
    pad = 2.0 / TEX_SIZE
    size = CELL / TEX_SIZE
    x = cx * size + pad + u * (size - 2 * pad)
    # В OBJ v=0 внизу картинки, а строки PNG идут сверху
    y = 1.0 - (cy * size + pad + (1 - v) * (size - 2 * pad))
    return (x, y)


# ---- Геометрия ----

class Mesh:
    def __init__(self):
        self.faces = []  # (список вершин, нормаль, список uv, имя цвета)

    def quad(self, pts, color, uvs=None):
        pts = [np.asarray(p, dtype=float) for p in pts]
        n = np.cross(pts[1] - pts[0], pts[2] - pts[0])
        n /= np.linalg.norm(n)
        uvs = uvs or [(0, 0), (1, 0), (1, 1), (0, 1)][:len(pts)]
        self.faces.append((pts, n, [cell_uv(color, *uv) for uv in uvs], color))

    def box(self, lo, hi, color, side_color=None, shear_z_per_y=0.0, shear_top=None):
        """Коробка lo..hi. shear_z_per_y наклоняет её вдоль Z (для рукояти) относительно высоты shear_top."""
        (x0, y0, z0), (x1, y1, z1) = lo, hi
        top = y1 if shear_top is None else shear_top

        def p(x, y, z):
            return (x, y, z + (y - top) * shear_z_per_y)

        c = [p(x0, y0, z0), p(x1, y0, z0), p(x1, y1, z0), p(x0, y1, z0),
             p(x0, y0, z1), p(x1, y0, z1), p(x1, y1, z1), p(x0, y1, z1)]
        side = side_color or color
        # Обход против часовой стрелки, если смотреть снаружи
        self.quad([c[0], c[3], c[2], c[1]], color)        # -Z
        self.quad([c[4], c[5], c[6], c[7]], color)        # +Z
        self.quad([c[0], c[4], c[7], c[3]], side)         # -X
        self.quad([c[1], c[2], c[6], c[5]], side)         # +X
        self.quad([c[3], c[7], c[6], c[2]], color)        # +Y
        self.quad([c[0], c[1], c[5], c[4]], color)        # -Y

    def cylinder_z(self, center_xy, r, z0, z1, color, sides=8, caps=True, axis="z"):
        """Цилиндр вдоль Z (или вдоль Y при axis='y')."""
        cx, cy = center_xy

        def p(a, h):
            ox, oy = r * math.cos(a), r * math.sin(a)
            if axis == "z":
                return (cx + ox, cy + oy, h)
            return (cx + ox, h, cy + oy)

        angles = [2 * math.pi * i / sides + math.pi / sides for i in range(sides)]
        for i in range(sides):
            a, b = angles[i], angles[(i + 1) % sides]
            if axis == "z":
                self.quad([p(a, z0), p(b, z0), p(b, z1), p(a, z1)], color)
            else:
                self.quad([p(a, z0), p(a, z1), p(b, z1), p(b, z0)], color)
        if caps:
            ring0 = [p(a, z0) for a in angles]
            ring1 = [p(a, z1) for a in angles]
            if axis == "z":
                self.quad(list(reversed(ring0)), color, [(0.5, 0.5)] * sides)
                self.quad(ring1, color, [(0.5, 0.5)] * sides)
            else:
                self.quad(ring0, color, [(0.5, 0.5)] * sides)
                self.quad(list(reversed(ring1)), color, [(0.5, 0.5)] * sides)


BARREL_Y = 0.055
MUZZLE = (0.0, BARREL_Y, 0.185)


def build():
    m = Mesh()
    # Корпус (затвор) с белой наклейкой по бокам
    m.box((-0.016, 0.035, -0.035), (0.016, 0.075, 0.105), "green", side_color="green")
    m.box((-0.0165, 0.045, 0.0), (0.0165, 0.065, 0.04), "white")
    # Ствол и оранжевый колпачок
    m.cylinder_z((0.0, BARREL_Y), 0.011, 0.105, 0.165, "green_dark")
    m.cylinder_z((0.0, BARREL_Y), 0.0145, 0.165, MUZZLE[2], "orange")
    # Прицелы
    m.box((-0.003, 0.075, 0.09), (0.003, 0.085, 0.1), "black")
    m.box((-0.008, 0.075, -0.03), (-0.003, 0.084, -0.02), "black")
    m.box((0.003, 0.075, -0.03), (0.008, 0.084, -0.02), "black")
    # Бункер с пульками сверху (как у пневматических игрушек)
    m.cylinder_z((0.0, 0.035), 0.017, 0.075, 0.105, "yellow", sides=10, axis="y")
    m.cylinder_z((0.0, 0.035), 0.0175, 0.085, 0.092, "red", sides=10, caps=False, axis="y")
    # Рукоять, наклонена назад
    m.box((-0.014, -0.06, -0.03), (0.014, 0.035, 0.005), "yellow", shear_z_per_y=0.35)
    m.box((-0.015, -0.068, -0.036), (0.015, -0.06, 0.0), "green_dark", shear_z_per_y=0.35, shear_top=0.035)
    # Спусковая скоба и крючок
    m.box((-0.004, 0.0, 0.005), (0.004, 0.006, 0.06), "green_dark")
    m.box((-0.004, 0.006, 0.054), (0.004, 0.035, 0.06), "green_dark")
    m.box((-0.003, 0.012, 0.02), (0.003, 0.035, 0.028), "black")
    # Начало координат в центре рукояти
    grip = np.array([0.0, -0.012, -0.0])
    for f in m.faces:
        f[0][:] = [p - grip for p in f[0]]
    muzzle = tuple(np.array(MUZZLE) - grip)
    return m, muzzle


def write_obj(mesh, path):
    lines = [f"# Сгенерировано Tools/generate_toy_gun.py, не править руками",
             f"mtllib {MODEL_NAME}.mtl", f"o {MODEL_NAME}", "usemtl ToyGunPlastic"]
    v_idx = vt_idx = vn_idx = 0
    body = []
    for pts, n, uvs, _ in mesh.faces:
        ids = []
        lines.append(f"vn {n[0]:.5f} {n[1]:.5f} {n[2]:.5f}")
        vn_idx += 1
        for p, uv in zip(pts, uvs):
            lines.append(f"v {p[0]:.5f} {p[1]:.5f} {p[2]:.5f}")
            lines.append(f"vt {uv[0]:.5f} {uv[1]:.5f}")
            v_idx += 1
            vt_idx += 1
            ids.append(f"{v_idx}/{vt_idx}/{vn_idx}")
        body.append("f " + " ".join(ids))
    with open(path, "w", newline="\n") as fh:
        fh.write("\n".join(lines + body) + "\n")


def write_mtl(path):
    with open(path, "w", newline="\n") as fh:
        fh.write("newmtl ToyGunPlastic\nKd 1.000 1.000 1.000\nKs 0.200 0.200 0.200\n"
                 f"Ns 40\nmap_Kd {TEXTURE_NAME}.png\n")


def write_shot_sound(path, rate=44100):
    """Короткий пластиковый "пук": щелчок пружины + тональный хлопок с падающей частотой."""
    rng = np.random.default_rng(3)
    t = np.arange(int(rate * 0.18)) / rate
    click = rng.normal(0, 1, t.size) * np.exp(-t * 400)
    freq = 900 * np.exp(-t * 18) + 250
    phase = 2 * np.pi * np.cumsum(freq) / rate
    pop = np.sin(phase) * np.exp(-t * 35)
    rattle = rng.normal(0, 1, t.size) * np.exp(-((t - 0.04) ** 2) / 0.0002) * 0.25
    signal = 0.55 * click + 0.7 * pop + rattle
    signal /= np.max(np.abs(signal))
    data = (signal * 0.8 * 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(data.tobytes())


def render_preview(mesh, path):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from mpl_toolkits.mplot3d.art3d import Poly3DCollection

    light = np.array([0.4, 0.8, 0.5])
    light /= np.linalg.norm(light)
    fig = plt.figure(figsize=(10, 5))
    for i, (elev, azim) in enumerate([(15, -60), (10, 200)]):
        ax = fig.add_subplot(1, 2, i + 1, projection="3d")
        polys, colors = [], []
        for pts, n, _, color in mesh.faces:
            # matplotlib: Z вверх, поэтому меняем местами Y и Z
            polys.append([(p[0], p[2], p[1]) for p in pts])
            k = 0.45 + 0.55 * max(0.0, float(np.dot(n, light)))
            colors.append(tuple(min(1.0, c / 255 * k) for c in PALETTE[color]))
        ax.add_collection3d(Poly3DCollection(polys, facecolors=colors, edgecolors=(0, 0, 0, 0.15),
                                             linewidths=0.3))
        ax.set_xlim(-0.11, 0.11)
        ax.set_ylim(-0.06, 0.16)
        ax.set_zlim(-0.08, 0.14)
        ax.set_box_aspect((1, 1, 1))
        ax.view_init(elev=elev, azim=azim)
        ax.axis("off")
    plt.tight_layout()
    plt.savefig(path, dpi=110)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--preview", help="сохранить картинку-превью модели")
    args = parser.parse_args()

    os.makedirs(OUT_DIR, exist_ok=True)
    mesh, muzzle = build()
    write_obj(mesh, os.path.join(OUT_DIR, MODEL_NAME + ".obj"))
    write_mtl(os.path.join(OUT_DIR, MODEL_NAME + ".mtl"))
    make_texture().save(os.path.join(OUT_DIR, TEXTURE_NAME + ".png"))
    write_shot_sound(os.path.join(OUT_DIR, SOUND_NAME + ".wav"))
    tris = sum(len(f[0]) - 2 for f in mesh.faces)
    print(f"Модель: {len(mesh.faces)} граней, {tris} треугольников; дуло в {tuple(round(float(c), 4) for c in muzzle)}")
    if args.preview:
        render_preview(mesh, args.preview)


if __name__ == "__main__":
    main()

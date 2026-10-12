"""
Генератор банки "Adrenaline Game Fuel" (синяя Energy Power): текстура и низкополигональная модель (FBX).
Иконку инвентаря рисует Unity (Lethal Company → Render Item Icons) в Textures/Adrenaline/AdrenalineIcon.png.

Запуск (обычный Python 3.13, Blender как модуль):
    pip install bpy pillow numpy
    python Tools/Adrenaline/model.py

Пишет в Assets/Sources/Art/:
    Textures/Adrenaline/AdrenalineCan.png       текстура 512x512 (этикетка + металл)
    Models/Adrenaline/AdrenalineCan.fbx         модель: Can (целая, центр в середине) и Crushed (пустая, смята с боков)
"""
import math
import os
import random

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MODEL_DIR = os.path.join(ROOT, "Assets", "Sources", "Art", "Models", "Adrenaline")
TEX_DIR = os.path.join(ROOT, "Assets", "Sources", "Art", "Textures", "Adrenaline")
TEX = os.path.join(TEX_DIR, "AdrenalineCan.png")
FBX = os.path.join(MODEL_DIR, "AdrenalineCan.fbx")

SIZE = 512
LABEL_V0 = 0.25  # этикетка занимает v 0.25..1, ниже служебная полоса металла

random.seed(1337)
np.random.seed(1337)


def font(size, names=("InterDisplay-BlackItalic.otf", "Inter-BlackItalic.otf",
                      "DejaVuSans-BoldOblique.ttf", "DejaVuSans-Bold.ttf", "LiberationSans-Bold.ttf")):
    for root in ("/usr/share/fonts", "C:/Windows/Fonts", "/Library/Fonts", "/System/Library/Fonts"):
        for dirpath, _, files in os.walk(root):
            for n in names:
                if n in files:
                    return ImageFont.truetype(os.path.join(dirpath, n), size)
    return ImageFont.load_default(size)


# ---------------------------------------------------------------- текстура

def make_texture():
    """Синяя банка по мотивам Adrenaline Energy Power: логотип, серебряная «А», красная плашка."""
    img = Image.new("RGB", (SIZE, SIZE), (0, 0, 0))
    lh = int(SIZE * (1 - LABEL_V0))  # высота этикетки в атласе
    # Рисуем в пропорциях развёртки (окружность 20.7 см x высота 13.4 см), потом растягиваем до lh
    W, H = SIZE, 332
    label = Image.new("RGB", (W, H))
    px = label.load()
    for y in range(H):
        for x in range(W):
            # синий градиент + мягкие вертикальные блики, как у металлизированной банки
            k = 0.5 + 0.5 * math.cos((x / W) * 2 * math.pi * 2)
            px[x, y] = (int(18 + 20 * k), int(52 + 40 * k), int(170 + 50 * k))
    d = ImageDraw.Draw(label)

    FRONT = 150  # центр лицевой стороны по горизонтали

    # серебряные диагональные полосы справа от «А» (только в клине, чтобы не мешать тексту)
    stripes = Image.new("RGB", (W, H), (205, 212, 228))
    mask = Image.new("L", (W, H), 0)
    md = ImageDraw.Draw(mask)
    for x in range(-200, W, 9):
        md.line([(x, H), (x + 120, 0)], fill=255, width=2)
    clip = Image.new("L", (W, H), 0)
    ImageDraw.Draw(clip).polygon([(FRONT - 10, H), (FRONT + 125, H), (FRONT + 125, 0), (FRONT + 110, 0)], fill=255)
    label.paste(stripes, (0, 0), Image.composite(mask, Image.new("L", (W, H), 0), clip))

    # верхний и нижний серебряный пояс
    d.rectangle([0, 0, W, 22], fill=(200, 204, 214))
    d.rectangle([0, H - 10, W, H], fill=(200, 204, 214))
    f_orig = font(15)
    for x in (FRONT - 40, FRONT + 210):
        d.text((x, 3), "ORIGINAL", font=f_orig, fill=(220, 30, 40))
    d.text((FRONT - 58, 25), "АБСОЛЮТНАЯ ЭНЕРГИЯ", font=font(11, ("Inter-Black.otf", "DejaVuSans-Bold.ttf")),
           fill=(255, 255, 255))

    # логотип Adrenaline
    f_logo = font(46)
    txt = "Adrenaline"
    bb = d.textbbox((0, 0), txt, font=f_logo)
    tw = bb[2] - bb[0]
    d.text((FRONT - tw // 2 - bb[0], 42 - bb[1]), txt, font=f_logo, fill=(255, 255, 255),
           stroke_width=3, stroke_fill=(10, 25, 90))
    # ENERGY (красным) + POWER (белым)
    f_ep = font(24)
    w1 = d.textlength("ENERGY", font=f_ep)
    w2 = d.textlength("POWER", font=f_ep)
    x0 = FRONT - (w1 + w2) / 2 + 20
    d.text((x0, 94), "ENERGY", font=f_ep, fill=(235, 30, 45), stroke_width=1, stroke_fill=(255, 255, 255))
    d.text((x0 + w1, 94), "POWER", font=f_ep, fill=(255, 255, 255), stroke_width=1, stroke_fill=(10, 25, 90))

    # большая серебряная «А» с красно-белыми росчерками
    f_a = font(190)
    a_img = Image.new("RGBA", (220, 220), (0, 0, 0, 0))
    ad = ImageDraw.Draw(a_img)
    bb = ad.textbbox((0, 0), "A", font=f_a)
    ad.text((110 - (bb[0] + bb[2]) // 2, 110 - (bb[1] + bb[3]) // 2), "A", font=f_a, fill=(255, 255, 255),
            stroke_width=5, stroke_fill=(235, 30, 45))
    # металлический градиент внутри буквы
    grad = Image.new("RGBA", a_img.size)
    gp = grad.load()
    for y in range(a_img.height):
        for x in range(a_img.width):
            v = int(170 + 70 * math.sin((x + y) * 0.05))
            gp[x, y] = (v, v + 4, min(255, v + 15), 255)
    alpha = a_img.split()[3]
    red_edge = Image.eval(a_img.convert("L"), lambda p: 255 if p < 140 else 0)
    metal = Image.composite(Image.new("RGBA", a_img.size, (235, 30, 45, 255)), grad, red_edge)
    metal.putalpha(alpha)
    label.paste(metal, (FRONT - 120, 112), metal)
    # росчерки
    d.arc([FRONT - 120, 120, FRONT + 30, 330], 200, 280, fill=(255, 255, 255), width=4)
    d.arc([FRONT - 112, 128, FRONT + 38, 338], 200, 270, fill=(235, 30, 45), width=3)

    # объём
    d.text((FRONT + 18, 228), "0,449", font=font(40), fill=(255, 255, 255), stroke_width=2,
           stroke_fill=(10, 25, 90))

    # красная плашка «ВСЕ ТЫ МОЖЕШЬ»
    d.rectangle([FRONT - 100, 284, FRONT + 110, 308], fill=(225, 30, 40))
    f_b = font(17, ("Inter-Black.otf", "DejaVuSans-Bold.ttf"))
    bw = d.textlength("ВСЕ ТЫ МОЖЕШЬ", font=f_b)
    d.text((FRONT + 5 - bw / 2, 286), "ВСЕ ТЫ МОЖЕШЬ", font=f_b, fill=(255, 255, 255))

    # оборот: маленький логотип и мелкий текст состава
    BACK = FRONT + W // 2
    d.text((BACK - 80, 30), "Adrenaline", font=font(28), fill=(255, 255, 255), stroke_width=2,
           stroke_fill=(10, 25, 90))
    f_small = font(8, ("Inter-Bold.otf", "DejaVuSans-Bold.ttf", "LiberationSans-Bold.ttf"))
    lines = ["НАПИТОК БЕЗАЛКОГОЛЬНЫЙ ТОНИЗИРУЮЩИЙ", "ГАЗИРОВАННЫЙ «ADRENALINE GAME FUEL»",
             "СОСТАВ: ВОДА, САХАР, РЕГУЛЯТОРЫ", "КИСЛОТНОСТИ, КОФЕИН, ТАУРИН,",
             "АРОМАТИЗАТОРЫ, ВИТАМИНЫ B3, B6, B12.", "НЕ РЕКОМЕНДУЕТСЯ ДЕТЯМ И ТЕМ,",
             "КТО ХОЧЕТ ДОЖИТЬ ДО КВОТЫ.", "ИЗГОТОВИТЕЛЬ: THE COMPANY",
             "ХРАНИТЬ ВДАЛИ ОТ ЛЕСТНИЦЫ И ДЖЕСТЕРА.", "ОБЪЁМ 0,449 Л"]
    for i, line in enumerate(lines):
        d.text((BACK - 86, 74 + i * 13), line, font=f_small, fill=(235, 238, 250))
    d.rectangle([BACK - 82, 220, BACK - 20, 250], fill=(240, 240, 240))
    for i in range(14):
        d.line([(BACK - 78 + i * 4, 223), (BACK - 78 + i * 4, 247)], fill=(20, 20, 20), width=1 + i % 2)
    # «ENERGYPOWER» вертикально вдоль шва
    side = Image.new("RGBA", (300, 34), (0, 0, 0, 0))
    sd = ImageDraw.Draw(side)
    sd.text((4, 0), "ENERGY", font=font(28), fill=(235, 30, 45))
    sd.text((4 + sd.textlength("ENERGY", font=font(28)), 0), "POWER", font=font(28), fill=(255, 255, 255))
    side = side.rotate(-90, expand=True)
    label.paste(side, (FRONT + 130, 26), side)

    img.paste(label.resize((SIZE, lh), Image.BICUBIC), (0, 0))

    # --- служебная полоса металла (v 0..0.25 = нижние 128 px картинки)
    strip_y = lh
    sd = ImageDraw.Draw(img)
    # u 0..0.25: крышка, u 0.25..0.5: дно, u 0.5..1: металл для обода и горлышка
    for x in range(SIZE // 2, SIZE):
        g = 175 + int(40 * math.sin(x * 0.09))
        sd.line([(x, strip_y), (x, SIZE)], fill=(g, g + 2, g + 8))
    c = SIZE // 8
    for cx, base in ((c, 185), (3 * c, 150)):
        cy = strip_y + c
        sd.ellipse([cx - c + 2, cy - c + 2, cx + c - 2, cy + c - 2], fill=(base, base + 2, base + 8))
        sd.ellipse([cx - c + 12, cy - c + 12, cx + c - 12, cy + c - 12], outline=(base - 45,) * 3, width=3)
    cx, cy = c, strip_y + c
    sd.rounded_rectangle([cx - 12, cy - 34, cx + 12, cy - 8], radius=8, fill=(40, 40, 40))
    sd.rounded_rectangle([cx - 10, cy - 6, cx + 10, cy + 26], radius=7, outline=(120, 120, 128), width=3)
    sd.text((3 * c - 26, strip_y + c - 8), "LOT 13", font=font(13, ("DejaVuSans-Bold.ttf",)), fill=(90, 90, 95))

    # --- лёгкая грязь и потёртости в духе игры
    a = np.asarray(img).astype(np.float32)
    noise = np.random.normal(0, 5, a.shape[:2])[..., None]
    blotch = Image.fromarray((np.random.rand(SIZE // 16, SIZE // 16) * 255).astype(np.uint8))
    blotch = np.asarray(blotch.resize((SIZE, SIZE), Image.BICUBIC).filter(ImageFilter.GaussianBlur(6)),
                        dtype=np.float32)[..., None] / 255.0
    a = a * (0.85 + 0.15 * blotch) + noise
    scratches = Image.new("L", (SIZE, SIZE), 0)
    sc = ImageDraw.Draw(scratches)
    for _ in range(45):
        x, y = random.randrange(SIZE), random.randrange(lh)
        ang = random.uniform(-0.6, 0.6)
        ln = random.randint(6, 26)
        sc.line([(x, y), (x + ln * math.cos(ang), y + ln * math.sin(ang))], fill=random.randint(50, 120), width=1)
    s = np.asarray(scratches, dtype=np.float32)[..., None] / 255.0
    a = a * (1 - s) + 200 * s
    Image.fromarray(np.clip(a, 0, 255).astype(np.uint8)).save(TEX)


# ---------------------------------------------------------------- модель

SEG = 16
# профиль банки снизу вверх: (z, радиус, область UV)
PROFILE = [
    (0.000, 0.026, "metal"),
    (0.007, 0.0330, "metal"),
    (0.012, 0.0330, "label"),
    (0.146, 0.0330, "label"),
    (0.151, 0.0330, "metal"),
    (0.163, 0.0285, "metal"),
    (0.168, 0.0275, "metal"),
]
LID_Z = 0.165   # крышка утоплена ниже обода
BOTTOM_Z = 0.005  # вогнутое дно
HEIGHT = 0.168


def build_mesh(name, profile=PROFILE, seg=SEG):
    """Сетка банки по профилю; seg точек по кругу."""
    import bpy
    import bmesh

    mesh = bpy.data.meshes.new(name)

    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")

    rings = []
    for z, r, _ in profile:
        rings.append([bm.verts.new((r * math.cos(2 * math.pi * i / seg),
                                    r * math.sin(2 * math.pi * i / seg), z)) for i in range(seg)])

    z_l0, z_l1 = PROFILE[2][0], PROFILE[3][0]  # этикетка от первого до последнего кольца

    # боковые стенки
    for k in range(len(profile) - 1):
        label = profile[k][2] == "label" and profile[k + 1][2] == "label"
        for i in range(seg):
            i2 = (i + 1) % seg
            f = bm.faces.new((rings[k][i], rings[k][i2], rings[k + 1][i2], rings[k + 1][i]))
            for loop, (kk, jj) in zip(f.loops, ((k, i), (k, i + 1), (k + 1, i + 1), (k + 1, i))):
                z = profile[kk][0]
                if label:
                    u = jj / seg
                    v = LABEL_V0 + (1 - LABEL_V0) * (z - z_l0) / (z_l1 - z_l0)
                else:
                    u = 0.5 + 0.5 * jj / seg
                    v = 0.02 + 0.2 * z / HEIGHT
                loop[uv].uv = (u, v)

    def cap(ring, z, center_uv, top):
        ring_r = ring[0].co.xy.length
        c = bm.verts.new((0, 0, z))
        for i in range(seg):
            i2 = (i + 1) % seg
            verts = (c, ring[i], ring[i2]) if top else (c, ring[i2], ring[i])
            f = bm.faces.new(verts)
            for loop in f.loops:
                x, y, _ = loop.vert.co
                k = 0.118 / ring_r  # круг радиусом 0.118 в своей клетке атласа
                loop[uv].uv = (center_uv[0] + x * k * (1 if top else -1), center_uv[1] + y * k)

    # крышка: внутренний бортик обода вниз к утопленной крышке
    top = rings[-1]
    lid_ring = [bm.verts.new((0.025 * math.cos(2 * math.pi * i / seg),
                              0.025 * math.sin(2 * math.pi * i / seg), LID_Z)) for i in range(seg)]
    for i in range(seg):
        i2 = (i + 1) % seg
        f = bm.faces.new((top[i], top[i2], lid_ring[i2], lid_ring[i]))
        for loop, jj, vv in zip(f.loops, (i, i + 1, i + 1, i), (0.23, 0.23, 0.20, 0.20)):
            loop[uv].uv = (0.5 + 0.5 * jj / seg, vv)
    cap(lid_ring, LID_Z, (0.125, 0.125), True)

    # дно: вогнутое
    cap(rings[0], BOTTOM_Z, (0.375, 0.125), False)

    # язычок: плоская пластинка на крышке
    tab = bmesh.ops.create_cube(bm, size=1.0)["verts"]
    for v in tab:
        v.co.x *= 0.012
        v.co.y *= 0.022
        v.co.z *= 0.0015
        v.co.y -= 0.004
        v.co.z += LID_Z + 0.001
    for f in {f for v in tab for f in v.link_faces}:
        for loop in f.loops:
            loop[uv].uv = (0.75, 0.1)

    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    # центр модели в середине банки
    for v in bm.verts:
        v.co.z -= HEIGHT / 2
    bm.to_mesh(mesh)
    bm.free()

    return mesh


def make_model():
    import bpy

    bpy.ops.wm.read_factory_settings(use_empty=True)
    mesh = build_mesh("AdrenalineCan")
    obj = bpy.data.objects.new("Can", mesh)
    bpy.context.scene.collection.objects.link(obj)

    # Плоские грани у торцов, сглаженный бок
    mesh.shade_smooth()
    try:
        mod = obj.modifiers.new("Smooth", "SMOOTH_BY_ANGLE")
    except Exception:
        mod = None
    if mod is None:
        try:
            bpy.context.view_layer.objects.active = obj
            bpy.ops.object.shade_smooth_by_angle(angle=math.radians(40))
        except Exception:
            pass

    mat = bpy.data.materials.new("AdrenalineCan")
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
    tex.image = bpy.data.images.load(TEX)
    mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Metallic"].default_value = 0.6
    bsdf.inputs["Roughness"].default_value = 0.4
    mesh.materials.append(mat)

    crushed = make_crushed(obj)

    # Два объекта в одном FBX: целая банка и мятая пустая, AdrenalineItem показывает нужную
    bpy.ops.object.select_all(action="DESELECT")
    for o in (obj, crushed):
        o.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.fbx(
        filepath=FBX,
        use_selection=True,
        object_types={"MESH"},
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z",
        axis_up="Y",
        bake_space_transform=True,
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        path_mode="STRIP",
        embed_textures=False,
        add_leaf_bones=False,
        bake_anim=False,
    )
    tris = sum(len(p.vertices) - 2 for p in mesh.polygons)
    print(f"FBX: {FBX} ({tris} треугольников на банку)")
    crushed.hide_render = True  # на иконке только целая банка
    return obj


def make_crushed(can):
    """Мятая пустая банка: та же сетка и развёртка, сжатая с боков в кулаке. Высота почти та же,
    середина сплющена с двух сторон внутрь до узкой талии с заломами, дно и крышка целые и чуть
    повёрнуты друг к другу."""
    import bpy

    rng = random.Random(449)
    phases = [rng.uniform(0, 2 * math.pi) for _ in range(3)]
    # Та же банка, но с частыми кольцами на этикетке и вдвое большим числом точек по кругу:
    # у целой бок из одного ряда граней, а тут ему нужно гнуться
    z0, z1 = PROFILE[2][0], PROFILE[3][0]
    rings = 14
    profile = PROFILE[:2] + [(z0 + (z1 - z0) * j / rings, PROFILE[2][1], "label") for j in range(rings + 1)] \
        + PROFILE[4:]
    mesh = build_mesh("Crushed", profile, SEG * 2)
    mesh.materials.append(can.data.materials[0])
    half = HEIGHT / 2
    for v in mesh.vertices:
        x, y, z = v.co
        t = (z + half) / HEIGHT                     # 0 у дна, 1 у крышки
        a = math.atan2(y, x)
        # сила сжатия: максимум чуть выше середины, у торцов (жёсткие ободки) почти ноль
        m = max(0.0, math.sin(math.pi * min(max((t - 0.06) / 0.86, 0.0), 1.0))) ** 1.4
        m *= 1 + 0.25 * math.sin(2 * math.pi * t + 0.6)
        # сплющивание по X: стенки вдавлены внутрь до узкой щели, по Y немного раздаются
        x2 = x * (1 - 0.78 * m)
        y2 = y * (1 + 0.16 * m)
        # заломы металла: складки поперёк вмятины и мелкие вмятины по кругу
        fold = 0.0035 * m * math.sin(9 * math.pi * t + phases[0]) * math.copysign(1, x)
        dent = 1 - 0.10 * m * max(0.0, math.sin(4 * a + phases[1] + 4 * t))
        x2 = (x2 + fold) * dent
        y2 *= dent
        # пальцы вдавили сильнее одну сторону: лёгкий изгиб, верх повёрнут относительно дна
        x2 += 0.004 * m
        twist = math.radians(8) * t
        c, s_ = math.cos(twist), math.sin(twist)
        x2, y2 = x2 * c - y2 * s_, x2 * s_ + y2 * c
        nz = z - 0.10 * HEIGHT * m * 0.5 - 0.04 * HEIGHT * (t - 0.5)
        v.co = (x2, y2, nz)
    for poly in mesh.polygons:
        poly.use_smooth = False  # грани видны: смятый металл
    obj = bpy.data.objects.new("Crushed", mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


if __name__ == "__main__":
    os.makedirs(MODEL_DIR, exist_ok=True)
    os.makedirs(TEX_DIR, exist_ok=True)
    make_texture()
    print(f"Текстура: {TEX}")
    make_model()

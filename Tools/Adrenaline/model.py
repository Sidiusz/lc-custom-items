"""
Генератор банки "Adrenaline Game Fuel": текстура, низкополигональная модель (FBX) и иконка.

Запуск (обычный Python 3.13, Blender как модуль):
    pip install bpy pillow numpy
    python Tools/Adrenaline/model.py

Пишет в Assets/Adrenaline/:
    AdrenalineCan.png       текстура 512x512 (этикетка + металл)
    AdrenalineCan.fbx       модель, ~170 треугольников, центр в середине банки
    AdrenalineCanIcon.png   иконка для инвентаря 256x256
"""
import math
import os
import random

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "Assets", "Adrenaline")
TEX = os.path.join(OUT, "AdrenalineCan.png")
FBX = os.path.join(OUT, "AdrenalineCan.fbx")
ICON = os.path.join(OUT, "AdrenalineCanIcon.png")

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
    img = Image.new("RGB", (SIZE, SIZE), (0, 0, 0))
    lh = int(SIZE * (1 - LABEL_V0))  # высота этикетки в пикселях (сверху картинки)

    # --- этикетка: чёрная банка, оранжево-красный разрез, вертикальный логотип
    label = Image.new("RGB", (SIZE, lh), (14, 14, 16))
    d = ImageDraw.Draw(label)

    # тонкие диагональные полосы фона
    for x in range(-lh, SIZE, 14):
        d.line([(x, lh), (x + lh, 0)], fill=(24, 24, 28), width=3)

    # огненный клин по диагонали (уходит за шов, поэтому рисуем со сдвигом дважды)
    for shift in (0, -SIZE):
        pts = [(shift + 250, lh), (shift + 330, lh), (shift + 470, 0), (shift + 410, 0)]
        d.polygon(pts, fill=(255, 92, 0))
        pts = [(shift + 290, lh), (shift + 318, lh), (shift + 458, 0), (shift + 436, 0)]
        d.polygon(pts, fill=(255, 196, 30))
        pts = [(shift + 200, lh), (shift + 222, lh), (shift + 362, 0), (shift + 342, 0)]
        d.polygon(pts, fill=(200, 24, 24))

    # вертикальный логотип ADRENALINE (читается снизу вверх)
    f_logo = font(78)
    txt = "ADRENALINE"
    bbox = d.textbbox((0, 0), txt, font=f_logo)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    logo = Image.new("RGBA", (tw + 24, th + 24), (0, 0, 0, 0))
    ld = ImageDraw.Draw(logo)
    ld.text((12 - bbox[0], 12 - bbox[1]), txt, font=f_logo, fill=(255, 255, 255),
            stroke_width=4, stroke_fill=(255, 92, 0))
    logo = logo.rotate(90, expand=True)
    logo = logo.resize((logo.width, min(logo.height, lh - 30)))
    label.paste(logo, (70, (lh - logo.height) // 2), logo)

    # GAME FUEL рядом с логотипом
    f_sub = font(30)
    sub = Image.new("RGBA", (220, 44), (0, 0, 0, 0))
    ImageDraw.Draw(sub).text((4, 2), "GAME FUEL", font=f_sub, fill=(255, 140, 0))
    sub = sub.rotate(90, expand=True)
    label.paste(sub, (70 + logo.width + 2, (lh - sub.height) // 2 + 40), sub)

    # обратная сторона: состав мелким текстом и объём
    f_small = font(13, ("DejaVuSans-Bold.ttf", "LiberationSans-Bold.ttf"))
    for i, line in enumerate(["ENERGY DRINK", "CAFFEINE 32mg/100ml", "TAURINE  B-VITAMINS",
                              "NOT FOR CHILDREN", "PROPERTY OF", "THE COMPANY"]):
        d.text((340, 30 + i * 18), line, font=f_small, fill=(190, 190, 190))
    d.text((360, lh - 48), "0.449 L", font=font(26), fill=(255, 255, 255))

    # кромки этикетки
    d.rectangle([0, 0, SIZE, 5], fill=(150, 150, 155))
    d.rectangle([0, lh - 6, SIZE, lh], fill=(150, 150, 155))

    img.paste(label, (0, 0))

    # --- служебная полоса металла (v 0..0.25 = нижние 128 px картинки)
    strip_y = lh
    sd = ImageDraw.Draw(img)
    # u 0..0.25: крышка, u 0.25..0.5: дно, u 0.5..1: металл для обода и горлышка
    for x in range(SIZE // 2, SIZE):
        g = 150 + int(50 * math.sin(x * 0.09))
        sd.line([(x, strip_y), (x, SIZE)], fill=(g, g, g + 6))
    c = SIZE // 8
    for cx, base in ((c, 175), (3 * c, 140)):
        cy = strip_y + c
        sd.ellipse([cx - c + 2, cy - c + 2, cx + c - 2, cy + c - 2], fill=(base, base, base + 5))
        sd.ellipse([cx - c + 12, cy - c + 12, cx + c - 12, cy + c - 12], outline=(base - 45,) * 3, width=3)
    # отверстие и язычок на крышке
    cx, cy = c, strip_y + c
    sd.rounded_rectangle([cx - 12, cy - 34, cx + 12, cy - 8], radius=8, fill=(40, 40, 40))
    sd.rounded_rectangle([cx - 10, cy - 6, cx + 10, cy + 26], radius=7, outline=(110, 110, 115), width=3)
    sd.text((3 * c - 26, strip_y + c - 8), "LOT 13", font=f_small, fill=(90, 90, 95))

    # --- грязь и потёртости в духе игры
    a = np.asarray(img).astype(np.float32)
    noise = np.random.normal(0, 7, a.shape[:2])[..., None]
    blotch = Image.fromarray((np.random.rand(SIZE // 16, SIZE // 16) * 255).astype(np.uint8))
    blotch = np.asarray(blotch.resize((SIZE, SIZE), Image.BICUBIC).filter(ImageFilter.GaussianBlur(6)),
                        dtype=np.float32)[..., None] / 255.0
    a = a * (0.78 + 0.22 * blotch) + noise
    scratches = Image.new("L", (SIZE, SIZE), 0)
    sc = ImageDraw.Draw(scratches)
    for _ in range(70):
        x, y = random.randrange(SIZE), random.randrange(lh)
        ang = random.uniform(-0.6, 0.6)
        ln = random.randint(6, 30)
        sc.line([(x, y), (x + ln * math.cos(ang), y + ln * math.sin(ang))], fill=random.randint(60, 140), width=1)
    s = np.asarray(scratches, dtype=np.float32)[..., None] / 255.0
    a = a * (1 - s) + 175 * s
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


def make_model():
    import bpy
    import bmesh

    bpy.ops.wm.read_factory_settings(use_empty=True)
    mesh = bpy.data.meshes.new("AdrenalineCan")
    obj = bpy.data.objects.new("AdrenalineCan", mesh)
    bpy.context.scene.collection.objects.link(obj)

    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")

    rings = []
    for z, r, _ in PROFILE:
        rings.append([bm.verts.new((r * math.cos(2 * math.pi * i / SEG),
                                    r * math.sin(2 * math.pi * i / SEG), z)) for i in range(SEG)])

    z_l0, z_l1 = PROFILE[2][0], PROFILE[3][0]

    # боковые стенки
    for k in range(len(PROFILE) - 1):
        label = PROFILE[k][2] == "label" and PROFILE[k + 1][2] == "label"
        for i in range(SEG):
            i2 = (i + 1) % SEG
            f = bm.faces.new((rings[k][i], rings[k][i2], rings[k + 1][i2], rings[k + 1][i]))
            for loop, (kk, jj) in zip(f.loops, ((k, i), (k, i + 1), (k + 1, i + 1), (k + 1, i))):
                z = PROFILE[kk][0]
                if label:
                    u = jj / SEG
                    v = LABEL_V0 + (1 - LABEL_V0) * (z - z_l0) / (z_l1 - z_l0)
                else:
                    u = 0.5 + 0.5 * jj / SEG
                    v = 0.02 + 0.2 * z / HEIGHT
                loop[uv].uv = (u, v)

    def cap(ring, z, center_uv, top):
        ring_r = ring[0].co.xy.length
        c = bm.verts.new((0, 0, z))
        for i in range(SEG):
            i2 = (i + 1) % SEG
            verts = (c, ring[i], ring[i2]) if top else (c, ring[i2], ring[i])
            f = bm.faces.new(verts)
            for loop in f.loops:
                x, y, _ = loop.vert.co
                k = 0.118 / ring_r  # круг радиусом 0.118 в своей клетке атласа
                loop[uv].uv = (center_uv[0] + x * k * (1 if top else -1), center_uv[1] + y * k)

    # крышка: внутренний бортик обода вниз к утопленной крышке
    top = rings[-1]
    lid_ring = [bm.verts.new((0.025 * math.cos(2 * math.pi * i / SEG),
                              0.025 * math.sin(2 * math.pi * i / SEG), LID_Z)) for i in range(SEG)]
    for i in range(SEG):
        i2 = (i + 1) % SEG
        f = bm.faces.new((top[i], top[i2], lid_ring[i2], lid_ring[i]))
        for loop, jj, vv in zip(f.loops, (i, i + 1, i + 1, i), (0.23, 0.23, 0.20, 0.20)):
            loop[uv].uv = (0.5 + 0.5 * jj / SEG, vv)
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

    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
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
    print(f"FBX: {FBX} ({tris} треугольников)")
    return obj


def render_icon(obj):
    """Белый силуэт банки на прозрачном фоне, как у ванильных иконок."""
    import bpy

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "SINGLE"
    scene.display.shading.single_color = (0.92, 0.92, 0.92)
    scene.display.shading.show_object_outline = True
    scene.display.shading.object_outline_color = (0.35, 0.35, 0.35)
    scene.render.film_transparent = True
    scene.render.resolution_x = scene.render.resolution_y = 256
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.view_settings.view_transform = "Standard"

    obj.rotation_euler = (0, math.radians(-20), math.radians(30))
    cam_data = bpy.data.cameras.new("Cam")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = 0.21
    cam = bpy.data.objects.new("Cam", cam_data)
    scene.collection.objects.link(cam)
    cam.location = (0.0, -1.0, 0.0)
    cam.rotation_euler = (math.radians(90), 0, 0)
    scene.camera = cam
    scene.render.filepath = ICON
    bpy.ops.render.render(write_still=True)
    print(f"Иконка: {ICON}")


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    make_texture()
    print(f"Текстура: {TEX}")
    can = make_model()
    render_icon(can)

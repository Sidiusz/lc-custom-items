# Генерирует низкополигональный монитор 27" для предмета Monitor27:
# меш, UV-атлас, текстуру 512x512 и FBX с встроенной текстурой в Assets/Items/Monitor27/.
#
# Запуск из корня репозитория:
#   blender -b --factory-startup -P Tools/Blender/monitor27.py
#   blender -b --factory-startup -P Tools/Blender/monitor27.py -- --preview preview.png
#
# Размеры в метрах (1 единица Unity = 1 м). Начало координат в центре модели,
# экран смотрит в +Z Unity, низ подставки на y = -HEIGHT/2.

import os
import sys

import bmesh
import bpy
import numpy as np
from mathutils import Vector

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT_DIR = os.path.join(REPO, "Assets", "Items", "Monitor27")
NAME = "Monitor27"
TEX = 512

# ---- Размеры: 27" 16:9 = экран 0.598 x 0.336 ----
PANEL_W, PANEL_H, PANEL_D = 0.620, 0.370, 0.026
SCREEN_W, SCREEN_H = 0.598, 0.336
PANEL_BOTTOM = 0.105                       # от пола до низа панели
HUMP_W, HUMP_H, HUMP_D = 0.380, 0.230, 0.034
NECK_W, NECK_D = 0.060, 0.026
BASE_W, BASE_H, BASE_D = 0.250, 0.014, 0.190
HEIGHT = PANEL_BOTTOM + PANEL_H

# ---- Регионы атласа в пикселях: (x0, y0, x1, y1), y от верха картинки ----
R_PLASTIC = (0, 0, 256, 256)
R_STAND = (0, 256, 256, 512)
R_SCREEN = (256, 0, 512, 144)
R_BACK = (256, 144, 512, 320)
R_LED = (256, 320, 272, 336)
R_LOGO = (288, 320, 352, 336)


# ---------------------------------------------------------------- текстура

def noise(rng, h, w, scale):
    """Value-noise: случайная сетка, растянутая билинейно."""
    gh, gw = max(2, h // scale + 2), max(2, w // scale + 2)
    g = rng.random((gh, gw))
    ys = np.linspace(0, gh - 1.001, h)
    xs = np.linspace(0, gw - 1.001, w)
    y0, x0 = ys.astype(int), xs.astype(int)
    fy, fx = (ys - y0)[:, None], (xs - x0)[None, :]
    a = g[y0][:, x0]
    b = g[y0][:, x0 + 1]
    c = g[y0 + 1][:, x0]
    d = g[y0 + 1][:, x0 + 1]
    return a * (1 - fx) * (1 - fy) + b * fx * (1 - fy) + c * (1 - fx) * fy + d * fx * fy


def fbm(rng, h, w):
    return (noise(rng, h, w, 64) * 0.5 + noise(rng, h, w, 16) * 0.3 + noise(rng, h, w, 4) * 0.2)


def scratches(rng, img, count, color, length=(6, 30)):
    h, w, _ = img.shape
    for _ in range(count):
        x, y = rng.random() * w, rng.random() * h
        ang = rng.random() * np.pi
        n = int(rng.integers(*length))
        for i in range(n):
            px, py = int(x + np.cos(ang) * i) % w, int(y + np.sin(ang) * i) % h
            img[py, px] = img[py, px] * 0.4 + np.array(color) * 0.6


def region(img, r):
    x0, y0, x1, y1 = r
    return img[y0:y1, x0:x1]


def paint_plastic(rng, a, base, grime):
    h, w, _ = a.shape
    n = fbm(rng, h, w)[..., None]
    a[:] = np.array(base) * (0.85 + 0.3 * n)
    dirt = (noise(rng, h, w, 24) > 0.72)[..., None]
    a[:] = np.where(dirt, a * 0.75 + np.array(grime) * 0.25, a)
    scratches(rng, a, 25, (0.30, 0.30, 0.31))


def paint_screen(rng, a):
    h, w, _ = a.shape
    yy, xx = np.mgrid[0:h, 0:w] / np.array([h, w])[:, None, None]
    # Тёмное стекло с холодным бликом по диагонали
    glare = np.clip(1 - np.abs((xx * 0.9 + (1 - yy)) - 1.05) * 3.0, 0, 1) ** 2
    a[:] = np.array([0.025, 0.03, 0.04])
    a += glare[..., None] * np.array([0.10, 0.13, 0.17])
    a += (fbm(rng, h, w)[..., None] - 0.5) * 0.03
    # Пыль
    dust = rng.random((h, w)) > 0.997
    a[dust] = a[dust] * 0.5 + 0.12
    # Трещина из правого нижнего угла: несколько ломаных лучей
    cx, cy = int(w * 0.82), int(h * 0.78)
    for k in range(7):
        x, y = float(cx), float(cy)
        ang = k / 7 * np.pi * 2 + rng.random() * 0.6
        for _ in range(int(rng.integers(15, 60))):
            ang += (rng.random() - 0.5) * 0.25
            x, y = x + np.cos(ang), y + np.sin(ang)
            ix, iy = int(x), int(y)
            if 0 <= ix < w and 0 <= iy < h:
                a[iy, ix] = (0.55, 0.60, 0.62)
    yy2, xx2 = np.mgrid[0:h, 0:w]
    hole = (xx2 - cx) ** 2 + (yy2 - cy) ** 2 < 9
    a[hole] = (0.5, 0.55, 0.58)
    # Тонкая чёрная рамка матрицы по краю
    a[:3, :] = a[-3:, :] = 0.01
    a[:, :3] = a[:, -3:] = 0.01


def paint_back(rng, a, base, grime):
    paint_plastic(rng, a, base, grime)
    h, w, _ = a.shape
    # Вентиляционные прорези
    for row in range(5):
        y = 14 + row * 9
        for col in range(10):
            x = 22 + col * 22
            a[y:y + 4, x:x + 16] = 0.012
    # Наклейка с серийником: желтоватая бумага с «текстом»
    sx, sy = 158, 112
    a[sy:sy + 40, sx:sx + 80] = (0.62, 0.58, 0.45)
    for line in range(5):
        ly = sy + 6 + line * 7
        ln = int(rng.integers(30, 70))
        bars = rng.random(ln) > 0.35
        a[ly:ly + 3, sx + 6:sx + 6 + ln][:, bars] = 0.08
    a[sy:sy + 40, sx:sx + 80] *= 0.85 + 0.15 * noise(rng, 40, 80, 8)[..., None]
    # Крепление VESA: четыре отверстия
    for dx in (0, 1):
        for dy in (0, 1):
            ox, oy = 40 + dx * 176, 70 + dy * 90
            a[oy - 3:oy + 3, ox - 3:ox + 3] = 0.03


def build_texture():
    rng = np.random.default_rng(27)
    img = np.zeros((TEX, TEX, 3))
    plastic = (0.085, 0.085, 0.09)
    grime = (0.20, 0.17, 0.12)
    paint_plastic(rng, region(img, R_PLASTIC), plastic, grime)
    paint_plastic(rng, region(img, R_STAND), (0.11, 0.11, 0.115), grime)
    scratches(rng, region(img, R_STAND), 15, (0.40, 0.40, 0.41))
    paint_screen(rng, region(img, R_SCREEN))
    paint_back(rng, region(img, R_BACK), plastic, grime)
    region(img, R_LED)[:] = (1.0, 0.45, 0.05)   # оранжевый: монитор в режиме ожидания
    logo = region(img, R_LOGO)
    logo[:] = plastic
    logo[5:11, 4:60] = 0.35                      # серебристая полоска логотипа
    img = np.clip(img, 0, 1)

    # sRGB для PNG
    srgb = np.where(img <= 0.0031308, img * 12.92, 1.055 * np.power(img, 1 / 2.4) - 0.055)
    pix = np.ones((TEX, TEX, 4), dtype=np.float32)
    pix[..., :3] = srgb[::-1]                    # Blender хранит строки снизу вверх
    image = bpy.data.images.new(NAME, TEX, TEX, alpha=False)
    image.pixels.foreach_set(pix.ravel())
    image.filepath_raw = os.path.join(OUT_DIR, NAME + ".png")
    image.file_format = "PNG"
    image.save()
    image.pack()
    return image


# ---------------------------------------------------------------- геометрия

def to_uv(px, py):
    return px / TEX, 1 - py / TEX


def map_face(face, uv_layer, r, axes, flip_u=False, flip_v=False):
    """Растягивает проекцию грани на прямоугольник атласа."""
    x0, y0, x1, y1 = r
    pad = 1.5
    pts = [(l.vert.co[axes[0]], l.vert.co[axes[1]]) for l in face.loops]
    us, vs = [p[0] for p in pts], [p[1] for p in pts]
    umin, umax, vmin, vmax = min(us), max(us), min(vs), max(vs)
    for l, (u, v) in zip(face.loops, pts):
        fu = (u - umin) / max(umax - umin, 1e-6)
        fv = (v - vmin) / max(vmax - vmin, 1e-6)
        if flip_u:
            fu = 1 - fu
        if flip_v:
            fv = 1 - fv
        px = x0 + pad + fu * (x1 - x0 - 2 * pad)
        py = y1 - pad - fv * (y1 - y0 - 2 * pad)
        l[uv_layer].uv = to_uv(px, py)


def face_axes(face):
    """Оси проекции и зеркалирование по нормали грани (Blender: Z вверх, -Y вперёд)."""
    n = face.normal
    ax = max(range(3), key=lambda i: abs(n[i]))
    if ax == 0:
        return (1, 2), n[0] < 0, False
    if ax == 1:
        return (0, 2), n[1] > 0, False
    return (0, 1), False, n[2] < 0


def add_box(bm, uv, size, center, r, r_overrides=None):
    """Коробка; r_overrides: {направление нормали: регион} для отдельных граней."""
    res = bmesh.ops.create_cube(bm, size=1.0)
    verts = res["verts"]
    bmesh.ops.scale(bm, vec=Vector(size), verts=verts)
    bmesh.ops.translate(bm, vec=Vector(center), verts=verts)
    faces = list({f for v in verts for f in v.link_faces})
    for f in faces:
        f.normal_update()
        rr = r
        for key, val in (r_overrides or {}).items():
            if f.normal.dot(Vector(key)) > 0.9:
                rr = val
        axes, fu, fv = face_axes(f)
        map_face(f, uv, rr, axes, fu, fv)
    return faces


def build_mesh():
    # Blender: X вправо, -Y вперёд (к зрителю), Z вверх. При экспорте -Y станет +Z Unity.
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    zc = HEIGHT / 2                      # сдвиг, чтобы центр модели был в начале координат
    panel_z = PANEL_BOTTOM + PANEL_H / 2 - zc

    # Панель: передняя грань потом вдавливается под экран
    panel = add_box(bm, uv, (PANEL_W, PANEL_D, PANEL_H), (0, 0, panel_z), R_PLASTIC)
    front = next(f for f in panel if f.normal.y < -0.9)
    bezel_side = (PANEL_W - SCREEN_W) / 2
    bezel_bottom = PANEL_H - SCREEN_H - bezel_side
    inset = bmesh.ops.inset_individual(bm, faces=[front], thickness=bezel_side, depth=-0.003)
    # Рамка снизу толще: опускаем нижние вершины экрана
    screen_verts = list(front.verts)
    for v in screen_verts:
        if v.co.z < panel_z:
            v.co.z += bezel_bottom - bezel_side
    for f in inset["faces"]:
        f.normal_update()
        axes, fu, fv = face_axes(f)
        map_face(f, uv, R_PLASTIC, axes, fu, fv)
    front.normal_update()
    map_face(front, uv, R_SCREEN, (0, 2))

    # Задний горб с вентиляцией и наклейкой
    hump_z = panel_z - 0.02
    add_box(bm, uv, (HUMP_W, HUMP_D, HUMP_H), (0, PANEL_D / 2 + HUMP_D / 2, hump_z),
            R_PLASTIC, {(0, 1, 0): R_BACK})

    # Ножка и подставка
    neck_y = PANEL_D / 2 + HUMP_D + NECK_D / 2 - 0.004
    neck_top = hump_z + 0.04
    neck_bottom = BASE_H - zc
    add_box(bm, uv, (NECK_W, NECK_D, neck_top - neck_bottom),
            (0, neck_y, (neck_top + neck_bottom) / 2), R_STAND)
    add_box(bm, uv, (BASE_W, BASE_D, BASE_H), (0, neck_y - 0.03, BASE_H / 2 - zc), R_STAND)

    # Логотип и светодиод на нижней рамке
    bez_z = PANEL_BOTTOM + bezel_bottom / 2 - zc
    front_y = -PANEL_D / 2 - 0.0008
    add_box(bm, uv, (0.07, 0.0016, 0.008), (0, front_y, bez_z), R_LOGO)
    add_box(bm, uv, (0.005, 0.0016, 0.005), (0.27, front_y, bez_z), R_LED)

    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    mesh = bpy.data.meshes.new(NAME)
    bm.to_mesh(mesh)
    bm.free()
    for p in mesh.polygons:
        p.use_smooth = False
    return mesh


def build_material(image):
    mat = bpy.data.materials.new(NAME)
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = image
    tex.interpolation = "Closest"
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 0.6
    bsdf.inputs["Metallic"].default_value = 0.0
    return mat


def render_preview(obj, path, back=False):
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 48
    scene.render.resolution_x, scene.render.resolution_y = 900, 640
    scene.render.film_transparent = False
    world = bpy.data.worlds.new("W")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.05, 0.05, 0.055, 1)
    scene.world = world

    floor = bpy.data.meshes.new("Floor")
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=3)
    bm.to_mesh(floor)
    bm.free()
    fo = bpy.data.objects.new("Floor", floor)
    fo.location.z = -HEIGHT / 2
    fm = bpy.data.materials.new("FloorMat")
    fm.use_nodes = True
    fm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.12, 0.11, 0.1, 1)
    floor.materials.append(fm)
    scene.collection.objects.link(fo)

    for loc, energy in (((-1.2, -1.6, 1.4), 120), ((1.5, 1.0, 1.0), 60)):
        ld = bpy.data.lights.new("L", "POINT")
        ld.energy = energy
        lo = bpy.data.objects.new("L", ld)
        lo.location = loc
        scene.collection.objects.link(lo)

    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    cam.location = (0.75, -1.05, 0.30) if not back else (-0.8, 0.95, 0.35)
    direction = Vector((0, 0, -0.02)) - cam.location
    cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    scene.collection.objects.link(cam)
    scene.camera = cam
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    preview = argv[argv.index("--preview") + 1] if "--preview" in argv else None

    os.makedirs(OUT_DIR, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)

    image = build_texture()
    mesh = build_mesh()
    mesh.materials.append(build_material(image))
    obj = bpy.data.objects.new(NAME, mesh)
    bpy.context.scene.collection.objects.link(obj)
    print(f"{NAME}: {len(mesh.polygons)} граней, {len(mesh.vertices)} вершин, "
          f"габариты {tuple(round(d, 3) for d in obj.dimensions)}")

    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.fbx(
        filepath=os.path.join(OUT_DIR, NAME + ".fbx"),
        use_selection=True,
        object_types={"MESH"},
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        bake_space_transform=True,
        mesh_smooth_type="FACE",
        path_mode="COPY",
        embed_textures=True,
        bake_anim=False,
        add_leaf_bones=False,
    )
    # Текстура уже внутри FBX; отдельный PNG в Assets дал бы дубликат в Unity
    os.remove(os.path.join(OUT_DIR, NAME + ".png"))

    if preview:
        render_preview(obj, os.path.abspath(preview), back="--back" in argv)


main()

"""Модель игрушечного пистолета (белый затвор, голубая рамка, красное кольцо на дуле).

Скрипт Blender 4.2: строит модель из профилей и булевых вырезов, скругляет края,
запекает цвет и ambient occlusion в текстуру и экспортирует в Assets/Sources/Art/:
Models/ToyGun/ToyGunModel.obj, Textures/ToyGun/ToyGunTexture.png и звук выстрела Audio/ToyGun/ToyGunShot.wav.

Запуск из корня репозитория:
    blender -b -P Tools/ToyGun/model.py                  # только ассеты
    blender -b -P Tools/ToyGun/model.py -- --preview p.png  # плюс картинка-превью

Размеры в миллиметрах. В профилях u = вперёд (к дулу), v = вверх, x = вбок.
В Blender ствол смотрит в -Y; после экспорта OBJ (forward -Z, up Y) и импорта в
Unity он смотрит в +Z. Начало координат в верхней части рукояти, где её держит рука.
"""
import math
import os
import sys
import wave

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
MODEL_DIR = os.path.join(REPO, "Assets", "Sources", "Art", "Models", "ToyGun")
TEXTURE_DIR = os.path.join(REPO, "Assets", "Sources", "Art", "Textures", "ToyGun")
AUDIO_DIR = os.path.join(REPO, "Assets", "Sources", "Art", "Audio", "ToyGun")
MODEL_NAME = "ToyGunModel"
TEXTURE_NAME = "ToyGunTexture"
SOUND_NAME = "ToyGunShot"
RELOAD_SOUND_NAME = "ToyGunReload"
DRY_SOUND_NAME = "ToyGunDryFire"
# Во сколько раз игрушка крупнее настоящего пистолета: в руке игрока 1:1 выглядит крошечным
SCALE = 1.6
TEXTURE_SIZE = 1024

# Точка, где рука держит рукоять, и центр дула (u, v в мм)
ORIGIN_UV = (-24.0, -30.0)
BORE_V = 54.0
MUZZLE_U = 101.0


def srgb(r, g, b):
    def lin(c):
        c /= 255.0
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    return (lin(r), lin(g), lin(b), 1.0)


COLORS = {
    "slide": (srgb(236, 236, 232), 0.35),
    "frame": (srgb(120, 185, 230), 0.5),
    "red": (srgb(225, 30, 25), 0.3),
    "dark": (srgb(28, 28, 32), 0.45),
    "bore": (srgb(10, 10, 10), 0.8),
}


def material(name):
    mat = bpy.data.materials.get(name)
    if mat:
        return mat
    color, rough = COLORS[name]
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = color
    bsdf.inputs["Roughness"].default_value = rough
    return mat


def to_world(u, v, x=0.0):
    return Vector((x, -u, v))


def prism(name, profile, width, mat, x_center=0.0):
    """Профиль (u, v) в плоскости сбоку, вытянутый по ширине x."""
    bm = bmesh.new()
    # Обход профиля против часовой стрелки, если смотреть с +X
    area = sum(profile[i][0] * profile[(i + 1) % len(profile)][1]
               - profile[(i + 1) % len(profile)][0] * profile[i][1] for i in range(len(profile)))
    if area < 0:
        profile = list(reversed(profile))
    left = [bm.verts.new(to_world(u, v, x_center - width / 2)) for u, v in profile]
    right = [bm.verts.new(to_world(u, v, x_center + width / 2)) for u, v in profile]
    bm.faces.new(right)
    bm.faces.new(list(reversed(left)))
    n = len(profile)
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new([left[i], left[j], right[j], right[i]])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return make_object(name, bm, mat)


def section(name, profile_xv, u0, u1, mat):
    """Сечение (x, v), вытянутое вдоль ствола от u0 до u1."""
    bm = bmesh.new()
    back = [bm.verts.new(to_world(u0, v, x)) for x, v in profile_xv]
    front = [bm.verts.new(to_world(u1, v, x)) for x, v in profile_xv]
    bm.faces.new(back)
    bm.faces.new(list(reversed(front)))
    n = len(profile_xv)
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new([back[i], back[j], front[j], front[i]])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    return make_object(name, bm, mat)


def box(name, u0, u1, v0, v1, x0, x1, mat=None):
    return section(name, [(x0, v0), (x1, v0), (x1, v1), (x0, v1)], u0, u1, mat)


def cylinder_u(name, radius, u0, u1, v, mat=None, x=0.0, sides=32):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=sides, radius1=radius, radius2=radius, depth=u1 - u0)
    # create_cone строит вдоль Z, поворачиваем вдоль -Y (ствол)
    bmesh.ops.rotate(bm, verts=bm.verts, matrix=Matrix.Rotation(math.pi / 2, 3, "X"))
    bmesh.ops.translate(bm, verts=bm.verts, vec=to_world((u0 + u1) / 2, v, x))
    return make_object(name, bm, mat)


def make_object(name, bm, mat):
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    if mat:
        mesh.materials.append(mat)
    return obj


def cut(target, *cutters):
    for c in cutters:
        mod = target.modifiers.new("cut", "BOOLEAN")
        mod.operation = "DIFFERENCE"
        mod.solver = "EXACT"
        mod.object = c
        c.hide_render = True
        c.hide_viewport = True
    return target


def bevel(obj, width, segments=3, angle=35):
    mod = obj.modifiers.new("bevel", "BEVEL")
    mod.width = width
    mod.segments = segments
    mod.limit_method = "ANGLE"
    mod.angle_limit = math.radians(angle)
    mod.harden_normals = False
    mod.miter_outer = "MITER_ARC"
    return obj


def build():
    slide_m, frame_m, red_m, dark_m, bore_m = (material(n) for n in ("slide", "frame", "red", "dark", "bore"))
    parts = []

    # ---- Затвор: сечение с фасками сверху, вдоль всей длины ----
    slide = section("Slide", [(-12.5, 41), (12.5, 41), (12.5, 66), (8.5, 72), (-8.5, 72), (-12.5, 66)],
                    -88, 99, slide_m)
    cutters = []
    # Насечки сзади по бокам
    for i in range(7):
        u = -82 + i * 3.6
        for side in (-1, 1):
            cutters.append(box(f"serr{i}{side}", u, u + 1.6, 45, 69, side * 11.6, side * 16))
    # Окно выброса гильзы сверху справа
    cutters.append(box("port", 8, 42, 68.5, 80, 0.5, 20))
    # Срез низа затвора спереди (скос как у оригинала)
    cutters.append(prism("nose", [(86, 36), (104, 36), (104, 47)], 40, None))
    # Отверстие ствола
    cutters.append(cylinder_u("boreCut", 7.5, 92, 110, BORE_V))
    cut(slide, *cutters)
    bevel(slide, 1.4)
    parts.append(slide)

    # Красное кольцо на дуле и тёмный ствол внутри
    ring = cylinder_u("Ring", 9.5, 96.5, MUZZLE_U, BORE_V, red_m)
    cut(ring, cylinder_u("ringHole", 6.2, 90, 110, BORE_V))
    bevel(ring, 0.8, 2, 30)
    parts.append(ring)
    parts.append(cylinder_u("Bore", 6.3, 90, 95, BORE_V, bore_m))

    # Прицелы
    front_sight = box("FrontSight", 86, 92, 71, 76, -1.6, 1.6, slide_m)
    parts.append(bevel(front_sight, 0.6, 2))
    rear_sight = box("RearSight", -84, -77, 71, 77, -9, 9, slide_m)
    cut(rear_sight, box("notch", -90, -70, 73.5, 80, -1.8, 1.8))
    parts.append(bevel(rear_sight, 0.6, 2))

    # ---- Рамка: пылезащитная часть, спусковая скоба и рукоять одним профилем ----
    frame_profile = [
        (-86, 42), (95, 42), (95, 30), (89, 23), (46, 23), (44, 20), (41, 20),
        (38, -1), (33, -6), (8, -6), (3, -12), (-17, -86), (-22, -92), (-64, -92),
        (-69, -87), (-51, -10), (-56, 6), (-68, 20), (-84, 29), (-86, 33),
    ]
    frame = prism("Frame", frame_profile, 27, frame_m)
    guard_hole = prism("guardHole", [(36, 19), (33.5, 0.5), (29, -1), (10, -1), (6.5, 6), (5, 19)], 40, None)
    cutters = [guard_hole]
    # Канавки планки под фонарь
    for i in range(3):
        u = 52 + i * 11
        cutters.append(box(f"rail{i}", u, u + 4, 20, 25.5, -20, 20))
    # Углубление-площадка на боках рукояти
    for side in (-1, 1):
        cutters.append(prism(f"panel{side}", [(-8, -18), (-24, -82), (-56, -82), (-41, -18)], 3,
                             None, x_center=side * 14.4))
    # Выемка под большой палец над скобой
    for side in (-1, 1):
        cutters.append(prism(f"thumb{side}", [(-2, 10), (12, 10), (12, 20), (-2, 20)], 3,
                             None, x_center=side * 14.6))
    cut(frame, *cutters)
    bevel(frame, 2.6, 3, 30)
    parts.append(frame)

    # Пятка магазина
    mag = prism("MagBase", [(-17, -90), (-21, -97), (-67, -97), (-69, -90)], 27, dark_m)
    parts.append(bevel(mag, 1.5, 2))

    # Спусковой крючок
    trigger = prism("Trigger", [(23, 21), (22, 12), (19.5, 6), (15.5, 3), (16, 6.5), (18, 11), (18.5, 21)],
                    5.5, dark_m)
    parts.append(bevel(trigger, 0.6, 2))

    # Затворная задержка и рычаг разборки
    parts.append(bevel(box("SlideStop", -6, 16, 36.5, 39.5, -14.6, -12.5, dark_m), 0.6, 2))
    for side in (-1, 1):
        parts.append(bevel(box(f"Takedown{side}", 27, 31, 35, 41, side * 12.5, side * 14.2, dark_m), 0.5, 2))

    return parts


def finalize(parts):
    bpy.ops.object.select_all(action="DESELECT")
    for p in parts:
        p.hide_viewport = False
        p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.convert(target="MESH")  # применяет модификаторы
    bpy.ops.object.join()
    gun = bpy.context.active_object
    gun.name = MODEL_NAME
    # Удаляем вспомогательные объекты-резаки
    for obj in list(bpy.data.objects):
        if obj != gun and obj.type == "MESH":
            bpy.data.objects.remove(obj, do_unlink=True)

    # Начало координат на рукояти, мм → метры
    offset = to_world(*ORIGIN_UV)
    gun.data.transform(Matrix.Translation(-offset))
    gun.data.transform(Matrix.Scale(0.001 * SCALE, 4))
    # Треугольники сразу: так текстура запекается на тех же треугольниках, что увидит Unity
    bm = bmesh.new()
    bm.from_mesh(gun.data)
    bmesh.ops.triangulate(bm, faces=bm.faces, quad_method="BEAUTY", ngon_method="BEAUTY")
    bm.to_mesh(gun.data)
    bm.free()
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(35), keep_sharp_edges=True)
    return gun


def bake(gun):
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 64

    bpy.ops.object.select_all(action="DESELECT")
    gun.select_set(True)
    bpy.context.view_layer.objects.active = gun
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.004)
    bpy.ops.object.mode_set(mode="OBJECT")

    def target(name, fill):
        # Фон незапечённых мест: цвет рамки для цвета и белый для AO, иначе мипмапы
        # затягивают чёрный фон в тонкие острова (чёрные точки на модели в игре)
        img = bpy.data.images.new(name, TEXTURE_SIZE, TEXTURE_SIZE, alpha=False)
        img.pixels[:] = np.tile(np.array(fill, dtype=np.float32), TEXTURE_SIZE * TEXTURE_SIZE)
        for mat in gun.data.materials:
            nodes = mat.node_tree.nodes
            node = nodes.get("BakeTarget") or nodes.new("ShaderNodeTexImage")
            node.name = "BakeTarget"
            node.image = img
            nodes.active = node
        return img

    color = target("color", (120 / 255, 185 / 255, 230 / 255, 1))  # цвет рамки в sRGB
    bpy.ops.object.bake(type="DIFFUSE", pass_filter={"COLOR"}, margin=24, use_clear=False)
    ao = target("ao", (1, 1, 1, 1))
    scene.world = scene.world or bpy.data.worlds.new("World")
    bpy.ops.object.bake(type="AO", margin=24, use_clear=False)

    c = np.array(color.pixels[:]).reshape(-1, 4)
    a = np.array(ao.pixels[:]).reshape(-1, 4)[:, :1]
    # AO мягко: глубокие щели темнее, плоские места почти без изменений
    c[:, :3] *= 0.35 + 0.65 * a
    color.pixels[:] = c.ravel()

    out = bpy.data.images.new(TEXTURE_NAME, TEXTURE_SIZE, TEXTURE_SIZE, alpha=False)
    out.pixels[:] = color.pixels[:]
    out.filepath_raw = os.path.join(TEXTURE_DIR, TEXTURE_NAME + ".png")
    out.file_format = "PNG"
    out.save()

    # Один материал с запечённой текстурой вместо цветных
    final = bpy.data.materials.new("ToyGunPlastic")
    final.use_nodes = True
    tex = final.node_tree.nodes.new("ShaderNodeTexImage")
    tex.image = out
    bsdf = final.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Roughness"].default_value = 0.4
    final.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    gun.data.materials.clear()
    gun.data.materials.append(final)


def export(gun):
    bpy.ops.object.select_all(action="DESELECT")
    gun.select_set(True)
    bpy.ops.wm.obj_export(filepath=os.path.join(MODEL_DIR, MODEL_NAME + ".obj"), export_selected_objects=True,
                          forward_axis="NEGATIVE_Z", up_axis="Y", export_uv=True, export_normals=True,
                          export_materials=True, path_mode="RELATIVE", apply_modifiers=True)


RATE = 44100


def write_shot_sound(path):
    """Пластиковый щелчок пружины и короткий хлопок с падающим тоном."""
    rng = np.random.default_rng(3)
    t = np.arange(int(RATE * 0.18)) / RATE
    click = rng.normal(0, 1, t.size) * np.exp(-t * 400)
    freq = 900 * np.exp(-t * 18) + 250
    pop = np.sin(2 * np.pi * np.cumsum(freq) / RATE) * np.exp(-t * 35)
    rattle = rng.normal(0, 1, t.size) * np.exp(-((t - 0.04) ** 2) / 0.0002) * 0.25
    write_wav(path, 0.55 * click + 0.7 * pop + rattle)


def plastic_click(t, at, pitch, rng):
    """Короткий щелчок пластика в момент at."""
    dt = np.clip(t - at, 0, None)
    on = t >= at
    tone = np.sin(2 * np.pi * pitch * dt) * np.exp(-dt * 120)
    noise = rng.normal(0, 1, t.size) * np.exp(-dt * 300)
    return on * (0.6 * tone + 0.8 * noise)


def write_reload_sound(path):
    """Перезарядка: защёлка магазина, шуршание пулек, щелчок затвора."""
    rng = np.random.default_rng(5)
    t = np.arange(int(RATE * 0.75)) / RATE
    rattle = np.zeros_like(t)
    for at in rng.uniform(0.12, 0.42, 14):  # пульки сыплются в магазин
        rattle += plastic_click(t, at, rng.uniform(2500, 4000), rng) * 0.25
    signal = (plastic_click(t, 0.02, 900, rng) + rattle + plastic_click(t, 0.55, 700, rng)
              + plastic_click(t, 0.6, 1100, rng) * 0.7)
    write_wav(path, signal)


def write_dry_fire_sound(path):
    """Пустой магазин: сухой щелчок спуска."""
    rng = np.random.default_rng(7)
    t = np.arange(int(RATE * 0.12)) / RATE
    write_wav(path, plastic_click(t, 0.005, 1400, rng), volume=0.5)


def write_wav(path, signal, volume=0.8):
    signal = signal / np.max(np.abs(signal))
    data = (signal * volume * 32767).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(data.tobytes())


def render_preview(gun, path):
    scene = bpy.context.scene
    scene.render.resolution_x, scene.render.resolution_y = 1200, 800
    scene.cycles.samples = 96
    world = scene.world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.55, 0.57, 0.6, 1)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.6

    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sun.data.energy = 3.5
    sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(-40))
    scene.collection.objects.link(sun)

    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    cam.data.lens = 55
    scene.collection.objects.link(cam)
    scene.camera = cam
    center = Vector((0, -0.045, 0.02)) * SCALE
    root, ext = os.path.splitext(path)
    for i, view in enumerate([Vector((0.5, -0.32, 0.16)), Vector((-0.45, 0.25, 0.1))]):
        cam.location = center + view * SCALE
        cam.rotation_euler = (center - cam.location).to_track_quat("-Z", "Y").to_euler()
        scene.render.filepath = path if i == 0 else f"{root}_back{ext}"
        bpy.ops.render.render(write_still=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    preview = argv[argv.index("--preview") + 1] if "--preview" in argv else None

    bpy.ops.wm.read_factory_settings(use_empty=True)
    for d in (MODEL_DIR, TEXTURE_DIR, AUDIO_DIR):
        os.makedirs(d, exist_ok=True)
    gun = finalize(build())
    bake(gun)
    export(gun)
    write_shot_sound(os.path.join(AUDIO_DIR, SOUND_NAME + ".wav"))
    write_reload_sound(os.path.join(AUDIO_DIR, RELOAD_SOUND_NAME + ".wav"))
    write_dry_fire_sound(os.path.join(AUDIO_DIR, DRY_SOUND_NAME + ".wav"))

    tris = sum(len(p.vertices) - 2 for p in gun.data.polygons)
    muzzle = (to_world(MUZZLE_U, BORE_V) - to_world(*ORIGIN_UV)) * 0.001 * SCALE
    # В Unity: x = Blender x (OBJ зеркалит, но x = 0), y = Blender z, z = -Blender y
    print(f"Треугольников: {tris}; дуло в Unity: (0, {muzzle.z:.4f}, {-muzzle.y:.4f})")
    xs, ys, zs = zip(*(v.co for v in gun.data.vertices))
    lo, hi = Vector((-max(xs), min(zs), -max(ys))), Vector((-min(xs), max(zs), -min(ys)))
    print(f"Границы в Unity: центр {tuple(round(c, 4) for c in (lo + hi) / 2)}, "
          f"размер {tuple(round(c, 4) for c in hi - lo)}")
    if preview:
        render_preview(gun, os.path.abspath(preview))


main()

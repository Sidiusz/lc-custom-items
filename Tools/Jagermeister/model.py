"""Низкополигональная бутылка Егермейстера -> Assets/Sources/Art/Models/Jagermeister/Jagermeister.fbx

Запуск (Blender 3.6+ / 4.x):
  blender --background --python model.py

Сначала texture.py (рисует Jagermeister.png). UV-раскладка описана там же.
Размеры настоящей бутылки 0,7 л: ~25 см в высоту, ~8,5 см в ширину. 1 единица = 1 метр.
Центр модели в середине бутылки, перёд (этикетка) смотрит в -Y Блендера.
Объекты: Bottle (стекло и крышка) и Liquid1..Liquid3 (жидкость снизу вверх).
"""
import math
import os

import bmesh
import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.normpath(os.path.join(HERE, "..", "..", "Assets", "Sources", "Art", "Models", "Jagermeister"))
TEX_DIR = os.path.normpath(os.path.join(HERE, "..", "..", "Assets", "Sources", "Art", "Textures", "Jagermeister"))
NAME = "Jagermeister"
SEGMENTS = 24  # точек по обхвату; кратно 4, чтобы углы попадали в вершины

# (высота, полуширина, степень суперэллипса: 2 = круг, больше = квадратнее)
GLASS_PROFILE = [
    (0.000, 0.038, 4.0),
    (0.004, 0.042, 4.0),
    (0.150, 0.042, 4.0),
    (0.165, 0.040, 3.4),
    (0.180, 0.032, 2.8),
    (0.192, 0.022, 2.3),
    (0.200, 0.016, 2.0),
    (0.209, 0.015, 2.0),
]
GLASS_V = (0.0, 0.80)
CAP_PROFILE = [
    (0.203, 0.015, 2.0),
    (0.205, 0.0175, 2.0),
    (0.248, 0.0175, 2.0),
    (0.250, 0.0160, 2.0),
]
CAP_V = (0.82, 1.0)
HEIGHT = 0.250


def ring(half, exp):
    """Точки суперэллипса, равномерно по длине дуги, по часовой, если смотреть сверху.
    Первая точка сзади (+Y); перёд (-Y) приходится на u = 0.5."""
    dense = []
    n = 512
    for i in range(n + 1):
        a = math.pi / 2 - 2 * math.pi * i / n
        c, s = math.cos(a), math.sin(a)
        x = half * math.copysign(abs(c) ** (2 / exp), c)
        y = half * math.copysign(abs(s) ** (2 / exp), s)
        dense.append((x, y))
    lengths = [0.0]
    for a, b in zip(dense, dense[1:]):
        lengths.append(lengths[-1] + math.dist(a, b))
    total = lengths[-1]
    pts, j = [], 0
    for k in range(SEGMENTS):
        target = total * k / SEGMENTS
        while lengths[j + 1] < target:
            j += 1
        t = (target - lengths[j]) / (lengths[j + 1] - lengths[j])
        (x0, y0), (x1, y1) = dense[j], dense[j + 1]
        pts.append((x0 + (x1 - x0) * t, y0 + (y1 - y0) * t))
    return pts


def lathe(bm, uv, profile, v_range, cap_bottom, cap_top):
    h0, h1 = profile[0][0], profile[-1][0]
    rows = []
    for h, half, exp in profile:
        rows.append([bm.verts.new((x, y, h - HEIGHT / 2)) for x, y in ring(half, exp)])
    vs = [v_range[0] + (h - h0) / (h1 - h0) * (v_range[1] - v_range[0]) for h, _, _ in profile]

    for r in range(len(rows) - 1):
        for k in range(SEGMENTS):
            k2 = (k + 1) % SEGMENTS
            f = bm.faces.new((rows[r][k], rows[r][k2], rows[r + 1][k2], rows[r + 1][k]))
            f.smooth = True
            u0, u1 = 1 - k / SEGMENTS, 1 - (k + 1) / SEGMENTS
            for loop, (u, v) in zip(f.loops, ((u0, vs[r]), (u1, vs[r]), (u1, vs[r + 1]), (u0, vs[r + 1]))):
                loop[uv].uv = (u, v)

    def disk(row, v, flip):
        f = bm.faces.new(list(reversed(row)) if flip else row)
        for loop in f.loops:
            loop[uv].uv = (0.5, v)

    if cap_bottom:
        disk(rows[0], v_range[0] + 0.01, flip=True)
    if cap_top:
        disk(rows[-1], v_range[1] - 0.01, flip=False)


# Жидкость внутри: три одинаковых слоя, по одному на глоток (JagermeisterItem прячет
# верхние, когда глотков осталось меньше). Чуть уже стекла и от дна до плеч.
LIQUID_HALF = 0.038
LIQUID_BOTTOM = 0.006
LIQUID_TOP = 0.150
LIQUID_LAYERS = 3
LIQUID_COLOR = (0.22, 0.08, 0.03, 1.0)  # тёмно-коричневый, как сам ликёр


def make_object(name, fill, material):
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    fill(bm, uv)
    # lathe() обходит кольца так, что грани смотрят внутрь; разворачиваем все разом
    # (UV остаются на своих вершинах). Без этого Unity отсекает лицевую сторону.
    bmesh.ops.reverse_faces(bm, faces=bm.faces[:])
    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(material)

    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def build():
    bpy.ops.wm.read_factory_settings(use_empty=True)

    glass = bpy.data.materials.new(NAME)
    glass.use_nodes = True
    bsdf = glass.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Roughness"].default_value = 0.15
    tex = glass.node_tree.nodes.new("ShaderNodeTexImage")
    tex.image = bpy.data.images.load(os.path.join(TEX_DIR, NAME + ".png"))
    glass.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    glass.node_tree.links.new(tex.outputs["Alpha"], bsdf.inputs["Alpha"])

    liquid = bpy.data.materials.new(NAME + "Liquid")
    liquid.use_nodes = True
    lb = liquid.node_tree.nodes["Principled BSDF"]
    lb.inputs["Base Color"].default_value = LIQUID_COLOR
    lb.inputs["Roughness"].default_value = 0.3
    liquid.diffuse_color = LIQUID_COLOR

    objects = [make_object("Bottle", lambda bm, uv: (
        lathe(bm, uv, GLASS_PROFILE, GLASS_V, cap_bottom=True, cap_top=False),
        lathe(bm, uv, CAP_PROFILE, CAP_V, cap_bottom=False, cap_top=True)), glass)]

    step = (LIQUID_TOP - LIQUID_BOTTOM) / LIQUID_LAYERS
    for i in range(LIQUID_LAYERS):
        h0 = LIQUID_BOTTOM + step * i
        profile = [(h0, LIQUID_HALF, 4.0), (h0 + step, LIQUID_HALF, 4.0)]
        objects.append(make_object(f"Liquid{i + 1}", lambda bm, uv, p=profile, first=(i == 0): lathe(
            bm, uv, p, (0.0, 0.0), cap_bottom=first, cap_top=True), liquid))

    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    # Гладкие стенки, острые рёбра у дна, на торце крышки и на крышках слоёв жидкости
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(50))
    return objects


def export():
    os.makedirs(OUT_DIR, exist_ok=True)
    path = os.path.join(OUT_DIR, NAME + ".fbx")
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        object_types={"MESH"},
        apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z",
        axis_up="Y",
        bake_space_transform=True,
        mesh_smooth_type="FACE",
        use_mesh_modifiers=True,
        add_leaf_bones=False,
        bake_anim=False,
        path_mode="STRIP",
    )
    print("saved", path)


build()
export()

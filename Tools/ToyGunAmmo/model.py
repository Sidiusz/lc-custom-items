"""Модель пачки пулек для игрушечного пистолета: коробка со скруглёнными краями
и выступающим клапаном сверху, грани разложены по текстуре из texture.py.

Скрипт Blender 4.2, запуск из корня репозитория:
    python3 Tools/ToyGunAmmo/texture.py
    blender -b -P Tools/ToyGunAmmo/model.py

Пишет Assets/Sources/Art/Models/ToyGunAmmo/ToyGunAmmo.fbx, центр модели в середине коробки.
"""
import os
import sys

import bmesh
import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from layout import DEPTH, HEIGHT, RECTS, TEXTURE_SIZE, WIDTH  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "Assets", "Sources", "Art", "Models", "ToyGunAmmo", "ToyGunAmmo.fbx")


def uv_rect(name):
    x0, y0, x1, y1 = RECTS[name]
    s = float(TEXTURE_SIZE)
    # BL, BR, TR, TL; v растёт вверх, а пиксели вниз
    return [(x0 / s, 1 - y1 / s), (x1 / s, 1 - y1 / s), (x1 / s, 1 - y0 / s), (x0 / s, 1 - y0 / s)]


def box(bm, uv, sx, sy, sz, z0=0.0, side_rect=None):
    x, y, z = sx / 2, sy / 2, sz / 2
    # Грани по порядку BL, BR, TR, TL, если смотреть на грань снаружи (обход против часовой)
    faces = {
        "front": [(-x, -y, -z), (x, -y, -z), (x, -y, z), (-x, -y, z)],
        "back": [(x, y, -z), (-x, y, -z), (-x, y, z), (x, y, z)],
        "right": [(x, -y, -z), (x, y, -z), (x, y, z), (x, -y, z)],
        "left": [(-x, y, -z), (-x, -y, -z), (-x, -y, z), (-x, y, z)],
        "top": [(-x, -y, z), (x, -y, z), (x, y, z), (-x, y, z)],
        "bottom": [(-x, y, -z), (x, y, -z), (x, -y, -z), (-x, -y, -z)],
    }
    verts = {}
    for name, corners in faces.items():
        vs = []
        for c in corners:
            key = (round(c[0], 6), round(c[1], 6), round(c[2] + z0, 6))
            if key not in verts:
                verts[key] = bm.verts.new(key)
            vs.append(verts[key])
        f = bm.faces.new(vs)
        rect = name if side_rect is None or name == "top" else side_rect
        for loop, coord in zip(f.loops, uv_rect(rect)):
            loop[uv].uv = coord


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    box(bm, uv, WIDTH, DEPTH, HEIGHT)
    # Клапан крышки чуть шире коробки и выше её, бока клапана однотонные
    box(bm, uv, WIDTH + 0.003, DEPTH + 0.003, 0.006, z0=HEIGHT / 2 - 0.001, side_rect="bottom")

    mesh = bpy.data.meshes.new("ToyGunAmmo")
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new("ToyGunAmmo", mesh)
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)

    bevel = obj.modifiers.new("bevel", "BEVEL")
    bevel.width = 0.0015
    bevel.segments = 2
    bpy.ops.object.modifier_apply(modifier="bevel")
    bpy.ops.object.shade_smooth_by_angle()

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    bpy.ops.export_scene.fbx(filepath=OUT, use_selection=True, object_types={"MESH"}, apply_unit_scale=True,
                             apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y",
                             bake_space_transform=True, mesh_smooth_type="FACE", add_leaf_bones=False,
                             bake_anim=False)
    tris = sum(len(p.vertices) - 2 for p in mesh.polygons)
    print(f"Модель: {OUT}, {tris} треугольников")


main()

# Генерирует модель умной колонки с часами (Яндекс Станция Мини) для предмета
# YandexStation: FBX в Assets/Sources/Art/Models/YandexStation/. Материалы
# HDRP/Lit по именам объектов собирает Editor/ItemAssets.cs (SetupYandexStation).
#
# Объекты модели:
#   Body          корпус (тканевый цилиндр со скруглённым верхом)
#   Trim          тёмная ножка, порт и кнопка микрофона сзади
#   Marks         «+», «−» и точки на крышке
#   Ring          кольцо Алисы на крышке (светится, пульсирует, когда колонка говорит)
#   Seg<цифра><a-g>  сегменты четырёх цифр часов, ColonTop/ColonBottom — двоеточие
#   MouthSmile, MouthHalf, MouthOpen  рот мордочки (глаза рисуются сегментами цифр)
# Что горит, решает YandexStationItem (время или мордочка).
#
# Запуск из корня репозитория (Blender или pip-пакет bpy):
#   blender -b --factory-startup -P Tools/YandexStation/model.py
#   python3 Tools/YandexStation/model.py -- --preview preview.png [--face] [--back]
#
# Размеры в метрах (1 единица Unity = 1 м). Начало координат в центре габаритов,
# цифры смотрят в +Z Unity.

import math
import os
import sys

import bpy  # до bmesh: в pip-пакете bpy модуль bmesh появляется только после него
import bmesh
from mathutils import Vector

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT_DIR = os.path.join(REPO, "Assets", "Sources", "Art", "Models", "YandexStation")
NAME = "YandexStation"

R = 0.070            # радиус корпуса
H = 0.090            # полная высота с ножкой
ZC = H / 2           # сдвиг: центр габаритов в начале координат
SIDES = 72

# Цифры: высота, ширина, толщина сегмента, зазор между сегментами
DIGIT_H, DIGIT_W, SEG_T, SEG_GAP = 0.026, 0.0135, 0.0030, 0.0006
DIGIT_U = (-0.0315, -0.0130, 0.0130, 0.0315)   # центры цифр по дуге (м), 0 — середина фронта
DISPLAY_Z = 0.047                               # высота центра цифр от пола
SURFACE_OFF = 0.0005                            # сегменты чуть над поверхностью

BODY_COLOR = (0.105, 0.105, 0.12, 1)
TRIM_COLOR = (0.035, 0.035, 0.04, 1)
MARK_COLOR = (0.55, 0.55, 0.58, 1)
DISPLAY_COLOR = (1.0, 0.82, 0.58, 1)
RING_COLOR = (0.55, 0.32, 1.0, 1)

# Цифры для превью: 12:35, как на фото
DIGITS = {
    0: "abcdef", 1: "bc", 2: "abdeg", 3: "abcdg", 4: "bcfg",
    5: "acdfg", 6: "acdefg", 7: "abc", 8: "abcdefg", 9: "abcdfg",
}
# Мордочка для превью (как в YandexStationItem): глаза-квадратики из сегментов и рот
FACE = ["abfg", "", "", "abfg"]
FACE_MOUTH = "MouthHalf"
MOUTH_V = -0.0075     # центр рта по высоте относительно центра цифр


# ---------------------------------------------------------------- корпус

def body_profile():
    """(радиус, высота от пола) снизу вверх: корпус от ножки до центра крышки."""
    pts = [(0.0655, 0.012), (0.0690, 0.0135), (0.0700, 0.0160),
           (0.0700, 0.0200), (0.0690, 0.0208), (0.0690, 0.0222), (0.0700, 0.0230)]  # шов у низа
    # Прямая стенка и скругление верха радиусом 0.017
    rr = 0.017
    top = H
    pts.append((R, top - rr))
    for i in range(1, 9):
        a = i / 8 * math.pi / 2
        pts.append((R - rr + rr * math.cos(a), top - rr + rr * math.sin(a)))
    # Чуть выпуклая крышка
    pts += [(0.035, top + 0.0006), (0.012, top + 0.0009)]
    return pts


def lathe(bm, profile, close_bottom=False, close_top=True):
    rings = []
    for r, z in profile:
        ring = [bm.verts.new((r * math.cos(2 * math.pi * k / SIDES),
                              r * math.sin(2 * math.pi * k / SIDES), z - ZC)) for k in range(SIDES)]
        rings.append(ring)
    for a, b in zip(rings, rings[1:]):
        for k in range(SIDES):
            k2 = (k + 1) % SIDES
            bm.faces.new((a[k], a[k2], b[k2], b[k]))
    if close_top:
        c = bm.verts.new((0, 0, profile[-1][1] + 0.0002 - ZC))
        last = rings[-1]
        for k in range(SIDES):
            bm.faces.new((last[k], last[(k + 1) % SIDES], c))
    if close_bottom:
        c = bm.verts.new((0, 0, profile[0][1] - ZC))
        first = rings[0]
        for k in range(SIDES):
            bm.faces.new((first[(k + 1) % SIDES], first[k], c))


def make_object(name, build, color, emission=0.0):
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    build(bm)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    bm.to_mesh(mesh)
    bm.free()
    mat = bpy.data.materials.get(name + "Mat") or new_material(name + "Mat", color, emission)
    mesh.materials.append(mat)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def new_material(name, color, emission):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = color
    bsdf.inputs["Roughness"].default_value = 0.85
    if emission:
        bsdf.inputs["Emission Color"].default_value = color
        bsdf.inputs["Emission Strength"].default_value = emission
    mat.diffuse_color = color
    return mat


def build_body(bm):
    lathe(bm, body_profile())


def build_trim(bm):
    # Ножка: утопленный диск под корпусом
    lathe(bm, [(0.058, 0.0), (0.0615, 0.002), (0.0625, 0.006), (0.0640, 0.0125)],
          close_bottom=True, close_top=False)
    # Сзади (+Y в Blender): USB-C у низа и кнопка микрофона повыше
    box(bm, (0.0105, 0.004, 0.0038), (0, R - 0.0012, 0.026 - ZC))
    box(bm, (0.017, 0.004, 0.0085), (0, R - 0.0010, 0.062 - ZC))


def box(bm, size, center):
    res = bmesh.ops.create_cube(bm, size=1.0)
    for v in res["verts"]:
        v.co = Vector((v.co.x * size[0] + center[0], v.co.y * size[1] + center[1], v.co.z * size[2] + center[2]))


def top_shape(bm, pts2d, z):
    """Плоская фигура на крышке (точки по часовой/против — неважно, нормали пересчитаются)."""
    verts = [bm.verts.new((x, y, z)) for x, y in pts2d]
    bm.faces.new(verts)


def build_marks(bm):
    z = H + 0.0012 - ZC
    w, t = 0.0055, 0.0007
    # «−» слева, «+» справа (вид спереди: фронт в -Y, право в +X)
    for cx in (-0.040, 0.040):
        top_shape(bm, [(cx - w / 2, -t), (cx + w / 2, -t), (cx + w / 2, t), (cx - w / 2, t)], z)
    top_shape(bm, [(0.040 - t, -w / 2), (0.040 + t, -w / 2), (0.040 + t, w / 2), (0.040 - t, w / 2)], z + 0.0001)
    # Точки спереди и сзади
    for cy in (-0.030, 0.030):
        top_shape(bm, [(0.0012 * math.cos(a), cy + 0.0012 * math.sin(a))
                       for a in (2 * math.pi * k / 10 for k in range(10))], z)


def build_ring(bm):
    z = H + 0.0014 - ZC
    r0, r1, n = 0.0068, 0.0102, 40
    inner = [bm.verts.new((r0 * math.cos(2 * math.pi * k / n), r0 * math.sin(2 * math.pi * k / n), z)) for k in range(n)]
    outer = [bm.verts.new((r1 * math.cos(2 * math.pi * k / n), r1 * math.sin(2 * math.pi * k / n), z)) for k in range(n)]
    for k in range(n):
        k2 = (k + 1) % n
        bm.faces.new((inner[k], outer[k], outer[k2], inner[k2]))


# ---------------------------------------------------------------- сегменты

def to_surface(u, v):
    """Точка на фронте корпуса: u — дуга вправо от центра, v — высота от центра цифр."""
    a = u / R
    r = R + SURFACE_OFF
    return Vector((r * math.sin(a), -r * math.cos(a), DISPLAY_Z + v - ZC))


def densify(pts, step=0.0015):
    out = []
    for p, q in zip(pts, pts[1:] + pts[:1]):
        n = max(1, int(math.dist(p, q) / step))
        out += [(p[0] + (q[0] - p[0]) * i / n, p[1] + (q[1] - p[1]) * i / n) for i in range(n)]
    return out


def segment_outline(cu, s):
    """Шестиугольник сегмента s (a..g) цифры с центром cu, в координатах (u, v)."""
    w2, h2, t2 = DIGIT_W / 2, DIGIT_H / 2, SEG_T / 2
    g = SEG_GAP
    if s in "agd":
        v = {"a": h2, "g": 0.0, "d": -h2}[s]
        u0, u1 = cu - w2 + g, cu + w2 - g
        return [(u0, v), (u0 + t2, v - t2), (u1 - t2, v - t2), (u1, v), (u1 - t2, v + t2), (u0 + t2, v + t2)]
    u = cu + (w2 if s in "bc" else -w2)
    v0, v1 = (g, h2 - g) if s in "bf" else (-h2 + g, -g)
    return [(u, v0), (u + t2, v0 + t2), (u + t2, v1 - t2), (u, v1), (u - t2, v1 - t2), (u - t2, v0 + t2)]


def build_flat(bm, outline):
    """Выпуклая фигура (u, v), натянутая на цилиндр: веер треугольников от центра."""
    pts = densify(outline)
    cu = sum(p[0] for p in outline) / len(outline)
    cv = sum(p[1] for p in outline) / len(outline)
    c = bm.verts.new(to_surface(cu, cv))
    ring = [bm.verts.new(to_surface(u, v)) for u, v in pts]
    for k in range(len(ring)):
        f = bm.faces.new((c, ring[k], ring[(k + 1) % len(ring)]))
        f.normal_update()


def circle(cu, cv, r, n=12):
    return [(cu + r * math.cos(2 * math.pi * k / n), cv + r * math.sin(2 * math.pi * k / n)) for k in range(n)]


def build_stroke(bm, path, closed=False, t=SEG_T):
    """Линия толщиной t по точкам (u, v), натянутая на цилиндр."""
    n = len(path)
    left, right = [], []
    for i, (u, v) in enumerate(path):
        p0 = path[i - 1] if (i > 0 or closed) else path[i]
        p1 = path[(i + 1) % n] if (i < n - 1 or closed) else path[i]
        du, dv = p1[0] - p0[0], p1[1] - p0[1]
        ln = math.hypot(du, dv) or 1.0
        nu, nv = -dv / ln * t / 2, du / ln * t / 2
        left.append(bm.verts.new(to_surface(u + nu, v + nv)))
        right.append(bm.verts.new(to_surface(u - nu, v - nv)))
    for i in range(n if closed else n - 1):
        j = (i + 1) % n
        bm.faces.new((left[i], left[j], right[j], right[i]))


def arc(cu, cv, ru, rv, a0, a1, n=16):
    return [(cu + ru * math.cos(a0 + (a1 - a0) * k / n), cv + rv * math.sin(a0 + (a1 - a0) * k / n))
            for k in range(n + 1)]


def mouth_paths():
    """Рот: улыбка, приоткрытый и открытый (кольца-овалы), центр между двумя средними цифрами."""
    v = MOUTH_V
    return {
        "MouthSmile": (arc(0.0, v + 0.003, 0.0085, 0.0045, math.pi * 1.1, math.pi * 1.9), False),
        "MouthHalf": (arc(0.0, v, 0.0075, 0.0028, 0, 2 * math.pi, 28)[:-1], True),
        "MouthOpen": (arc(0.0, v - 0.0005, 0.0080, 0.0050, 0, 2 * math.pi, 28)[:-1], True),
    }


def build_segments():
    objs = {}
    for d, cu in enumerate(DIGIT_U):
        for s in "abcdefg":
            name = f"Seg{d}{s}"
            objs[name] = make_object(name, lambda bm, o=segment_outline(cu, s): build_flat(bm, o),
                                     DISPLAY_COLOR, 6.0)
            objs[name].data.materials[0] = display_material()
    for name, v in (("ColonTop", 0.0055), ("ColonBottom", -0.0055)):
        objs[name] = make_object(name, lambda bm, v=v: build_flat(bm, circle(0.0, v, 0.0016)), DISPLAY_COLOR, 6.0)
        objs[name].data.materials[0] = display_material()
    for name, (path, closed) in mouth_paths().items():
        objs[name] = make_object(name, lambda bm, p=path, c=closed: build_stroke(bm, p, c), DISPLAY_COLOR, 6.0)
        objs[name].data.materials[0] = display_material()
    return objs


def display_material():
    return bpy.data.materials.get("Display") or new_material("Display", DISPLAY_COLOR, 6.0)


# ---------------------------------------------------------------- сборка

def fix_normals_outward(obj):
    """Нормали сегментов и фигур на крышке — наружу от центра колонки."""
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    for f in bm.faces:
        c = f.calc_center_median()
        out = Vector((c.x, c.y, 0)) if obj.name.startswith(("Seg", "Colon", "Mouth")) else Vector((0, 0, 1))
        if f.normal.dot(out) < 0:
            f.normal_flip()
    bm.to_mesh(me)
    bm.free()


def build():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    objs = {
        "Body": make_object("Body", build_body, BODY_COLOR),
        "Trim": make_object("Trim", build_trim, TRIM_COLOR),
        "Marks": make_object("Marks", build_marks, MARK_COLOR),
        "Ring": make_object("Ring", build_ring, RING_COLOR, 8.0),
    }
    objs.update(build_segments())
    for name, o in objs.items():
        if name in ("Marks", "Ring") or name.startswith(("Seg", "Colon", "Mouth")):
            fix_normals_outward(o)

    bpy.ops.object.select_all(action="DESELECT")
    for o in objs.values():
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs["Body"]
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(40))
    return objs


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
        add_leaf_bones=False,
        bake_anim=False,
        path_mode="STRIP",
    )
    print("saved", path)


def show_pattern(objs, patterns, colon, mouth=None):
    for d in range(4):
        for s in "abcdefg":
            objs[f"Seg{d}{s}"].hide_render = s not in patterns[d]
    objs["ColonTop"].hide_render = objs["ColonBottom"].hide_render = not colon
    for name in mouth_paths():
        objs[name].hide_render = name != mouth


def render_preview(objs, path, face=False, back=False):
    if face:
        show_pattern(objs, FACE, False, os.environ.get("MOUTH", FACE_MOUTH))
    else:
        show_pattern(objs, [DIGITS[1], DIGITS[2], DIGITS[3], DIGITS[5]], True)

    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 48
    scene.render.resolution_x, scene.render.resolution_y = 900, 700
    world = bpy.data.worlds.new("W")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.55, 0.55, 0.56, 1)
    world.node_tree.nodes["Background"].inputs[1].default_value = 0.6
    scene.world = world

    floor = bpy.data.meshes.new("Floor")
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=2)
    bm.to_mesh(floor)
    bm.free()
    fo = bpy.data.objects.new("Floor", floor)
    fo.location.z = -ZC
    floor.materials.append(new_material("FloorMat", (0.5, 0.5, 0.5, 1), 0))
    scene.collection.objects.link(fo)

    for loc, energy in (((-0.5, -0.6, 0.6), 25), ((0.6, -0.2, 0.4), 10)):
        ld = bpy.data.lights.new("L", "POINT")
        ld.energy = energy
        lo = bpy.data.objects.new("L", ld)
        lo.location = loc
        scene.collection.objects.link(lo)

    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    cam.data.lens = 70
    cam.location = (0.10, -0.42, 0.16) if not back else (-0.12, 0.42, 0.16)
    direction = Vector((0, 0, -0.005)) - cam.location
    cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    scene.collection.objects.link(cam)
    scene.camera = cam
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    objs = build()
    export()
    if "--preview" in argv:
        render_preview(objs, os.path.abspath(argv[argv.index("--preview") + 1]),
                       face="--face" in argv, back="--back" in argv)


main()

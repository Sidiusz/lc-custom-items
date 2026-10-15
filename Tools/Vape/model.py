"""
Три одноразовых вейпа: модели (FBX), текстуры корпуса и экрана, превью.

    pip install bpy pillow numpy
    python Tools/Vape/model.py                       # модели и текстуры
    python Tools/Vape/model.py --preview out.png     # плюс превью всех трёх рядом

Пишет в Assets/Sources/Art/:
    Models/Vape<Модель>/Vape<Модель>.fbx           модель из объектов по материалам (один объект — один материал)
    Textures/Vape<Модель>/Vape<Модель>Body.png     градиент корпуса (развёртка корпуса — проекция спереди)
    Textures/Vape<Модель>/Vape<Модель>Screen.png   экран по умолчанию (в игре его перерисовывает VapeScreen.cs)

Модели (по картинкам из треда, не копии):
    Pixl  коробочка с градиентом розовый -> синий, мундштук слева, ступенька-рельс справа,
          крупный семисегментный экран вверху (PYXL 8000)
    Box   округлая «мыльница» голубая -> синяя, чёрный верх и мундштук, маленький экран (VAPEFIT)
    Pod   тонкая чёрная ручка с высоким экраном, прозрачный картридж сверху, золотые полосы сзади (OXYA)

Объект Screen — прямоугольник с развёрткой 0..1 (u вправо, v вверх), на нём рисуется экран.
Объект Mouth — пустышка у мундштука: из этой точки идёт пар у предмета на полу.
Размеры в мм, итог в метрах с множителем SCALE (в руке игрока 1:1 слишком мелко).
В Blender перед — это -Y, верх — +Z; после FBX (forward -Z, up Y) в Unity перед смотрит в -Z.
"""
import math
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import screen  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MODELS = os.path.join(ROOT, "Assets", "Sources", "Art", "Models")
SCALE = 1.5
FONT_BOLD = ["/usr/share/fonts/opentype/inter/Inter-Black.otf", "/usr/share/fonts/opentype/inter/Inter-Bold.otf",
             "C:/Windows/Fonts/arialbd.ttf"]


def srgb(r, g, b, a=1.0):
    def lin(c):
        c /= 255.0
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    return (lin(r), lin(g), lin(b), a)


# ---------------------------------------------------------------- текстуры

def gradient(path, stops, h=256):
    """Вертикальный градиент: stops — [(v, (r, g, b))], v = 0 низ, 1 верх."""
    img = np.zeros((h, 8, 3), np.float32)
    for row in range(h):
        v = 1 - row / (h - 1)
        for (v0, c0), (v1, c1) in zip(stops, stops[1:]):
            if v0 <= v <= v1:
                k = (v - v0) / (v1 - v0)
                k = k * k * (3 - 2 * k)
                img[row, :] = [a + (b - a) * k for a, b in zip(c0, c1)]
    # лёгкое зерно, чтобы градиент не полосил
    img += np.random.default_rng(7).normal(0, 0.8, img.shape)
    Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).save(path)
    return path


# ---------------------------------------------------------------- сетки

def bm_box(w, d, h, r_side=0.0, r_edge=0.0, taper=(1.0, 1.0), z0=0.0):
    """Скруглённый брусок: w по X, d по Y, h по Z (от z0 вверх). r_side — радиус углов
    силуэта спереди (рёбра вдоль Y), r_edge — фаска всех рёбер. taper: (x, y) у верха."""
    import bmesh
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co.x *= w
        v.co.y *= d
        v.co.z = (v.co.z + 0.5) * h + z0
    if r_side > 0:
        edges = [e for e in bm.edges if abs((e.verts[0].co - e.verts[1].co).normalized().y) > 0.99]
        bmesh.ops.bevel(bm, geom=edges + [v for e in edges for v in e.verts], offset=r_side, segments=8,
                        profile=0.5, affect="EDGES", clamp_overlap=True)
    if r_edge > 0:
        bmesh.ops.bevel(bm, geom=list(bm.edges) + list(bm.verts), offset=r_edge, segments=3,
                        profile=0.5, affect="EDGES", clamp_overlap=True)
    if taper != (1.0, 1.0):
        for v in bm.verts:
            t = (v.co.z - z0) / h
            v.co.x *= 1 + (taper[0] - 1) * t
            v.co.y *= 1 + (taper[1] - 1) * t
    return bm


def bm_cylinder(rx, ry, h, z0=0.0, taper=1.0, r_edge=0.0, seg=32):
    import bmesh
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=seg, radius1=1.0, radius2=taper, depth=h)
    for v in bm.verts:
        v.co.x *= rx
        v.co.y *= ry
        v.co.z += h / 2 + z0
    if r_edge > 0:
        caps = [e for e in bm.edges if len(e.link_faces) == 2 and
                any(len(f.verts) > 4 for f in e.link_faces)]
        bmesh.ops.bevel(bm, geom=caps + list({v for e in caps for v in e.verts}), offset=r_edge, segments=3,
                        profile=0.5, affect="EDGES", clamp_overlap=True)
    return bm


def bm_quad(w, h, y, z):
    """Плоский прямоугольник перед корпусом (лицом в -Y), развёртка 0..1."""
    import bmesh
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    vs = [bm.verts.new((x, y, zz)) for x, zz in ((-w / 2, z - h / 2), (w / 2, z - h / 2),
                                                 (w / 2, z + h / 2), (-w / 2, z + h / 2))]
    f = bm.faces.new(vs)
    for loop, c in zip(f.loops, ((0, 0), (1, 0), (1, 1), (0, 1))):
        loop[uv].uv = c
    f.normal_update()
    if f.normal.y > 0:
        f.normal_flip()
    return bm


def front_uv(bm, bounds=None):
    """Проекция спереди: u по X, v по Z в пределах bounds (minx, maxx, minz, maxz)."""
    uv = bm.loops.layers.uv.verify()
    xs = [v.co.x for v in bm.verts]
    zs = [v.co.z for v in bm.verts]
    minx, maxx, minz, maxz = bounds or (min(xs), max(xs), min(zs), max(zs))
    for f in bm.faces:
        for loop in f.loops:
            loop[uv].uv = ((loop.vert.co.x - minx) / (maxx - minx), (loop.vert.co.z - minz) / (maxz - minz))


def split_z(bm, z):
    """Разрезать по плоскости z: вернуть (низ, верх) — два новых bmesh."""
    import bmesh
    geom = list(bm.verts) + list(bm.edges) + list(bm.faces)
    bmesh.ops.bisect_plane(bm, geom=geom, plane_co=(0, 0, z), plane_no=(0, 0, 1))
    parts = []
    for upper in (False, True):
        part = bm.copy()
        kill = [f for f in part.faces if (f.calc_center_median().z > z) != upper]
        bmesh.ops.delete(part, geom=kill, context="FACES")
        parts.append(part)
    bm.free()
    return parts


# ---------------------------------------------------------------- сборка

class Builder:
    def __init__(self, name):
        import bpy
        self.name = name
        self.objects = []
        self.mats = {}
        self.col = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(self.col)

    def material(self, key, color, rough=0.4, metal=0.0, image=None, emission=None, alpha=1.0):
        import bpy
        name = f"Vape{self.name}{key}"
        if name in self.mats:
            return self.mats[name]
        m = bpy.data.materials.new(name)
        try:
            m.use_nodes = True
        except Exception:
            pass
        nt = m.node_tree
        bsdf = nt.nodes["Principled BSDF"]
        bsdf.inputs["Base Color"].default_value = color
        bsdf.inputs["Roughness"].default_value = rough
        bsdf.inputs["Metallic"].default_value = metal
        if image:
            tex = nt.nodes.new("ShaderNodeTexImage")
            tex.image = bpy.data.images.load(image)
            nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
            if emission:
                tex.interpolation = "Closest"
                nt.links.new(tex.outputs["Color"], bsdf.inputs["Emission Color"])
                bsdf.inputs["Emission Strength"].default_value = emission
        if alpha < 1:
            bsdf.inputs["Alpha"].default_value = alpha
            bsdf.inputs["Transmission Weight"].default_value = 0.9
            bsdf.inputs["IOR"].default_value = 1.45
        self.mats[name] = m
        return m

    def add(self, obj_name, bm, mat, smooth=True):
        import bpy
        bm.normal_update()
        mesh = bpy.data.meshes.new(f"Vape{self.name}{obj_name}")
        bm.to_mesh(mesh)
        bm.free()
        mesh.materials.append(mat)
        obj = bpy.data.objects.new(obj_name, mesh)
        self.col.objects.link(obj)
        if smooth:
            for p in mesh.polygons:
                p.use_smooth = True
            try:
                bpy.context.view_layer.objects.active = obj
                obj.select_set(True)
                bpy.ops.object.shade_smooth_by_angle(angle=math.radians(40))
                obj.select_set(False)
            except Exception:
                pass
        self.objects.append(obj)
        return obj

    def text(self, obj_name, body, size, x, y, z, mat, rotate=0.0, depth=0.25):
        """Надпись, выдавленная из передней (y < 0) или задней грани."""
        import bpy
        import bmesh
        curve = bpy.data.curves.new(f"{obj_name}Text", type="FONT")
        curve.body = body
        curve.size = size
        curve.extrude = depth / 2
        curve.align_x = "CENTER"
        curve.align_y = "CENTER"
        for path in FONT_BOLD:
            if os.path.exists(path):
                curve.font = bpy.data.fonts.load(path)
                break
        tmp = bpy.data.objects.new(f"{obj_name}Tmp", curve)
        self.col.objects.link(tmp)
        back = y > 0
        tmp.rotation_euler = (math.radians(90), rotate, math.radians(180) if back else 0.0)
        tmp.location = (x, y, z)
        bpy.context.view_layer.update()
        deps = bpy.context.evaluated_depsgraph_get()
        mesh = bpy.data.meshes.new_from_object(tmp.evaluated_get(deps))
        mesh.transform(tmp.matrix_world)
        bpy.data.objects.remove(tmp)
        bm = bmesh.new()
        bm.from_mesh(mesh)
        bpy.data.meshes.remove(mesh)
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
        return self.add(obj_name, bm, mat, smooth=False)

    def empty(self, obj_name, x, y, z):
        import bpy
        e = bpy.data.objects.new(obj_name, None)
        e.location = (x, y, z)
        self.col.objects.link(e)
        self.objects.append(e)
        return e

    def finish(self):
        """Миллиметры -> метры с SCALE, центр модели в середине по высоте."""
        import bpy
        from mathutils import Matrix, Vector
        meshes = [o for o in self.objects if o.type == "MESH"]
        zs = [(o.matrix_world @ v.co).z for o in meshes for v in o.data.vertices]
        mid = (min(zs) + max(zs)) / 2
        m = Matrix.Scale(0.001 * SCALE, 4) @ Matrix.Translation(Vector((0, 0, -mid)))
        for o in self.objects:
            if o.type == "MESH":
                o.data.transform(m)
            else:
                o.location = m @ o.location
        self.height = (max(zs) - min(zs)) * 0.001 * SCALE
        bpy.context.view_layer.update()

    def export(self):
        import bpy
        bpy.ops.object.select_all(action="DESELECT")
        for o in self.objects:
            o.select_set(True)
        folder = os.path.join(MODELS, f"Vape{self.name}")
        os.makedirs(folder, exist_ok=True)
        path = os.path.join(folder, f"Vape{self.name}.fbx")
        bpy.ops.export_scene.fbx(
            filepath=path, use_selection=True, object_types={"MESH", "EMPTY"},
            apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS",
            axis_forward="-Z", axis_up="Y", bake_space_transform=True,
            use_mesh_modifiers=True, mesh_smooth_type="FACE", path_mode="STRIP",
            embed_textures=False, add_leaf_bones=False, bake_anim=False)
        tris = sum(len(p.vertices) - 2 for o in self.objects if o.type == "MESH" for p in o.data.polygons)
        print(f"{path}: {tris} треугольников, высота {self.height:.3f} м")
        bpy.ops.object.select_all(action="DESELECT")


def build_pixl(screens):
    b = Builder("Pixl")
    W, H, D = 50.0, 92.0, 25.0
    tex = gradient(os.path.join(screen.tex_dir("Pixl"), "VapePixlBody.png"),
                   [(0.0, (40, 70, 235)), (0.32, (120, 50, 220)), (0.62, (250, 50, 185)), (1.0, (255, 70, 200))])
    body_mat = b.material("Body", srgb(255, 255, 255), 0.25, image=tex)
    bounds = (-W / 2, W / 2, 0.0, H)

    body = bm_box(W, D, H, r_side=8, r_edge=3)
    front_uv(body, bounds)
    b.add("Body", body, body_mat)

    # Передняя панель чуть выступает: справа и сверху от неё видна ступенька-рельс
    panel = bm_box(W - 10, 1.6, H - 12, r_side=6, r_edge=0.6, z0=2)
    for v in panel.verts:
        v.co.x -= 3
        v.co.y -= D / 2
    front_uv(panel, bounds)
    b.add("Panel", panel, body_mat)

    # Мундштук слева, светлее корпуса
    mouth_mat = b.material("Mouthpiece", srgb(255, 90, 205), 0.3)
    mp = bm_box(22, 17, 22, r_side=6, r_edge=2.5, taper=(0.9, 0.85), z0=H - 4)
    for v in mp.verts:
        v.co.x -= 10
    b.add("Mouthpiece", mp, mouth_mat)
    hole_mat = b.material("Trim", srgb(25, 10, 25), 0.6)
    b.add("Hole", bm_cylinder(5, 2.2, 0.6, z0=H + 17.9), hole_mat, smooth=False)
    for v in b.objects[-1].data.vertices:
        v.co.x -= 10
    b.empty("Mouth", -10, 0, H + 19)

    # Экран: чёрное стекло и сам экран, крупный: в игре мелкие пиксели не читаются
    bezel_mat = b.material("Bezel", srgb(8, 8, 10), 0.1)
    bez = bm_box(36, 1.0, 28, r_side=4, r_edge=0.4, z0=48)
    for v in bez.verts:
        v.co.x -= 3
        v.co.y -= D / 2 + 1.6
    b.add("Bezel", bez, bezel_mat)
    scr = bm_quad(32, 24, -D / 2 - 2.65, 62)
    for v in scr.verts:
        v.co.x -= 3
    b.add("Screen", scr, b.material("Screen", srgb(255, 255, 255), 0.2, image=screens["Pixl"], emission=3.0),
          smooth=False)

    logo = b.material("Logo", srgb(255, 255, 255), 0.4)
    b.text("Logo", "PYXL", 13, -4, -D / 2 - 1.75, 24, logo, rotate=math.radians(-90))
    b.text("Logo8000", "8000", 9, 7, -D / 2 - 1.75, 24, logo, rotate=math.radians(-90))
    return b


def build_box(screens):
    b = Builder("Box")
    W, H, D = 50.0, 64.0, 25.0
    CAP = 50.0  # выше этого z корпус чёрный
    tex = gradient(os.path.join(screen.tex_dir("Box"), "VapeBoxBody.png"),
                   [(0.0, (30, 80, 210)), (0.55, (95, 150, 240)), (1.0, (175, 205, 250))])
    body = bm_box(W, D, H, r_side=7, r_edge=5)
    low, top = split_z(body, CAP)
    front_uv(low, (-W / 2, W / 2, 0.0, CAP))
    b.add("Body", low, b.material("Body", srgb(255, 255, 255), 0.22, metal=0.3, image=tex))
    black = b.material("Cap", srgb(14, 14, 16), 0.35)
    b.add("Cap", top, black)

    mp = bm_cylinder(8.5, 7.0, 19, z0=H - 1, taper=0.72, r_edge=1.6)
    b.add("Mouthpiece", mp, black)
    b.add("Hole", bm_cylinder(2.6, 2.0, 0.5, z0=H + 17.75), b.material("Trim", srgb(4, 4, 4), 0.8), smooth=False)
    b.empty("Mouth", 0, 0, H + 18)

    bez = bm_box(40, 1.0, 22, r_side=4, r_edge=0.4, z0=22)
    for v in bez.verts:
        v.co.y -= D / 2 - 0.6
    b.add("Bezel", bez, b.material("Bezel", srgb(8, 8, 10), 0.1))
    b.add("Screen", bm_quad(36, 18, -D / 2 - 0.45, 33),
          b.material("Screen", srgb(255, 255, 255), 0.2, image=screens["Box"], emission=3.0), smooth=False)

    logo = b.material("Logo", srgb(255, 255, 255), 0.4)
    b.text("Logo", "VAPEFIT", 6.5, 0, -D / 2 + 0.05, 12, logo)
    return b


def build_pod(screens):
    b = Builder("Pod")
    W, H, D = 27.0, 100.0, 17.0
    black = b.material("Body", srgb(16, 16, 18), 0.3, metal=0.2)
    b.add("Body", bm_box(W, D, H, r_side=5, r_edge=2.5), black)

    # Прозрачный картридж сверху, внутри тёмная трубка
    glass = b.material("Glass", srgb(200, 215, 225, 0.25), 0.05, alpha=0.25)
    b.add("Glass", bm_box(24, 15, 34, r_side=4, r_edge=2.5, taper=(0.72, 0.72), z0=H - 1), glass)
    b.add("Chimney", bm_cylinder(3.0, 3.0, 26, z0=H + 2), b.material("Chimney", srgb(140, 140, 150), 0.25, metal=0.8))
    b.add("Liquid", bm_box(20, 11, 9, r_side=3, r_edge=1, z0=H + 0.5),
          b.material("Liquid", srgb(255, 160, 40, 0.6), 0.1, alpha=0.6))
    b.add("Hole", bm_cylinder(5.5, 2.0, 0.5, z0=H + 32.8), b.material("Trim", srgb(30, 30, 34), 0.7), smooth=False)
    b.empty("Mouth", 0, 0, H + 33)

    bez = bm_box(21, 1.0, 53, r_side=3, r_edge=0.3, z0=11.5)
    for v in bez.verts:
        v.co.y -= D / 2 - 0.6
    b.add("Bezel", bez, b.material("Bezel", srgb(6, 6, 8), 0.08))
    b.add("Screen", bm_quad(18.2, 48.5, -D / 2 - 0.45, 38),
          b.material("Screen", srgb(255, 255, 255), 0.2, image=screens["Pod"], emission=3.0), smooth=False)

    # Кнопка над экраном и золотые полосы сзади
    gold = b.material("Gold", srgb(220, 175, 80), 0.25, metal=1.0)
    btn = bm_box(8, 1.2, 3.2, r_side=1.5, r_edge=0.4, z0=70)
    for v in btn.verts:
        v.co.y -= D / 2 - 0.3
    b.add("Button", btn, gold)
    for x in (-4.5, 4.5):
        s = bm_box(1.4, 0.8, H - 16, r_edge=0.3, z0=8)
        for v in s.verts:
            v.co.x += x
            v.co.y += D / 2 - 0.2
        b.add("Stripe" if x < 0 else "Stripe2", s, gold)

    logo = b.material("Logo", srgb(235, 235, 240), 0.4)
    b.text("Logo", "OXYA", 5.5, 0, -D / 2 + 0.05, 84, logo)
    return b


BUILDERS = {"Pixl": build_pixl, "Box": build_box, "Pod": build_pod}


# ---------------------------------------------------------------- превью

def render_preview(builders, path):
    import bpy
    from mathutils import Vector
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 64
    scene.render.resolution_x, scene.render.resolution_y = 1500, 900
    scene.view_settings.view_transform = "Standard"
    scene.world = bpy.data.worlds.new("World")
    scene.world.use_nodes = True
    bg = scene.world.node_tree.nodes["Background"]
    bg.inputs["Color"].default_value = (0.6, 0.62, 0.66, 1)
    bg.inputs["Strength"].default_value = 0.8

    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sun.data.energy = 3.0
    sun.rotation_euler = (math.radians(55), math.radians(5), math.radians(-30))
    scene.collection.objects.link(sun)

    # три модели в ряд, чуть повёрнуты
    for i, b in enumerate(builders):
        for o in b.objects:
            if o.parent is None:
                o.location.x += (i - 1) * 0.12
                o.rotation_euler.z = math.radians(-18)
    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    cam.data.lens = 60
    scene.collection.objects.link(cam)
    scene.camera = cam
    target = Vector((0, 0, 0.0))
    root, ext = os.path.splitext(path)
    for name, view in (("", Vector((0.18, -0.55, 0.12))), ("_back", Vector((-0.18, 0.55, 0.12)))):
        cam.location = target + view
        cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
        scene.render.filepath = f"{root}{name}{ext}"
        bpy.ops.render.render(write_still=True)
        print(scene.render.filepath)


def main():
    import bpy
    argv = sys.argv[1:]
    preview = argv[argv.index("--preview") + 1] if "--preview" in argv else None
    screens = screen.save_defaults()

    bpy.ops.wm.read_factory_settings(use_empty=True)
    builders = []
    for name, build in BUILDERS.items():
        b = build(screens)
        b.finish()
        b.export()
        builders.append(b)
    if preview:
        render_preview(builders, os.path.abspath(preview))


if __name__ == "__main__":
    main()

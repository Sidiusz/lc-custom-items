# Готовит модель плюшевого Уинстона для предмета Winston из Tools/Winston/Winton.blend:
# убирает скелет и лишние объекты, уменьшает до размера игрушки, запекает цвет
# вместе с AO (вторая UV) в одну текстуру и пишет FBX со встроенной текстурой
# в Assets/Items/Winston/.
#
# Запуск из корня репозитория (Blender или pip-пакет bpy):
#   blender -b --factory-startup -P Tools/Winston/model.py
#   python3 Tools/Winston/model.py -- --preview preview.png [--back]
#
# Размеры в метрах (1 единица Unity = 1 м). Начало координат в центре габаритов,
# лицо смотрит в +Z Unity.

import os
import sys

import bpy  # до bmesh: в pip-пакете bpy модуль bmesh появляется только после него
import bmesh
from mathutils import Matrix, Vector

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SOURCE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "Winton.blend")
OUT_DIR = os.path.join(REPO, "Assets", "Items", "Winston")
NAME = "Winston"
TEX = 1024
HEIGHT = 0.26  # высота игрушки; в исходнике сувенир 0.5 м


def load_mesh():
    bpy.ops.wm.open_mainfile(filepath=SOURCE)
    obj = bpy.data.objects["Winton"]
    for o in list(bpy.data.objects):
        if o is not obj:
            bpy.data.objects.remove(o)
    for m in list(obj.modifiers):
        obj.modifiers.remove(m)
    obj.vertex_groups.clear()
    obj.name = NAME

    # Поворот и масштаб в вершины, центр габаритов в начало координат
    me = obj.data
    me.transform(obj.matrix_world)
    obj.matrix_world = Matrix.Identity(4)
    lo = Vector(min(v.co[i] for v in me.vertices) for i in range(3))
    hi = Vector(max(v.co[i] for v in me.vertices) for i in range(3))
    scale = HEIGHT / (hi.z - lo.z)
    me.transform(Matrix.Scale(scale, 4) @ Matrix.Translation(-(lo + hi) / 2))
    me.update()
    return obj


def bake_texture(obj):
    """Цвет * AO в одну картинку на первой UV, чтобы в Unity хватило одного материала."""
    me = obj.data
    mat = me.materials[0]
    nt = mat.node_tree
    nodes = {n.name: n for n in nt.nodes}
    color = nodes["2903569922"]
    ao = nodes["3335614873"]

    image = bpy.data.images.new(NAME, TEX, TEX, alpha=False)
    mix = nt.nodes.new("ShaderNodeMix")
    mix.data_type = "RGBA"
    mix.blend_type = "MULTIPLY"
    mix.inputs["Factor"].default_value = 0.85
    nt.links.new(color.outputs["Color"], mix.inputs["A"])
    nt.links.new(ao.outputs["Color"], mix.inputs["B"])
    emit = nt.nodes.new("ShaderNodeEmission")
    nt.links.new(mix.outputs["Result"], emit.inputs["Color"])
    out = nodes["Material Output"]
    nt.links.new(emit.outputs["Emission"], out.inputs["Surface"])
    target = nt.nodes.new("ShaderNodeTexImage")
    target.image = image
    nt.nodes.active = target

    me.uv_layers.active = me.uv_layers["UVMap1"]
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 1
    scene.render.bake.margin = 8
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.bake(type="EMIT")
    image.filepath_raw = os.path.join(OUT_DIR, NAME + ".png")
    image.file_format = "PNG"
    image.save()
    image.pack()  # превью рендерится уже после удаления PNG

    # Простой материал вместо шейдера Overwatch: его FBX-экспорт не понимает
    me.materials.clear()
    plush = bpy.data.materials.new(NAME)
    plush.use_nodes = True
    bsdf = plush.node_tree.nodes["Principled BSDF"]
    tex = plush.node_tree.nodes.new("ShaderNodeTexImage")
    tex.image = image
    plush.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 0.95
    me.materials.append(plush)
    me.uv_layers.remove(me.uv_layers["UVMap2"])


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

    for loc, energy in (((-0.6, -0.8, 0.7), 40), ((0.8, 0.5, 0.5), 20)):
        ld = bpy.data.lights.new("L", "POINT")
        ld.energy = energy
        lo = bpy.data.objects.new("L", ld)
        lo.location = loc
        scene.collection.objects.link(lo)

    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    cam.location = (0.32, -0.5, 0.16) if not back else (-0.34, 0.48, 0.18)
    direction = Vector((0, 0, -0.01)) - cam.location
    cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    scene.collection.objects.link(cam)
    scene.camera = cam
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    preview = argv[argv.index("--preview") + 1] if "--preview" in argv else None

    os.makedirs(OUT_DIR, exist_ok=True)
    obj = load_mesh()
    bake_texture(obj)
    print(f"{NAME}: {len(obj.data.polygons)} граней, {len(obj.data.vertices)} вершин, "
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

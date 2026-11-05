"""CLIPBUILD clip_stills.py: CPU-only review stills (Blender, Cycles CPU, low samples). Run through Blender:
  blender.exe -b --python clip_stills.py -- <combat.glb> <outprefix> <clip:time,clip:time,...>
Renders each clip at the given time, front and side, 360x600: <outprefix>_<clip>_front.png / _side.png. No GPU device is enabled."""
import bpy, sys, math, os
from mathutils import Vector

argv = sys.argv[sys.argv.index('--') + 1:]
glb, prefix, spec = argv[0], argv[1], argv[2]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb)
sc = bpy.context.scene
sc.render.engine = 'CYCLES'; sc.cycles.device = 'CPU'; sc.cycles.samples = 24; sc.cycles.use_denoising = False
sc.render.resolution_x, sc.render.resolution_y = 360, 600; sc.render.image_settings.file_format = 'PNG'
sc.render.threads_mode = 'FIXED'; sc.render.threads = 2
w = bpy.data.worlds.new('w'); w.use_nodes = True; w.node_tree.nodes['Background'].inputs[0].default_value = (0.32, 0.33, 0.36, 1); w.node_tree.nodes['Background'].inputs[1].default_value = 1.0; sc.world = w
sun = bpy.data.objects.new('sun', bpy.data.lights.new('sun', 'SUN')); sun.data.energy = 3.5; sun.rotation_euler = (math.radians(50), 0, math.radians(-30)); sc.collection.objects.link(sun)
fill = bpy.data.objects.new('fill', bpy.data.lights.new('fill', 'SUN')); fill.data.energy = 1.6; fill.rotation_euler = (math.radians(60), 0, math.radians(150)); sc.collection.objects.link(fill)
cam = bpy.data.objects.new('cam', bpy.data.cameras.new('cam')); cam.data.lens = 50; sc.collection.objects.link(cam); sc.camera = cam
arm = [o for o in bpy.data.objects if o.type == 'ARMATURE'][0]
for o in bpy.data.objects:
    if o.type == 'MESH' and o.name.startswith(('Layer_', 'Icosphere')): o.hide_render = True
fps = sc.render.fps
def action_for(name):
    for a in bpy.data.actions:
        if a.name == name or a.name.split('|')[-1] == name or a.name.startswith(name + '_') and False: return a
    for a in bpy.data.actions:
        if name in a.name: return a
    return None
for item in spec.split(','):
    clip, t = item.split(':'); a = action_for(clip)
    if a is None: print('NO ACTION', clip, [x.name for x in bpy.data.actions][:5]); continue
    arm.animation_data_create(); arm.animation_data.action = a
    for tr in list(arm.animation_data.nla_tracks): arm.animation_data.nla_tracks.remove(tr)
    sc.frame_set(int(round(float(t) * fps)) + 1); bpy.context.view_layer.update()
    for view, loc, rot in (('front', (0, -3.6, 0.92), (math.radians(90), 0, 0)), ('side', (3.6, 0, 0.92), (math.radians(90), 0, math.radians(90)))):
        cam.location = loc; cam.rotation_euler = rot
        sc.render.filepath = '%s_%s_t%s_%s.png' % (prefix, clip, t, view); bpy.ops.render.render(write_still=True); print('wrote', sc.render.filepath)

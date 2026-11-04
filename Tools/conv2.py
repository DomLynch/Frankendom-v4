import bpy, sys
src, dst = sys.argv[sys.argv.index('--')+1:][:2]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
for o in list(bpy.data.objects):
    if o.name.startswith('Icosphere'): bpy.data.objects.remove(o)
arm = [o for o in bpy.data.objects if o.type == 'ARMATURE'][0]
print("ACTIONS", [(a.name, a.frame_range[:]) for a in bpy.data.actions])
bpy.context.scene.render.fps = 24
bpy.ops.export_scene.fbx(filepath=dst, path_mode='COPY', embed_textures=True, add_leaf_bones=False,
    bake_anim=True, bake_anim_use_all_actions=True, bake_anim_use_nla_strips=False, bake_anim_force_startend_keying=True,
    bake_anim_simplify_factor=0.0, axis_forward='-Z', axis_up='Y', object_types={'ARMATURE', 'MESH'}, use_armature_deform_only=False)

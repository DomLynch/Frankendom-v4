import bpy,sys
src,dst=sys.argv[sys.argv.index('--')+1:][:2]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
for o in list(bpy.data.objects):
    if o.name.startswith('Icosphere'): bpy.data.objects.remove(o)
arm=[o for o in bpy.data.objects if o.type=='ARMATURE']
meshes=[o for o in bpy.data.objects if o.type=='MESH']
print("ARM",[(a.name,len(a.data.bones),a.data.bones[0].name) for a in arm],"MESH",[(m.name,len(m.data.vertices),tuple(round(x,2) for x in m.dimensions)) for m in meshes])
for a in bpy.data.actions: print("ACT",a.name,a.frame_range[:])
acts=list(bpy.data.actions)
if acts:
    a=acts[0]; bpy.context.scene.frame_start=int(a.frame_range[0]); bpy.context.scene.frame_end=int(round(a.frame_range[1]))
    bpy.context.scene.render.fps=24
bpy.ops.export_scene.fbx(filepath=dst,path_mode='COPY',embed_textures=True,add_leaf_bones=False,bake_anim=True,bake_anim_use_all_actions=False,bake_anim_use_nla_strips=False,axis_forward='-Z',axis_up='Y',object_types={'ARMATURE','MESH','EMPTY'},use_armature_deform_only=False)

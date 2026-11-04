import bpy, sys
src = sys.argv[sys.argv.index('--')+1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
arm = [o for o in bpy.data.objects if o.type == 'ARMATURE'][0]
print("ARM matrix", [list(r) for r in arm.matrix_world])
def pos(n):
    pb = arm.pose.bones[n]; return tuple(round(x, 3) for x in (arm.matrix_world @ pb.head))
print("REST hips/head", pos('mixamorig:Hips'), pos('mixamorig:Head'), pos('mixamorig:LeftFoot'))
for a in bpy.data.actions:
    arm.animation_data.action = a
    for f in (0, 5):
        bpy.context.scene.frame_set(f)
        print("POSE", a.name, f, pos('mixamorig:Hips'), pos('mixamorig:Head'), pos('mixamorig:LeftFoot'))

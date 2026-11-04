import bpy, sys
src = sys.argv[sys.argv.index('--')+1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
arm = [o for o in bpy.data.objects if o.type == 'ARMATURE'][0]
a = bpy.data.actions['idle']; arm.animation_data.action = a
bpy.context.scene.frame_set(0)
for n in ['root','mixamorig:Hips','mixamorig:Spine','mixamorig:Spine1','mixamorig:Spine2','mixamorig:Neck','mixamorig:Head','mixamorig:LeftUpLeg','mixamorig:LeftLeg','mixamorig:LeftFoot']:
    pb = arm.pose.bones[n]
    print("P", n, tuple(round(x,3) for x in pb.head), "rest", tuple(round(x,3) for x in pb.bone.head_local), "parent", pb.parent.name if pb.parent else None)

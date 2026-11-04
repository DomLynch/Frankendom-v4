$B = "C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"
$S = "C:\Users\dom12\Documents\unity_test\Tools\conv.py"
$K = "C:\Users\dom12\Documents\kits_out\3d"
$U = "C:\Users\dom12\Documents\unity_test\Assets\Characters"
New-Item -ItemType Directory -Force "$U\Dracula","$U\Pitborn","$U\Clips" | Out-Null
function Conv($src,$dst){ & $B -b -P $S -- $src $dst 2>&1 | Select-String "ARM|ACT|rror" }
Conv "$K\crimson-regent\v4\assembled_v4f.glb" "$U\Dracula\Dracula.fbx"
Conv "$K\pitborn\staged\Pitborn_A_rigged.glb" "$U\Pitborn\Pitborn.fbx"
foreach($n in 'idle','walk','run','kicking','head-hit','dying'){
  $f = Get-ChildItem "$K\pitborn\clips6\*-$n.glb" | Select -First 1
  Conv $f.FullName "$U\Clips\$n.fbx"
}

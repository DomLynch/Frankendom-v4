$p = "C:\Users\dom12\Documents\unity_test_hdrp"
$exe = "$p\Builds\HDRP_v47\HadesSlice.exe"
$root = (New-Item -ItemType Directory -Force "$p\Shots\v47").FullName

$cams = @(
  @('cam_gate_flat','0,1.6,8,0,1.2,26,60'),
  @('cam_doorway','0.5,1.7,10.5,0,4.5,24,62'),
  @('cam_minusz','0,3.5,9,0,2.5,-40,55')
)
foreach($c in $cams) {
  $o = (New-Item -ItemType Directory -Force "$root\$($c[0])").FullName
  $q = Start-Process $exe -ArgumentList "-screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -hero -cam:$($c[1]) -camname:$($c[0]) -out:$o" -PassThru
  $q.WaitForExit(30000) | Out-Null
  if(!$q.HasExited){ $q.Kill() }
}

$o = (New-Item -ItemType Directory -Force "$root\game").FullName
$q = Start-Process $exe -ArgumentList "-screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -autoplay -out:$o" -PassThru
$q.WaitForExit(60000) | Out-Null
if(!$q.HasExited){ $q.Kill() }

"shots_done" | Set-Content "$root\_shots_done.txt"

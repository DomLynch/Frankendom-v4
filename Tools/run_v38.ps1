$p="C:\Users\dom12\Documents\unity_test_hdrp"; $u="C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe"
New-Item -ItemType Directory -Force "$p\Tools\_hold_v36c" | Out-Null
foreach($pair in @(@("Assets\Scenes\HadesSlice.unity","HadesSlice.unity"),@("Assets\HDRP\Global.asset","Global.asset"),@("Assets\Data\hdrp_fixes.json","hdrp_fixes.json"))){ Copy-Item "$p\$($pair[0])" "$p\Tools\_hold_v36c\$($pair[1])" -Force }
Copy-Item "$p\Tools\_backup_mhhead_1813\HadesSlice.unity" "$p\Assets\Scenes\HadesSlice.unity" -Force; Copy-Item "$p\Tools\_backup_mhhead_1813\Global.asset" "$p\Assets\HDRP\Global.asset" -Force; Copy-Item "$p\Tools\hdrp_fixes_v38.json" "$p\Assets\Data\hdrp_fixes.json" -Force
Set-Content "$p\Tools\build_dir.txt" 'Builds/HDRP_v38'
$pr=Start-Process $u -ArgumentList '-batchmode','-quit','-projectPath',$p,'-executeMethod','HdrpBatch.FixesBuild','-logFile',"$p\TestOut\v38_Build.log" -WindowStyle Hidden -PassThru; $pr | Wait-Process
Copy-Item "$p\Tools\_hold_v36c\HadesSlice.unity" "$p\Assets\Scenes\HadesSlice.unity" -Force; Copy-Item "$p\Tools\_hold_v36c\Global.asset" "$p\Assets\HDRP\Global.asset" -Force; Copy-Item "$p\Tools\_hold_v36c\hdrp_fixes.json" "$p\Assets\Data\hdrp_fixes.json" -Force
Set-Content "$p\Tools\build_dir.txt" 'Builds/HDRP_v36c'
"exit=$($pr.ExitCode)" | Set-Content "$p\TestOut\v38_done.txt"

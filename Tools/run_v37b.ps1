$p="C:\Users\dom12\Documents\unity_test_hdrp"; $u="C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe"; $res=@()
Set-Content "$p\Tools\build_dir.txt" 'Builds/HDRP_v36c'
$pr=Start-Process $u -ArgumentList '-batchmode','-quit','-projectPath',$p,'-executeMethod','HdrpBatch.Build','-logFile',"$p\TestOut\v36c_Build.log" -WindowStyle Hidden -PassThru; $pr | Wait-Process; $res+="v36c exit=$($pr.ExitCode)"
if($pr.ExitCode -eq 0){
 Copy-Item "$p\Assets\Scenes\HadesSlice.unity" "$p\Tools\_hold_v36b\HadesSlice.unity" -Force; Copy-Item "$p\Assets\HDRP\Global.asset" "$p\Tools\_hold_v36b\Global.asset" -Force
 Copy-Item "$p\Tools\_backup_mhhead_1813\HadesSlice.unity" "$p\Assets\Scenes\HadesSlice.unity" -Force; Copy-Item "$p\Tools\_backup_mhhead_1813\Global.asset" "$p\Assets\HDRP\Global.asset" -Force
 Set-Content "$p\Tools\build_dir.txt" 'Builds/HDRP_v37'
 $pr=Start-Process $u -ArgumentList '-batchmode','-quit','-projectPath',$p,'-executeMethod','HdrpBatch.Build','-logFile',"$p\TestOut\v37_Build.log" -WindowStyle Hidden -PassThru; $pr | Wait-Process; $res+="v37 exit=$($pr.ExitCode)"
 Copy-Item "$p\Tools\_hold_v36b\HadesSlice.unity" "$p\Assets\Scenes\HadesSlice.unity" -Force; Copy-Item "$p\Tools\_hold_v36b\Global.asset" "$p\Assets\HDRP\Global.asset" -Force
}
Set-Content "$p\Tools\build_dir.txt" 'Builds/HDRP_v36c'
$res | Set-Content "$p\TestOut\v37b_done.txt"

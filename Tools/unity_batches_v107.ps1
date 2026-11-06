$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
foreach($m in 'Zone2Build.RunAndSave','HdrpCharMats.Run'){
  $n=$m.Replace('.','_')
  $b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod $m -logFile $p\TestOut\v107_$n.log" -PassThru; if(-not $b.WaitForExit(1800000)){ $b.Kill() }
  Add-Content TestOut\v107_batches.txt "$m exit=$($b.ExitCode)"
}
Set-Content TestOut\v107_batches_done.txt done

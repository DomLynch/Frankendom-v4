$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v107_z2_done.txt -ErrorAction SilentlyContinue
$b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod Zone2BuildBatch.Run -logFile $p\TestOut\v107_Zone2BuildBatch.log" -PassThru; if(-not $b.WaitForExit(1800000)){ $b.Kill() }
Set-Content TestOut\v107_z2_done.txt "exit=$($b.ExitCode)"

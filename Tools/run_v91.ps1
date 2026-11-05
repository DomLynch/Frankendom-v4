$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v91_done.txt -ErrorAction SilentlyContinue
Set-Content Tools\build_dir.txt 'Builds/HDRP_v91'
$b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod HdrpBatch.V51 -logFile $p\TestOut\v91_Build.log" -PassThru; if(-not $b.WaitForExit(1500000)){ $b.Kill() }
Set-Content TestOut\v91_done.txt "build=$($b.ExitCode)"











































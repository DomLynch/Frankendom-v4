$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v84_joan_done.txt -ErrorAction SilentlyContinue
$b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod JoanSwap.RunAllBatch -logFile $p\TestOut\v84_Joan.log" -PassThru; if(-not $b.WaitForExit(1200000)){ $b.Kill() }
Set-Content TestOut\v84_joan_done.txt "joan=$($b.ExitCode)"
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v84.ps1
if(Test-Path "$p\Builds\HDRP_v84\HadesSlice.exe"){ & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v84 } else { Set-Content "$p\Builds\v84_BUILD_FAILED.txt" "build produced no exe" }


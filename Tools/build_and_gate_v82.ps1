$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v82_joan_done.txt -ErrorAction SilentlyContinue
$b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod JoanSwap.RunAllBatch -logFile $p\TestOut\v82_Joan.log" -PassThru; if(-not $b.WaitForExit(1200000)){ $b.Kill() }
Set-Content TestOut\v82_joan_done.txt "joan=$($b.ExitCode)"
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v82.ps1
if(Test-Path "$p\Builds\HDRP_v82\HadesSlice.exe"){ & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v82 } else { Set-Content "$p\Builds\v82_BUILD_FAILED.txt" "build produced no exe" }

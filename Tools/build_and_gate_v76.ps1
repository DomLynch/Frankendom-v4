$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v76_joan_done.txt -ErrorAction SilentlyContinue
$b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod JoanSwap.RunAllBatch -logFile $p\TestOut\v76_Joan.log" -PassThru; if(-not $b.WaitForExit(1200000)){ $b.Kill() }
Set-Content TestOut\v76_joan_done.txt "joan=$($b.ExitCode)"
cmd /c "python Tools\bake_attack_clips.py Joan > TestOut\v76_bake_attack.log 2>&1"
cmd /c "python Tools\bake_dodge_clips.py Joan > TestOut\v76_bake_dodge.log 2>&1"
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v76.ps1
if(Test-Path "$p\Builds\HDRP_v76\HadesSlice.exe"){ & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v76 } else { Set-Content "$p\Builds\v76_BUILD_FAILED.txt" "build produced no exe" }

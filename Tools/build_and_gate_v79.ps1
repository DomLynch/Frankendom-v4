$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v79_joan_done.txt -ErrorAction SilentlyContinue
$b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod JoanSwap.RunAllBatch -logFile $p\TestOut\v79_Joan.log" -PassThru; if(-not $b.WaitForExit(1200000)){ $b.Kill() }
Set-Content TestOut\v79_joan_done.txt "joan=$($b.ExitCode)"
cmd /c "python Tools\bake_attack_clips.py Joan > TestOut\v79_bake_attack.log 2>&1"
cmd /c "python Tools\bake_dodge_clips.py Joan > TestOut\v79_bake_dodge.log 2>&1"
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v79.ps1
if(Test-Path "$p\Builds\HDRP_v79\HadesSlice.exe"){ & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v79 } else { Set-Content "$p\Builds\v79_BUILD_FAILED.txt" "build produced no exe" }




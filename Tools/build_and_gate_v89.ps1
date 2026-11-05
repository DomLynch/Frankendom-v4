$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v89_fin_done.txt -ErrorAction SilentlyContinue
$b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod FinisherClipSetup.RunBatch -logFile $p\TestOut\v89_FinClips.log" -PassThru; if(-not $b.WaitForExit(1500000)){ $b.Kill() }
Set-Content TestOut\v89_fin_done.txt "fin=$($b.ExitCode)"
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v89.ps1
if((Test-Path "$p\Builds\HDRP_v89\HadesSlice.exe") -and (Test-Path "$p\Builds\HDRP_v89\HadesSlice_Data\StreamingAssets") -and -not (Select-String -Path "$p\TestOut\v89_Build.log" -Pattern "Build Finished, Result: Failure" -Quiet)){
  & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v89 -proofs loop
} else { Set-Content "$p\Builds\v89_BUILD_FAILED.txt" "build produced no exe or build failed" }

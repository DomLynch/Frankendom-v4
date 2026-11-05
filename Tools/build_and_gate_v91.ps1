$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v91.ps1
if((Test-Path "$p\Builds\HDRP_v91\HadesSlice.exe") -and (Test-Path "$p\Builds\HDRP_v91\HadesSlice_Data\StreamingAssets") -and -not (Select-String -Path "$p\TestOut\v91_Build.log" -Pattern "Build Finished, Result: Failure" -Quiet)){
  & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v91 -proofs loop
} else { Set-Content "$p\Builds\v91_BUILD_FAILED.txt" "build produced no exe or build failed" }

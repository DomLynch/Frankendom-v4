$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v93.ps1
if((Test-Path "$p\Builds\HDRP_v93\HadesSlice.exe") -and (Test-Path "$p\Builds\HDRP_v93\HadesSlice_Data\StreamingAssets") -and -not (Select-String -Path "$p\TestOut\v93_Build.log" -Pattern "Build Finished, Result: Failure" -Quiet)){
  & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v93 -proofs loop
} else { Set-Content "$p\Builds\v93_BUILD_FAILED.txt" "build produced no exe or build failed" }


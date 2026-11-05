$p='C:\Users\dom12\Documents\unity_test_hdrp'
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v80.ps1
if(Test-Path "$p\Builds\HDRP_v80\HadesSlice.exe"){ & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v80 } else { Set-Content "$p\Builds\v80_BUILD_FAILED.txt" "build produced no exe" }


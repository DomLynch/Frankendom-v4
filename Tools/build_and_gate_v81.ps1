$p='C:\Users\dom12\Documents\unity_test_hdrp'
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v81.ps1
if(Test-Path "$p\Builds\HDRP_v81\HadesSlice.exe"){ & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v81 } else { Set-Content "$p\Builds\v81_BUILD_FAILED.txt" "build produced no exe" }



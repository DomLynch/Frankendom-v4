$p='C:\Users\dom12\Documents\unity_test_hdrp'
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v83.ps1
if(Test-Path "$p\Builds\HDRP_v83\HadesSlice.exe"){ & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v83 -proofs finisher } else { Set-Content "$p\Builds\v83_BUILD_FAILED.txt" "build produced no exe" }

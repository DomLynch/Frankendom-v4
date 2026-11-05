$p='C:\Users\dom12\Documents\unity_test_hdrp'
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v87.ps1
if(Test-Path "$p\Builds\HDRP_v87\HadesSlice.exe"){ & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v87 -proofs loop } else { Set-Content "$p\Builds\v87_BUILD_FAILED.txt" "build produced no exe" }


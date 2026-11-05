$p='C:\Users\dom12\Documents\unity_test_hdrp'
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v69.ps1
if(Test-Path "$p\Builds\HDRP_v69\HadesSlice.exe"){ & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v69 -proofs flow } else { Set-Content "$p\Builds\v69_BUILD_FAILED.txt" "build produced no exe" }

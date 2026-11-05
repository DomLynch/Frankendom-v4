$p='C:\Users\dom12\Documents\unity_test_hdrp'
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v68.ps1
if(Test-Path "$p\Builds\HDRP_v68\HadesSlice.exe"){ & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v68 -proofs cam } else { Set-Content "$p\Builds\v68_BUILD_FAILED.txt" "build produced no exe" }

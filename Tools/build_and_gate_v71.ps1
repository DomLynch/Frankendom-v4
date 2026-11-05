$p='C:\Users\dom12\Documents\unity_test_hdrp'
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v71.ps1
if(Test-Path "$p\Builds\HDRP_v71\HadesSlice.exe"){ & powershell -ExecutionPolicy Bypass -File $p\Tools\gate.ps1 -v v71 } else { Set-Content "$p\Builds\v71_BUILD_FAILED.txt" "build produced no exe" }

$p='C:\Users\dom12\Documents\unity_test_hdrp'
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v75.ps1
if(Test-Path "$p\Builds\HDRP_v75\HadesSlice.exe"){ & powershell -ExecutionPolicy Bypass -File $p\Tools\gate.ps1 -v v75 } else { Set-Content "$p\Builds\v75_BUILD_FAILED.txt" "build produced no exe" }

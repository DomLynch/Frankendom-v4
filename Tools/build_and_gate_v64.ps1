$p='C:\Users\dom12\Documents\unity_test_hdrp'
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v64.ps1
& powershell -ExecutionPolicy Bypass -File $p\Tools\gate.ps1 -v v64

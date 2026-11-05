$p='C:\Users\dom12\Documents\unity_test_hdrp'
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v67.ps1
& powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v67 -proofs loop

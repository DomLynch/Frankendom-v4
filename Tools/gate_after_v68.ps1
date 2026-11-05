$p='C:\Users\dom12\Documents\unity_test_hdrp'
while(-not (Test-Path "$p\TestOut\v68_done.txt")){ Start-Sleep 10 }
if((Get-Content "$p\TestOut\v68_done.txt") -match 'build=0'){ & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v68 -proofs cam }

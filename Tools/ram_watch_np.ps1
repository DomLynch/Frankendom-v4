$f='C:\Users\dom12\Documents\unity_test_hdrp\TestOut\v109np_ram.txt'; Remove-Item $f -EA SilentlyContinue
while(-not (Test-Path 'C:\Users\dom12\Documents\unity_test_hdrp\TestOut\v109np_done.txt')){ Add-Content $f ("{0} freeGB {1}" -f (Get-Date -Format 'HH:mm:ss'),[math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB,1)); Start-Sleep 30 }

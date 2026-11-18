# unity-dev: stops MY chain (chain_timed.ps1 + project Unity) if C: free < 20 GB. Logs to v109n_timing.txt
param([double]$MinGB=20,[string]$Name='v109n')
$log="$($env:FK_ROOT)\_agents\unitydev\${Name}_timing.txt"
while($true){
  Start-Sleep 5
  $f=(Get-PSDrive C).Free/1GB
  $chain=Get-CimInstance Win32_Process | ? { $_.CommandLine -like '*chain_timed.ps1*' -and $_.Name -eq 'powershell.exe' }
  if(-not $chain){ exit 0 }
  if($f -lt $MinGB){
    $chain | % { Stop-Process -Id $_.ProcessId -Force }
    Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | ? { $_.CommandLine -like '*unity_test_hdrp*' } | % { Stop-Process -Id $_.ProcessId -Force }
    Get-CimInstance Win32_Process | ? { $_.CommandLine -like '*build_v109n.ps1*' } | % { Stop-Process -Id $_.ProcessId -Force }
    (Get-Date -f HH:mm:ss)+(" DISK GUARD: free {0:N1} GB < $MinGB, chain stopped" -f $f) | Add-Content $log
    exit 3
  }
}

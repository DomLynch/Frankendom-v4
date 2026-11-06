$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'; Set-Location $p
$log="$p\TestOut\v100_watcher.log"; function L($m){ Add-Content $log ("{0} {1}" -f (Get-Date -Format 'HH:mm:ss'),$m) }
L 'watcher started: polling unity auth status every 60 s (max 10 h)'
$t0=Get-Date
while(((Get-Date)-$t0).TotalHours -lt 10){
  $null = & unity --no-banner --non-interactive auth status 2>&1; $ok = ($LASTEXITCODE -eq 0)
  if(-not $ok){ Start-Sleep 60; continue }
  L 'signed in: waiting for any HadesSlice/Unity to finish, then JoanSwap batch'
  while(Get-Process HadesSlice -ErrorAction SilentlyContinue){ Start-Sleep 15 }
  Add-Content "$p\COORDINATION.md" ("`n{0} unity-dev WATCHER: Unity signed in -> running JoanSwap batch then the v100 build+gate chain. DO NOT WRITE ASSETS until Builds\HDRP_v100\chain_done.txt (or Builds\v100_SWAP_FAILED.txt / v100_BUILD_FAILED.txt)." -f (Get-Date -Format 'HH:mm'))
  Remove-Item "$p\TestOut\v100_JoanSwap.log" -ErrorAction SilentlyContinue
  $b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod JoanSwap.RunAllBatch -logFile $p\TestOut\v100_JoanSwap.log" -PassThru; if(-not $b.WaitForExit(1800000)){ $b.Kill(); L 'swap batch timeout killed' }
  $txt = if(Test-Path "$p\TestOut\v100_JoanSwap.log"){ Get-Content "$p\TestOut\v100_JoanSwap.log" -Raw } else { '' }
  L ("swap batch exit=$($b.ExitCode)")
  if($txt -match 'No valid Unity Editor license'){ L 'still no license: keep polling'; Start-Sleep 60; continue }
  if(($txt -match 'JOANSWAP materials assigned') -and ($txt -notmatch 'JOANSWAP no [^\r\n]* yet') -and ($txt -notmatch 'error CS')){
    L 'swap OK: starting build_and_gate_v100.ps1'
    & powershell -ExecutionPolicy Bypass -File "$p\Tools\build_and_gate_v100.ps1"
    L 'chain finished'; break
  } else {
    Set-Content "$p\Builds\v100_SWAP_FAILED.txt" "JoanSwap batch did not report materials assigned (exit $($b.ExitCode)); see TestOut\v100_JoanSwap.log"
    L 'swap FAILED: stopped, needs a look'; break
  }
}
L 'watcher ended'

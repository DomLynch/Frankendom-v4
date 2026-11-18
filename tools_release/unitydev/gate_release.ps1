# gate_release.ps1 (unity-dev2): release a build ONLY after a green smoke. Usage: gate_release.ps1 -Build HDRP_v109xx
# boot_check up to 3 times (post-deploy 1-2 s flake needs a rerun); release.ps1 -Apply runs only if one run says SMOKE PASS; refuses while a HadesSlice runs.
param([Parameter(Mandatory=$true)][string]$Build)
$P=$env:FK_ROOT; Set-Location $P
if (Get-Process HadesSlice -ErrorAction SilentlyContinue) { Write-Host 'ABORT: a HadesSlice is running'; exit 3 }

$pd = & powershell -NoProfile -File "$P\_agents\unitydev\prod_data_check.ps1" -Build $Build 2>&1 | Out-String; Write-Host $pd.Trim(); if ($LASTEXITCODE -ne 0) { Write-Host 'NO RELEASE: production data check failed (leak)'; exit 6 }
$ok=$false
for($i=1;$i -le 3;$i++){
  Start-Sleep -Seconds 20
  $o = & powershell -NoProfile -File "$P\_agents\unitydev\boot_check.ps1" -Build $Build -SaveDir "$P\_agents\laneGPUAUDIT\gate_save_$Build" -Secs 25 2>&1 | Select-Object -Last 2
  Write-Host ("smoke try $i : " + ($o -join ' | '))
  if (($o -join ' ') -match 'SMOKE PASS') { $ok=$true; break }
}
if (-not $ok) { Write-Host 'NO RELEASE: smoke never passed'; exit 2 }
& "$P\_agents\unitydev\release.ps1" -Build $Build -TreeAhead -Apply 2>&1 | Select-Object -Last 12 | Select-Object -First 9
$pl = (Get-Content "$P\Builds\PLAY.bat" -Raw); $pp = (Get-Content "$P\Builds\PLAY_PREV.bat" -Raw)
Write-Host ("VERIFY PLAY.bat -> " + ([regex]::Match($pl,"HDRP_v109\w+").Value) + " | PLAY_PREV.bat -> " + ([regex]::Match($pp,"HDRP_v109\w+").Value))
if ($pl -notmatch [regex]::Escape($Build)) { Write-Host "RELEASE FAILED: PLAY.bat does not point to $Build"; exit 5 }


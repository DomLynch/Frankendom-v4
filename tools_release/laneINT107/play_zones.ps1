# laneINT107 play_zones.ps1: launch a build with the one zone flag. Usage: .\play_zones.ps1 -Build C:\...\Builds\<v>\HadesSlice.exe [-Off] [-Fresh]
#   default  : Game.exe -zones            zones + chase + zone span ON for this run (the json files stay enabled:false)
#   -Off     : Game.exe -nozones          everything off even if the files say true
#   -Fresh   : deletes zones_save.json first (Pitborn alive, gate shut again)
param([Parameter(Mandatory=$true)][string]$Build,[switch]$Off,[switch]$Fresh,[string]$SaveDir = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Dom Games\Frankendom'))
if(-not (Test-Path $Build)){ Write-Host "no such exe: $Build"; exit 2 }
# COO 18 Nov (outside audit P2): -Fresh used to delete EVERY zones_save.json anywhere under LocalLow (other products, QA harness saves).
# Now only Dom's own Frankendom save, by exact path, renamed to .bak (recoverable) instead of deleted.
if($Fresh){ $zs = Join-Path $SaveDir 'zones_save.json'; if (Test-Path -LiteralPath $zs -PathType Leaf) { $bk = "$zs.bak_$(Get-Date -Format yyyyMMdd_HHmmss)"; Move-Item -LiteralPath $zs -Destination $bk -Force; Write-Host "Fresh start: moved $zs -> $bk (move it back to recover). No other folder touched." } else { Write-Host "Fresh start: no zones_save.json in $SaveDir; nothing to reset. No other folder touched." } }
$flag = if($Off){'-nozones'}else{'-zones'}
# DOM-RUNS LOG (COO 16:5x, revert = copy the .bak_pre_domlog file back): Dom's real runs write a timestamped game log (no spaces in the path) so they become evidence; keeps the newest 30.
$domRuns = "$env:FK_ROOT\dom-runs"; New-Item -ItemType Directory -Force $domRuns | Out-Null; Get-ChildItem $domRuns -Filter *.log -EA SilentlyContinue | Sort LastWriteTime -Descending | Select -Skip 30 | Remove-Item -Force -EA SilentlyContinue
$domLog = Join-Path $domRuns ('frankendom_' + (Get-Date -f 'yyyyMMdd_HHmmss') + '.log')
Start-Process -FilePath $Build -ArgumentList @($flag,'-quality:ultra2k','-screen-width','2560','-screen-height','1600','-screen-fullscreen','0','-logFile',$domLog)

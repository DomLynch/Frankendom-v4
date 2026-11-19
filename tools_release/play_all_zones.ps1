# unity-dev: WALK zones 4-7 without killing each boss first. Own save folder (Dom's real save is never touched), every sticky boss-unlock gate unlocked (read from the build's zones.json).
# usage: play_all_zones.ps1 -Build <...\HadesSlice.exe> [-Reset] [-Zone z5_humbaba] [-Spot arrive_from_prev]
param([Parameter(Mandatory=$true)][string]$Build, [switch]$Windowed, [switch]$Reset, [string]$Zone = 'z5_humbaba', [string]$Spot = 'arrive_from_prev', [switch]$NoLaunch)
if(-not (Test-Path -LiteralPath $Build)){ Write-Host "no such exe: $Build"; exit 2 }
$dir = "$env:LOCALAPPDATA\FrankendomAllZones"   # NO SPACES: Start-Process passes ArgumentList items unquoted, a path with a space was cut at the space ('...\LocalLow\Dom') and the game never read this save
New-Item -ItemType Directory -Force $dir | Out-Null
$sa = Join-Path (Split-Path -Parent $Build) 'HadesSlice_Data\StreamingAssets'
$z = [IO.File]::ReadAllText((Join-Path $sa 'zones.json')) | ConvertFrom-Json
$gates = @(); foreach($q in $z.zones){ foreach($g in $q.gates){ if($g.sticky -and $g.unlockKilledSpawn){ $gates += $g.id } } }
$save = Join-Path $dir 'zones_save.json'
if($Reset -or -not (Test-Path $save)){
  $o = [ordered]@{ version = 1; savedUtc = (Get-Date).ToUniversalTime().ToString('o'); player = [ordered]@{ zone = $Zone; spot = $Spot; hpFrac = 1.0; has = $true }; spawns = @(); gates = $gates }
  [IO.File]::WriteAllText($save, ($o | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
  Write-Host "new sandbox save: $save  (gates unlocked: $($gates -join ', '))"
}
if($NoLaunch){ exit 0 }
# DOM-RUNS LOG (COO 16:5x, revert = copy the .bak_pre_domlog file back): Dom's real runs write a timestamped game log (no spaces in the path) so they become evidence; keeps the newest 30.
$domRuns = 'C:\Users\dom12\Documents\unity_test_hdrp\dom-runs'; New-Item -ItemType Directory -Force $domRuns | Out-Null; Get-ChildItem $domRuns -Filter *.log -EA SilentlyContinue | Sort LastWriteTime -Descending | Select -Skip 30 | Remove-Item -Force -EA SilentlyContinue
$domLog = Join-Path $domRuns ('frankendom_' + (Get-Date -f 'yyyyMMdd_HHmmss') + '.log')
. (Join-Path $PSScriptRoot 'display.ps1'); $res = Get-NativeRes
$scr = if ($Windowed) { @('-screen-width','2560','-screen-height','1600','-screen-fullscreen','0') } elseif (Test-SavedRes $dir) { @() } else { @('-screen-width',[string]$res[0],'-screen-height',[string]$res[1],'-screen-fullscreen','1','-window-mode','borderless') }   # a saved resolution = no size args (the player's choice survives)
Start-Process -FilePath $Build -ArgumentList (@('-zones','-quality:ultra2k') + $scr + @('-savePath',$dir,'-logFile',$domLog))

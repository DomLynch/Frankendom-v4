# f5plain PROOF (bandits dev, file-only; NOT run by me). Run by unity-dev on a QA COPY of the new build with the whole bundle (incl. f5plain) applied, inside a Frankendom FOCUS window.
# A plain HadesSlice.exe launch (NO -zones / -nozones / -quality flags) must behave like PLAY.bat: zones + hub + zone span on, Hades gate opening, save loads.
# Only non-behaviour flags are passed: -savePath (sandbox so Dom's real save is never touched), window size, -logFile. Kills ONLY the pid it started.
# Usage: powershell -File plain_proof.ps1 -Build C:\...\Builds\<QA copy>\HadesSlice.exe [-Seconds 70] [-Sandbox C:\...\FrankendomSmoke\plain1]
param([Parameter(Mandatory=$true)][string]$Build, [int]$Seconds = 70, [string]$Sandbox = "$env:LOCALAPPDATA\FrankendomSmoke\plainproof")
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Build)) { Write-Host "no such exe: $Build"; exit 2 }
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..'); $live = @(); foreach ($b in Get-ChildItem (Join-Path $root 'Builds') -Filter 'PLAY*.bat') { $live += [regex]::Matches((Get-Content $b.FullName -Raw), 'HDRP_[A-Za-z0-9_]+') | ForEach-Object { $_.Value } }
foreach ($n in ($live | Sort-Object -Unique)) { if ($Build -like "*\$n\*") { Write-Host "REFUSED: $Build is inside $n, named by a PLAY*.bat launcher. Use a QA copy."; exit 3 } }
New-Item -ItemType Directory -Force $Sandbox | Out-Null; $log = Join-Path $Sandbox 'plain_game.log'; if (Test-Path $log) { Remove-Item $log -Force }
$procArgs = @('-savePath', $Sandbox, '-screen-width', '1600', '-screen-height', '1000', '-screen-fullscreen', '0', '-logFile', $log)
$pr = Start-Process -FilePath $Build -ArgumentList $procArgs -PassThru; Write-Host "started pid $($pr.Id): $Build $($procArgs -join ' ')"
Start-Sleep -Seconds $Seconds
if (-not $pr.HasExited) { Stop-Process -Id $pr.Id -Force; Write-Host "stopped own pid $($pr.Id)" }
$t = if (Test-Path $log) { Get-Content $log -Raw } else { '' }; $fail = 0
function Chk($name, $ok, $detail) { if ($ok) { Write-Host "PASS  $name  $detail" } else { Write-Host "FAIL  $name  $detail"; $script:fail++ } }
Chk 'log written' ($t.Length -gt 1000) "($($t.Length) chars) $log"
Chk 'plain launch (no launcher flag)' (($t -notmatch '\[Zones\] launcher flag') -and ($t -notmatch '-nozones')) '[Zones] launcher flag must NOT appear: the DATA turned zones on'
Chk 'zones on, Hades gate start' ($t -match '\[Zones\] on: zone pit_yard spot arrive_start') 'expect [Zones] on: zone pit_yard spot arrive_start (fresh sandbox save)'
Chk 'zone span bound' ($t -match '\[ZoneSpan\] bound: 7 zones') 'expect [ZoneSpan] bound: 7 zones'
Chk 'hub / market present' (($t -match '\[Hub\]') -or ($t -match '\[Market\]')) 'expect [Hub] or [Market] lines'
Chk 'save sandbox honoured' ($t -match '\[SavePaths\] save dir redirected') 'expect [SavePaths] save dir redirected (Dom''s real save untouched)'
Chk 'a boss engages or arena live' (($t -match '\[Zones\] engage') -or ($t -match '\[BanditY\]|\[Bandits\]')) 'expect a [Zones] engage / [Bandits] line within the window (the first fight is on the gate path)'
$ex = ([regex]::Matches($t, 'Exception')).Count; Chk 'no exceptions' ($ex -eq 0) "($ex 'Exception' lines)"
Write-Host "RESULT: $(if ($fail -eq 0) { 'CLEAN: keep f5plain' } else { "$fail FAIL: revert ONLY f5plain (python -I _agents\laneLG\bundle\f5kit_lx.py f5plain revert --go) and ship the rest" })"
Write-Host "Also take ONE frame of the opening (pit yard, Joan armoured) and compare with a PLAY.bat launch of the same build: same HUD, same camera, same zone. A human check for the menu / hint text is still needed."
exit $fail

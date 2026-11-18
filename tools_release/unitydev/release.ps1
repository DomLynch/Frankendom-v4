# unity-build release.ps1: make a finished build LIVE (launchers), with the quick gate. DRY RUN unless -Apply. Run ONLY after the Lead's PASS (or his posted 6-min rule). Never deploys/uploads, never deletes anything but the prune keep-last-3 (live + PREV + launcher targets protected).
#   .\release.ps1 -Build HDRP_v109bm [-Apply]
# What it does: gate (data_gates --added-keys-ok, md5 Assets\Data json == build StreamingAssets, no game running, exe present) -> PLAY.bat / PLAY_ZONES.bat / PLAY_ALL_ZONES.bat / PLAY_FINAL_BOSS.bat point to -Build,
#   PLAY_PREV.bat points to the build that PLAY.bat pointed to before -> prune_builds.ps1 (keep last 3) -> board line. Roll-back: run again with -Build <old live> or edit PLAY_PREV.
param([Parameter(Mandatory = $true)][string]$Build, [switch]$Apply, [switch]$TreeAhead)   # -TreeAhead: the tree already carries later kits (next chain), so only list the md5 differences instead of blocking (hot-copy releases from a build ahead of the tree)
$P = $env:FK_ROOT; Set-Location $P; $B = "$P\Builds"
function Board($s) { if ($Apply) { Add-Content "$P\_agents\COORDINATION.md" ("`n" + (Get-Date -f 'HH:mm') + ' unity-build ' + $s) } }
$cur = ([regex]::Match((Get-Content "$B\PLAY.bat" -Raw), 'HDRP_\w+')).Value
$bad = @()
if (-not (Test-Path "$B\$Build\HadesSlice.exe")) { $bad += "no exe in Builds\$Build" }
# Dom 17 Nov 09:57 (via Strategy+COO): a running game no longer blocks a flip. Only refuse when a running HadesSlice was started FROM the build folder being made live (never overwrite the folder his game runs from).
if (Get-CimInstance Win32_Process -Filter "Name='HadesSlice.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -and $_.ExecutablePath -like "$B\$Build\*" }) { $bad += 'a game is running from this build folder' }
$pdc = & powershell -NoProfile -File "$P\_agents\unitydev\prod_data_check.ps1" -Build $Build 2>&1 | Out-String; if ($LASTEXITCODE -ne 0) { $bad += ('production data check failed: ' + $pdc.Trim()) } else { Write-Host $pdc.Trim() }
if ($cur -eq $Build) { $bad += "$Build is already live" }
$sa = "$B\$Build\HadesSlice_Data\StreamingAssets"; $mis = @()
foreach ($f in Get-ChildItem "$P\Assets\Data" -Filter *.json -File) { $o = Join-Path $sa $f.Name; if ((Test-Path $o) -and ((Get-FileHash $f.FullName -Algorithm MD5).Hash -ne (Get-FileHash $o -Algorithm MD5).Hash)) { $mis += $f.Name } }
if ($mis) { if ($TreeAhead) { Write-Host ('md5 tree != build (allowed, -TreeAhead): ' + ($mis -join ', ')) } else { $bad += ('md5 tree != build: ' + ($mis -join ', ')) } }
$dg = & python -I "$P\Tools\data_gates.py" --added-keys-ok 2>&1 | Select-String 'RESULT'; Write-Host ('data_gates: ' + $dg)
if ($dg -notmatch 'PASS') { $bad += 'data_gates not PASS' }
Write-Host "live now: $cur -> new live: $Build ; PREV becomes $cur"
if ($bad) { Write-Host ('RELEASE BLOCKED: ' + ($bad -join '; ')); exit 3 }
$live = 'PLAY.bat', 'PLAY_ZONES.bat', 'PLAY_ALL_ZONES.bat', 'PLAY_FINAL_BOSS.bat'
foreach ($l in $live) { $t = Get-Content "$B\$l" -Raw; Write-Host ("  $l : " + ([regex]::Matches($t, 'HDRP_\w+') | % Value | Sort -Unique) + " -> $Build") }
Write-Host ("  PLAY_PREV.bat : " + ([regex]::Match((Get-Content "$B\PLAY_PREV.bat" -Raw), 'HDRP_\w+').Value) + " -> $cur")
if (-not $Apply) { Write-Host 'dry run (add -Apply)'; exit 0 }
New-Item -ItemType Directory "$P\Tools\_backup_LAUNCHERS_$(Get-Date -f HHmm)" -Force | Out-Null; Copy-Item "$B\PLAY*.bat" "$P\Tools\_backup_LAUNCHERS_$(Get-Date -f HHmm)\"
foreach ($l in $live) { $t = Get-Content "$B\$l" -Raw; [IO.File]::WriteAllText("$B\$l", ($t -replace [regex]::Escape($cur), $Build)) }
$t = Get-Content "$B\PLAY_PREV.bat" -Raw; [IO.File]::WriteAllText("$B\PLAY_PREV.bat", ($t -replace 'HDRP_\w+\\HadesSlice', "$cur\HadesSlice"))
Write-Host ('PRUNE ' + (& powershell -NoProfile -File "$P\Tools\prune_builds.ps1"))
Board "RELEASE live = $Build (PREV = $cur). Launchers PLAY/PLAY_ZONES/PLAY_ALL_ZONES/PLAY_FINAL_BOSS -> $Build, PLAY_PREV -> $cur; roll-back = PLAY_PREV.bat."


# flip.ps1 -Build HDRP_v109xx : data check + release.ps1 -Apply, then READS THE LAUNCHERS BACK. Prints FLIPPED OK only if PLAY.bat names the build.
# Lesson 16 Nov 14:39: release.ps1 prints "live now: ... -> new live: X" even when its last line is "RELEASE BLOCKED: a game is running"; never trust the first lines.
param([Parameter(Mandatory)][string]$Build,[string]$Note='',[switch]$DryRun)
$p = $env:FK_ROOT; Set-Location $p
# COO 08:38 17 Nov: refuse a flip when any StreamingAssets json of the target build is empty (<3 bytes), under 50% of its size in the CURRENT live build, or fails to parse; the tree twins (Assets\Data vs Assets\StreamingAssets) must be byte-identical. Runs BEFORE prod_data_check and release.
$sa = "$p\Builds\$Build\HadesSlice_Data\StreamingAssets"; $liveName = ((Get-Content "$p\Builds\PLAY.bat" -Raw) | Select-String 'HDRP_v109\w+').Matches.Value; $livesa = "$p\Builds\$liveName\HadesSlice_Data\StreamingAssets"
$why = @()
foreach ($j in Get-ChildItem $sa -Filter *.json -File -EA SilentlyContinue) {
  if ($j.Length -lt 3) { $why += "$($j.Name): EMPTY ($($j.Length) bytes)"; continue }
  $old = Join-Path $livesa $j.Name; if ((Test-Path $old) -and $liveName -ne $Build) { $ol = (Get-Item $old).Length; if ($ol -gt 200 -and $j.Length -lt 0.5 * $ol) { $why += "$($j.Name): $($j.Length) bytes is under 50% of live $liveName ($ol)" } }
  try { $null = (Get-Content $j.FullName -Raw).TrimStart([char]0xFEFF) | ConvertFrom-Json -ErrorAction Stop } catch { $why += "$($j.Name): does not parse" }
}
foreach ($j in Get-ChildItem "$p\Assets\Data" -Filter *.json -File -EA SilentlyContinue) {
  if ($j.Length -lt 3) { $why += "tree Assets\Data\$($j.Name): EMPTY"; continue }
  $tw = "$p\Assets\StreamingAssets\$($j.Name)"; if ((Test-Path $tw) -and ((Get-FileHash $j.FullName).Hash -ne (Get-FileHash $tw).Hash)) { $why += "twin mismatch: $($j.Name) (Assets\Data != Assets\StreamingAssets)" }
}
# Audit 18 Nov: a candidate whose build_info.json does not say Succeeded (failed/incomplete compile-only take) is never flipped.
try { $bif = "$p\Builds\$Build\build_info.json"; if (Test-Path $bif) { $bj = Get-Content $bif -Raw | ConvertFrom-Json; if ($bj.result -ne 'Succeeded') { $why += "build_info.json result is '$($bj.result)' (not Succeeded)" } } } catch { $why += 'build_info.json unreadable' }
if ($why.Count) { 'FLIP REFUSED (json guard): ' + ($why -join ' | '); exit 5 }
# COO/Lead 17 Nov: a build whose StreamingAssets json differs from the TREE would silently revert hot data (rh was built on an older base). Refuse unless the file is listed in $env:FK_FLIP_ALLOW_DIFF (comma list) or -TreeAheadOk.
$diff = @(); foreach ($j in Get-ChildItem "$p\Assets\StreamingAssets" -Filter *.json -File -EA SilentlyContinue) { $b = Join-Path $sa $j.Name; if ((Test-Path $b) -and ((Get-FileHash $j.FullName).Hash -ne (Get-FileHash $b).Hash)) { $diff += $j.Name } }
$allow = @(($env:FK_FLIP_ALLOW_DIFF -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$bad2 = @($diff | Where-Object { $allow -notcontains $_ })
if ($bad2.Count -gt 0 -and -not $DryRun) { 'FLIP REFUSED (tree check): the build differs from the tree (it would revert data): ' + ($bad2 -join ', '); exit 6 }
& powershell -NoProfile -File "$p\_agents\unitydev\prod_data_check.ps1" -Build $Build | Select-Object -Last 1
if ($DryRun) { $out = '' } else { $out = & powershell -NoProfile -File "$p\_agents\unitydev\release.ps1" -Build $Build -TreeAhead -Apply 2>&1 | Out-String }
$blocked = $out -split "`n" | Where-Object { $_ -match 'BLOCKED|REFUSED|FAIL' }
$bad = @()
foreach ($l in 'PLAY.bat','PLAY_ZONES.bat','PLAY_ALL_ZONES.bat','PLAY_FINAL_BOSS.bat') {
  $c = Get-Content "$p\Builds\$l" -Raw -EA SilentlyContinue; if ($c -notmatch [regex]::Escape($Build)) { $bad += $l }
}
if ($bad.Count -eq 0) { "FLIPPED OK: all four launchers name $Build"; $bi = $null; try { $bi = Get-Content "$p\Builds\$Build\build_info.json" -Raw | ConvertFrom-Json } catch { }; $pth = if ($bi -and $bi.source -eq 'deploy_data') { 'HOT' } elseif ($bi -and "$($bi.note)" -match 'compile-only') { 'CODE' } else { 'FULL' }; "PATH $pth"; $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm') flip.ps1 LIVE $Build [PATH $pth] (all four launchers read back, PLAY.bat = $Build). $Note"; if ($DryRun) { "DRYRUN board line: $line" } else { Add-Content "$p\_agents\COORDINATION.md" "`n$line" } } else { "FLIP FAILED: launchers not on $Build : " + ($bad -join ', ') + " | " + ($blocked -join ' ; ').Trim(); exit 4 }





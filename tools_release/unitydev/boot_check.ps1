# unity-dev: boots a build N s with real player flags (window 1280x800), closes ONLY the process it started, prints the zone / ground / fall lines of THAT run's session log.
# FAIL (exit 1) if: the player died before -Secs (prints its exit code), the log shows less than 10 s of run time, there is no "[Zones] on" zone-loaded line, or any Exception / NO ground / fell out line (Lead rule 15:50: a 0.4 s quit is not a smoke).
# usage: boot_check.ps1 -Build HDRP_v109q3 -SaveDir C:\...\nospace [-Secs 25]
param([string]$Build, [string]$SaveDir, [int]$Secs = 25)
$root = $env:FK_ROOT
if (Get-Process HadesSlice -ErrorAction SilentlyContinue) { Write-Host 'A GAME IS RUNNING: not launching'; exit 2 }
New-Item -ItemType Directory -Force $SaveDir | Out-Null
$t0 = Get-Date
$p = Start-Process -FilePath "$root\Builds\$Build\HadesSlice.exe" -ArgumentList @('-zones', '-quality:ultra2k', '-screen-width', '1280', '-screen-height', '800', '-screen-fullscreen', '0', '-savePath', $SaveDir) -PassThru
$died = $false; $rc = $null
for ($i = 0; $i -lt $Secs; $i++) { Start-Sleep 1; if ($p.HasExited) { $died = $true; $rc = $p.ExitCode; break } }
if (-not $died) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Start-Sleep 3 }
$ran = ((Get-Date) - $t0).TotalSeconds
$l = (Get-ChildItem "$env:USERPROFILE\AppData\LocalLow\Dom Games\Frankendom\logs" -Filter 'session_*.log' | Where-Object { $_.LastWriteTime -gt $t0.AddSeconds(-2) } | Sort-Object LastWriteTime | Select-Object -Last 1)
if (-not $l) { Write-Host 'SMOKE FAIL: no session log written by this run'; exit 1 }
$l = $l.FullName; Write-Host "log: $l"
Select-String $l -Pattern 'Zones\] on|NO ground|Exception|fell out|SavePaths' -CaseSensitive | ForEach-Object { $_.Line.Substring(0, [Math]::Min(170, $_.Line.Length)) }
$ng = (Select-String $l -Pattern 'NO ground' -CaseSensitive).Count; $ex = (Select-String $l -Pattern 'Exception').Count; $fl = (Select-String $l -Pattern 'fell out').Count; $zl = (Select-String $l -Pattern '\[Zones\] on').Count
$np = (Select-String $l -Pattern 'no player fighter in this scene|\[ZoneShot\] no player / camera' | Measure).Count
$last = 0.0; foreach ($m in (Select-String $l -Pattern '\[\+([0-9.]+)s f' -AllMatches | Select-Object -Last 1)) { $last = [double]$m.Matches[0].Groups[1].Value }
Write-Host ("NO ground: {0}  exceptions: {1}  fell: {2}  zone-loaded lines: {3}  log run time {4:N1} s  wall {5:N1} s  died early: {6}" -f $ng, $ex, $fl, $zl, $last, $ran, $(if ($died) { "yes (exit $rc)" } else { 'no' }))
$bad = @(); if ($died) { $bad += "player exited after $([int]$ran) s (exit $rc)" }; if ($last -lt 10) { $bad += "log shows only $last s" }; if ($zl -lt 1) { $bad += 'no zone-loaded line' }; if ($ex) { $bad += "$ex exceptions" }; if ($ng) { $bad += "$ng NO ground" }; if ($np) { $bad += "$np HARD FAIL line(s): no player fighter / no player+camera in this scene" }; if ($fl) { $bad += "$fl fell" }
if ($bad) { Write-Host ('SMOKE FAIL: ' + ($bad -join '; ')); exit 1 } else { Write-Host 'SMOKE PASS' }

# persistent_probe.ps1 - laneBUILDSPEED. Measures what a PERSISTENT Editor would really save, with the Unity CLI (unity-cli skill, com.unity.pipeline 0.10.0-exp.1 is in Packages\manifest.json).
# DRY RUN by default: prints the plan. -Run launches an Editor on THIS project: only the Unity dev runs it, with the slot ($($env:USERPROFILE)\claude-watchdog\UNITY_SLOT.md) held by a line
# written BEFORE and removed AFTER, and only when pool\unity_busy.py says FREE and no other Editor is running (Armagedom shares the slot: never run it while its line is there).
# Measures (all wall clock, written to out\persistent_probe.json):
#   t_open_ready   unity open <project> -> unity status --until-ready  (the cold start a persistent Editor pays ONCE instead of once per batch)
#   t_cmd_noop     unity command script eval 'UnityEngine.Debug.Log(1);' x5  (warm round trip)
#   t_cmd_batchfn  unity command script eval 'BuildTimer.Step(...)'-free call of a cheap static (SpanData.Reload()) twice: 2nd call time = warm cost, 1st = domain+cache cost
#   rss_mb         Unity.exe working set after ready and after the calls (memory a held Editor keeps while Armagedom needs the machine)
#   t_close        unity close
# Decision rule written next to the numbers: adopt only if (launches-1) * t_open_ready saves more than 10 percent of the chain AND the slot rule (max 30 min hold) allows it.
param([switch]$Run)
$P = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Write-Host "PLAN: unity self-update --check; unity open $P; unity status --until-ready --project-path $P; 5 x unity command script eval; 2 x SpanData.Reload; unity close --project-path $P"
if (-not $Run) { Write-Host 'DRY RUN: nothing launched.'; exit 0 }
$busy = & python -I (Join-Path $P '_agents\pool\unity_busy.py') 2>&1 | Out-String
if ($busy -match 'BUSY' -or (Get-Process Unity -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*6000.6.7f1*' })) { throw "an Editor is running: $busy" }
$slot = Get-Content "$env:USERPROFILE\claude-watchdog\UNITY_SLOT.md"
if (($slot | Select-Object -Skip 1 | Where-Object { $_.Trim() }).Count -gt 0 -and -not ($slot -match 'buildspeed-probe')) { Write-Host 'WARNING: slot file has lines; make sure they are expired'; }
$r = [ordered]@{}; $t = [System.Diagnostics.Stopwatch]::StartNew()
& unity open $P --no-banner | Out-Null; & unity status --until-ready --project-path $P --format json | Out-Null; $r.t_open_ready = [math]::Round($t.Elapsed.TotalSeconds, 1)
$r.rss_mb_ready = [math]::Round((Get-Process Unity | Where-Object { $_.Path -like '*6000.6.7f1*' } | Measure-Object WorkingSet64 -Sum).Sum / 1MB)
$lat = @(); 1..5 | ForEach-Object { $t.Restart(); & unity command script eval --caller plugin --skill unity-cli 'UnityEngine.Debug.Log(1);' --project-path $P | Out-Null; $lat += [math]::Round($t.Elapsed.TotalMilliseconds) }; $r.t_cmd_noop_ms = $lat
$lat = @(); 1..2 | ForEach-Object { $t.Restart(); & unity command script eval --caller plugin --skill unity-cli 'SpanData.Reload();' --project-path $P | Out-Null; $lat += [math]::Round($t.Elapsed.TotalMilliseconds) }; $r.t_cmd_spandata_ms = $lat
$r.rss_mb_after = [math]::Round((Get-Process Unity | Where-Object { $_.Path -like '*6000.6.7f1*' } | Measure-Object WorkingSet64 -Sum).Sum / 1MB)
$t.Restart(); & unity close --project-path $P | Out-Null; $r.t_close = [math]::Round($t.Elapsed.TotalSeconds, 1)
$r | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'out\persistent_probe.json'); $r | ConvertTo-Json

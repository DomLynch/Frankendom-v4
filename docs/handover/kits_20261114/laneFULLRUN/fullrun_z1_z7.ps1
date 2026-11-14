# FULLRUN discovery run z1 -> z7, ONE SEGMENT PER WINDOW. Staged by the Frankendom bandits dev, 2026-11-13. NOT RUN by its author (file-only lane).
# Usage (unity-dev, in the Frankendom window, QA copy only, never flips anything):
#   .\fullrun_z1_z7.ps1 -Segment 1 -DryRun        prints the steps + time estimate, writes nothing, launches nothing
#   .\fullrun_z1_z7.ps1 -Segment 1                prepares the QA copy if missing, waits for the slot + FOCUS, runs <= MaxMinutes, extracts frames + events
#   -Segment 1..5 in order (1 wipes the sandbox save, 2..5 reuse it so boss kill records / gate unlocks carry over). See README.md.
param(
  [Parameter(Mandatory = $true)][ValidateRange(1, 5)][int]$Segment,
  [string]$Base = '',                 # default = the build PLAY.bat launches (read back, never assumed)
  [string]$Tag = '109lcrun',          # QA copy folder Builds\HDRP_v<Tag>, hard-linked data copy of Base via deploy_data.ps1 (never flipped)
  [int]$MaxMinutes = 5,               # hard stop (Lead: 5 min window)
  [double]$P1S = 20, [double]$P2S = 20,   # seconds of real phase-1 fighting, seconds shown in phase 2 before the kill
  [double]$TakenMul = 0.2, [double]$HpFloor = 0.97,   # QA assist on the COPY only (Joan damage stays 1.0); see README "cannot judge"
  [switch]$DryRun, [switch]$NoSlotWait, [switch]$AllowTreeDrift
)
$ErrorActionPreference = 'Stop'
$P = 'C:\Users\dom12\Documents\unity_test_hdrp'; $enc = New-Object Text.UTF8Encoding $false
$o0 = "$P\_agents\laneFULLRUN\seg$Segment"
function Done($m) { if ($DryRun) { return }; New-Item -ItemType Directory -Force $o0 | Out-Null; Add-Content "$o0\_progress.txt" ("{0} {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }
function Board($m) { if ($DryRun) { return }; $fs = [IO.File]::Open("$P\_agents\COORDINATION.md", 'Append', 'Write', 'ReadWrite'); $b = [Text.Encoding]::UTF8.GetBytes("`r`n" + (Get-Date -Format 'yyyy-MM-dd HH:mm') + ' ' + $m + "`r`n"); $fs.Write($b, 0, $b.Length); $fs.Close() }
function FocusClear { $ln = (Get-Content "$P\_agents\COORDINATION.md" -Tail 40 | Select-String 'FOCUS (TAKEN|RELEASED)' | Select-Object -Last 1).Line; if (-not $ln) { return $true }; if ($ln -match 'RELEASED') { return $true }; if ($ln -match 'until (\d\d):(\d\d)') { $u = [datetime]::Today.AddHours([int]$Matches[1]).AddMinutes([int]$Matches[2]); return ((Get-Date) -gt $u.AddSeconds(15)) }; return $true }
function InSlot { $m = (Get-Date).Minute % 15; return ($m -ge 5 -and $m -lt 9) }   # Frankendom window, minutes 3-5 of each ten
function TakeLock { try { $fs = [IO.File]::Open("$P\_agents\laneHOTSHIP\player.lock", 'CreateNew', 'Write', 'None'); $fs.Close(); return $true } catch { return $false } }
function S($id, $kind, $h) { $o = [ordered]@{ id = $id; kind = $kind; run = $false; timeoutS = 60; arriveM = 2.5 }; foreach ($k in $h.Keys) { $o[$k] = $h[$k] }; [pscustomobject]$o }

# ---- live build = what PLAY.bat launches (read back) ----
if (-not $Base) { $pb = Get-Content "$P\Builds\PLAY.bat" | Select-String -Pattern 'HDRP_\w+' | Select-Object -First 1; if (-not $pb) { throw 'cannot read the live build from PLAY.bat' }; $Base = ([regex]::Match($pb.Line, 'HDRP_\w+')).Value }
$dir = "$P\Builds\HDRP_v$Tag"; $sb = "$dir\HadesSlice_Data\StreamingAssets"

# ---- step builder: pacing per boss. hpLeft = just under the phase threshold (effective max hp = fight_balance maxHealth x opponents hpMul: Pitborn 520, Sobek 552, Grendel 650, Humbaba 728, Erlking 900; thresholds ai_adapt enrage.hpFrac .5/.6/.6/.5/.5) ----
$ALL = @('z1_pitborn', 'z2_sobek', 'z4_grendel', 'z5_humbaba', 'z6_erlking')
$est = @{}   # id -> estimated seconds (for the window budget)
function Add($list, $step, $e) { $est[$step.id] = $e; $list.Add($step) | Out-Null }
function BossBlock($L, $zid, $boss, $stand, $standPos, $hpLeft, $p) {
  $m = if ($standPos) { S "${zid}_meet" 'walk' @{ pos = $standPos; run = $false; timeoutS = 40; arriveM = 2.0; untilEngaged = 1 } } else { S "${zid}_meet" 'walk' @{ zone = $stand[0]; spot = $stand[1]; run = $false; timeoutS = 40; arriveM = 2.5; untilEngaged = 1 } }
  Add $L $m 20
  Add $L (S "${zid}_p1" 'fight' @{ seconds = $P1S; timeoutS = ($P1S + 30); arriveM = 1.5; cycle = $true }) $P1S
  Add $L (S "${zid}_p2" 'fight' @{ seconds = ($P2S + 2); timeoutS = ($P2S + 32); arriveM = 1.5; cycle = $true; boss = $boss; killAfter = 1.0; hpLeft = $hpLeft }) ($P2S + 2)
  Add $L (S "${zid}_kill" 'fight' @{ seconds = 7; timeoutS = 40; arriveM = 1.5; cycle = $true; boss = $boss; killAfter = 1.0 }) 7
  Add $L (S "${zid}_dead" 'hold' @{ seconds = 4; timeoutS = 30 }) 4
}
function Arrive($L, $zid, $zone, $spot, $revive, $holdS) {
  Add $L (S "${zid}_restart" 'restart' @{ zone = $zone; spot = $spot; revive = $revive; run = $true; timeoutS = 70; arriveM = 1.5 }) 35
  Add $L (S "${zid}_arr" 'hold' @{ seconds = $holdS; timeoutS = 30 }) $holdS
}
function Gate($L, $id, $zone, $spot, $t) { Add $L (S $id 'walk' @{ zone = $zone; spot = $spot; run = $true; timeoutS = $t; arriveM = 2.0 }) 20 }
$L = New-Object System.Collections.ArrayList
switch ($Segment) {
  1 {   # z1 Pitborn + z2 Sobek. Resume spot: pit_yard / arrive_start
    Add $L (S 'ready' 'ready' @{ run = $true; timeoutS = 90; arriveM = 1.5 }) 20
    Arrive $L 'z1' 'pit_yard' 'arrive_start' $ALL 6
    BossBlock $L 'z1' 'z1_pitborn' $null @(0.0, 13.5) 240 $null
    Gate $L 'z1_gate' 'pit_yard' 'gate_sobek' 45
    Add $L (S 'z2_arr0' 'walk' @{ zone = 'sobek_reach'; spot = 'arrive_from_pit'; run = $true; timeoutS = 30; arriveM = 2.0 }) 8
    Add $L (S 'z2_arr' 'hold' @{ seconds = 5; timeoutS = 30 }) 5
    BossBlock $L 'z2' 'z2_sobek' @('sobek_reach', 'sobek_stand') $null 310 $null
  }
  2 {   # hub (safe) + z4 Grendel with the z4_p wave and BossHold. Resume spot: hub_camp / arrive_from_prev
    Arrive $L 'hub' 'hub_camp' 'arrive_from_prev' @() 6
    Gate $L 'hub_gate' 'hub_camp' 'gate_next' 45
    Add $L (S 'z4_arr0' 'walk' @{ zone = 'grendel'; spot = 'arrive_from_prev'; run = $true; timeoutS = 30; arriveM = 2.0 }) 8
    Add $L (S 'z4_arr' 'hold' @{ seconds = 6; timeoutS = 30 }) 6
    Add $L (S 'z4_wavewalk' 'walk' @{ zone = 'grendel'; spot = 'bandit_z4_p_1'; run = $true; timeoutS = 40; arriveM = 3.0 }) 6
Add $L (S 'z4_wave' 'fight' @{ seconds = 14; timeoutS = 40; arriveM = 1.5; boss = 'bandit_z4_p_1'; killAfter = 2.0; hpLeft = 5.0 }) 14   # Mere Raider wave while Grendel is held
    Add $L (S 'z4_rel' 'hold' @{ seconds = 12; timeoutS = 30 }) 12                                   # BossHold release (maxHoldS 150 is the fallback)
    BossBlock $L 'z4' 'z4_grendel' @('grendel', 'grendel_stand') $null 370 $null
  }
  3 {   # z5 Humbaba: wave fight, one forced death + respawn grab, boss. Resume spot: z5_humbaba / arrive_from_prev
    Arrive $L 'z5' 'z5_humbaba' 'arrive_from_prev' @('z5_humbaba') 26   # 26 s hold: the arrival frame must land after the ~25 s boot black
    Add $L (S 'z5_wavewalk1' 'walk' @{ zone = 'z5_humbaba'; spot = 'bandit_z5_p_1'; run = $true; timeoutS = 25; arriveM = 3.0 }) 5
    Add $L (S 'z5_wave1' 'fight' @{ seconds = 12; timeoutS = 30; arriveM = 1.5; boss = 'bandit_z5_p_1'; killAfter = 2.0; hpLeft = 5.0 }) 12
    Add $L (S 'z5_wavewalk2' 'walk' @{ zone = 'z5_humbaba'; spot = 'bandit_z5_p_2'; run = $true; timeoutS = 25; arriveM = 3.0 }) 5
    Add $L (S 'z5_wave2' 'fight' @{ seconds = 12; timeoutS = 30; arriveM = 1.5; boss = 'bandit_z5_p_2'; killAfter = 2.0; hpLeft = 5.0 }) 12
    Add $L (S 'z5_wavewalk3' 'walk' @{ zone = 'z5_humbaba'; spot = 'bandit_z5_p_3'; run = $true; timeoutS = 25; arriveM = 3.0 }) 5
    Add $L (S 'z5_wave3' 'fight' @{ seconds = 12; timeoutS = 30; arriveM = 1.5; boss = 'bandit_z5_p_3'; killAfter = 2.0; hpLeft = 5.0 }) 12
    Add $L (S 'z5_force' 'forceDeath' @{ count = 1; timeoutS = 60; gapS = 1.5 }) 18                  # Joan dies, revive path, respawn frame
    Add $L (S 'z5_respawn' 'hold' @{ seconds = 5; timeoutS = 30 }) 5
    BossBlock $L 'z5' 'z5_humbaba' @('z5_humbaba', 'boss_stand') $null 345 $null
  }
  4 {   # z6 Erlking (kill record lets the end gate open). Resume spot: z6_erlking / arrive_from_prev
    Arrive $L 'z6' 'z6_erlking' 'arrive_from_prev' @('z6_erlking') 26   # 26 s hold: arrival frame after the boot black
    Add $L (S 'z6_wavewalk1' 'walk' @{ zone = 'z6_erlking'; spot = 'bandit_z6_p_1'; run = $true; timeoutS = 25; arriveM = 3.0 }) 5
    Add $L (S 'z6_wave1' 'fight' @{ seconds = 12; timeoutS = 30; arriveM = 1.5; boss = 'bandit_z6_p_1'; killAfter = 2.0; hpLeft = 5.0 }) 12
    Add $L (S 'z6_wavewalk2' 'walk' @{ zone = 'z6_erlking'; spot = 'bandit_z6_p_2'; run = $true; timeoutS = 25; arriveM = 3.0 }) 5
    Add $L (S 'z6_wave2' 'fight' @{ seconds = 12; timeoutS = 30; arriveM = 1.5; boss = 'bandit_z6_p_2'; killAfter = 2.0; hpLeft = 5.0 }) 12
    Add $L (S 'z6_wavewalk3' 'walk' @{ zone = 'z6_erlking'; spot = 'bandit_z6_p_3'; run = $true; timeoutS = 25; arriveM = 3.0 }) 5
    Add $L (S 'z6_wave3' 'fight' @{ seconds = 12; timeoutS = 30; arriveM = 1.5; boss = 'bandit_z6_p_3'; killAfter = 2.0; hpLeft = 5.0 }) 12
    BossBlock $L 'z6' 'z6_erlking' @('z6_erlking', 'boss_stand') $null 430 $null
    Gate $L 'z6_gate' 'z6_erlking' 'gate_next' 45
  }
  5 {   # z7 camp + end gate + alpha_end screen (needs the z6_erlking kill record from segment 4 in the shared save). Resume spot: z7_market2 / arrive_from_prev
    Arrive $L 'z7' 'z7_market2' 'arrive_from_prev' @() 26   # 26 s hold: arrival frame after the boot black
    Add $L (S 'z7_hearth' 'walk' @{ zone = 'z7_market2'; spot = 'hearth'; run = $true; timeoutS = 45; arriveM = 2.5 }) 25
    Add $L (S 'z7_look' 'hold' @{ seconds = 4; timeoutS = 30 }) 4
    Add $L (S 'z7_end' 'walk' @{ zone = 'z7_market2'; spot = 'gate_alpha_end'; run = $true; timeoutS = 50; arriveM = 1.5 }) 25
    Add $L (S 'z7_endscreen' 'hold' @{ seconds = 8; timeoutS = 30 }) 8
  }
}
$steps = @($L); $totalEst = ($est.Values | Measure-Object -Sum).Sum; $budget = $MaxMinutes * 60 - 40
Write-Host ("segment {0}: {1} steps, estimated {2:N0} s of steps + ~30 s boot, budget {3} s (MaxMinutes {4}); base {5} -> QA copy HDRP_v{6}" -f $Segment, $steps.Count, $totalEst, $budget, $MaxMinutes, $Base, $Tag)

foreach ($s in $steps) { Write-Host ("  {0,-14} {1,-10} est {2,3} s  timeout {3}" -f $s.id, $s.kind, $est[$s.id], $s.timeoutS) }
if ($totalEst -gt $budget) { Write-Host "WARN: estimate exceeds the window budget: lower -P1S/-P2S or run with a longer -MaxMinutes (needs the next project's slot free)"; }
if ($DryRun) { Write-Host 'DRY RUN: nothing prepared, nothing launched, nothing written.'; return }

# ---- QA copy (never the live build; deploy_data.ps1 copies the WHOLE tree data over Base, so the tree must equal Base) ----
if (-not (Test-Path "$dir\build_info.json")) {
  if (-not $AllowTreeDrift) { foreach ($f in 'zones.json', 'bandits.json', 'ai_adapt.json', 'poise.json', 'boss_phase.json', 'look_zones.json', 'accent_lights.json', 'mount.json', 'fight_balance.json', 'opponents.json') {
      $a = "$P\Assets\StreamingAssets\$f"; $b = "$P\Builds\$Base\HadesSlice_Data\StreamingAssets\$f"; if ((Test-Path $a) -and (Test-Path $b) -and ((Get-FileHash $a -Algorithm MD5).Hash -ne (Get-FileHash $b -Algorithm MD5).Hash)) { throw "tree differs from the live build in ${f}: unfinished or staged tree edits would leak into the copy (use -AllowTreeDrift to accept)" } } }
  powershell -NoProfile -File "$P\_agents\laneBUILDSPEED\deploy_data.ps1" -v $Tag -Base $Base -Apply -SkipGates -IgnoreAssetChanges | Out-Null
}
if (-not (Test-Path "$dir\build_info.json")) { Done 'ABORT deploy'; throw 'QA copy missing after deploy' }
Get-ChildItem $sb -Filter *.json -Recurse | Where-Object { $_.IsReadOnly } | ForEach-Object { $_.IsReadOnly = $false }
$mj = [IO.File]::ReadAllText("$sb\menu.json").TrimStart([char]0xFEFF) | ConvertFrom-Json; $mj.enabled = $false; [IO.File]::WriteAllText("$sb\menu.json", ($mj | ConvertTo-Json -Depth 20), $enc)
$fb = [IO.File]::ReadAllText("$sb\fight_balance.json").TrimStart([char]0xFEFF) | ConvertFrom-Json; ($fb.fighters | Where-Object { $_.key -eq 'Joan' }).takenMul = $TakenMul; [IO.File]::WriteAllText("$sb\fight_balance.json", ($fb | ConvertTo-Json -Depth 20), $enc)
$j = [IO.File]::ReadAllText("$sb\camtrace.json").TrimStart([char]0xFEFF) | ConvertFrom-Json
$j.frames.enabled = $true; $j.frames.hz = 2.0; $j.frames.width = 0; $j.frames.maxFrames = 1500; $j.frames.jpgQuality = 90
$j.scene.hpFloorFrac = $HpFloor; $j.scene.strikeIntervalS = 0.55; $j.scene.globalTimeoutS = ($MaxMinutes * 60 - 25); $j.scene.quit = $true; $j.scene.steps = $steps
[IO.File]::WriteAllText("$sb\camtrace.json", ($j | ConvertTo-Json -Depth 30), $enc)
Done "prepared seg $Segment ($($steps.Count) steps, est $totalEst s)"

# ---- window: own slot, no FOCUS claim from others, no game running, lock ----
if (-not $NoSlotWait) { while (-not ((InSlot) -and (FocusClear) -and -not (Get-Process HadesSlice -ErrorAction SilentlyContinue))) { Start-Sleep -Milliseconds (2000 + (Get-Random -Maximum 1500)) } }
if (-not (TakeLock)) { Start-Sleep 8; if (-not (TakeLock)) { Done 'ABORT lock busy'; throw 'player.lock busy' } }
$until = (Get-Date).AddMinutes($MaxMinutes).ToString('HH:mm')
Board "Frankendom unity-dev FOCUS TAKEN until $until (fullrun discovery segment $Segment on QA copy HDRP_v$Tag, base $Base, native 2560x1600, shared sandbox save)"
$out = "$o0\run"; Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force "$out\trace" | Out-Null
$save = 'C:\Users\dom12\AppData\Local\FrankendomSmoke\fullrun_lc'
if ($Segment -eq 1) { Remove-Item $save -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $save | Out-Null
Done "start until $until"
# the REAL launcher flags (play_zones.ps1 passes -zones -quality:ultra2k 2560x1600 windowed); plus the harness flags
$a = @('-zones', '-quality:ultra2k', '-screen-width', '2560', '-screen-height', '1600', '-screen-fullscreen', '0', '-savePath', $save, '-camscene', "-camtrace:$out\trace", '-banditylog:5', '-silent-crashes', '-logFile', "$out\game.log")
$pr = Start-Process "$dir\HadesSlice.exe" -ArgumentList $a -WorkingDirectory $dir -PassThru
$pr.WaitForExit(($MaxMinutes * 60 - 5) * 1000) | Out-Null
if (-not $pr.HasExited) { try { $pr.Kill() } catch { }; Done 'hard stop: killed by window limit' }
Done 'game ended'
Remove-Item "$P\_agents\laneHOTSHIP\player.lock" -Force -ErrorAction SilentlyContinue
Board "Frankendom unity-dev FOCUS RELEASED (fullrun segment $Segment captured: _agents\laneFULLRUN\seg$Segment)"

# ---- extraction (reads files only) ----
$log = "$out\game.log"
if (Test-Path $log) {
  $pat = '\[BossHold\]|\[BossPhase\]|\[Zones\] engage|\[Zones\] revive|\[Zones\] end screen|\[CamScene\]|killAfter|hpLeft|\[Bandits\] gate|\[AiAdapt\].*(combo|enrage)|fell out of the world|Exception|\[GroundRing\]|\[CombatSlowMo\]|\[HudHp\] Joan'
  Select-String -Path $log -Pattern $pat | ForEach-Object { $_.Line } | Set-Content "$o0\events.txt" -Encoding utf8
  $sum = @(); foreach ($k in 'BossHold', 'BossPhase', 'Zones] engage', 'revive', 'end screen', 'fell out of the world', 'Exception') { $n = (Select-String -Path $log -Pattern ([regex]::Escape($k)) | Measure-Object).Count; $sum += ("{0,-28} {1}" -f $k, $n) }
  $sum | Set-Content "$o0\summary.txt" -Encoding utf8
}
# key frames: nearest native frame to each tagged step moment (camtrace.csv has t + step, frames.csv row i = f_<i>.jpg)
$ct = "$out\trace\camtrace.csv"; $fc = "$out\trace\frames.csv"; $kf = "$o0\key"
if ((Test-Path $ct) -and (Test-Path $fc)) {
  New-Item -ItemType Directory -Force $kf | Out-Null
  $rows = Import-Csv $ct; $fr = Import-Csv $fc
  $pt = ($rows[0].PSObject.Properties.Name | Where-Object { $_ -ieq 't' } | Select-Object -First 1); $ps = ($rows[0].PSObject.Properties.Name | Where-Object { $_ -ieq 'step' } | Select-Object -First 1); $ft = ($fr[0].PSObject.Properties.Name | Where-Object { $_ -ieq 't' } | Select-Object -First 1)
  if ($pt -and $ps -and $ft) {
    $ftimes = @(); foreach ($r in $fr) { $ftimes += [double]$r.$ft }
    function PickFrame($id, $mode, $off, $name) {
      $m = @($rows | Where-Object { $_.$ps -eq $id }); if ($m.Count -eq 0) { return }
      $t0 = [double]$m[0].$pt; $t1 = [double]$m[$m.Count - 1].$pt
      $tt = switch ($mode) { 'start' { $t0 + $off } 'end' { $t1 - $off } default { ($t0 + $t1) / 2 + $off } }
      $bi = 0; $bd = [double]::MaxValue; for ($i = 0; $i -lt $ftimes.Count; $i++) { $d = [math]::Abs($ftimes[$i] - $tt); if ($d -lt $bd) { $bd = $d; $bi = $i } }
      $src = "$out\trace\frames\f_{0:D6}.jpg" -f $bi; if (Test-Path $src) { Copy-Item $src ("$kf\{0}_{1}.jpg" -f $id, $name) -Force }
    }
    foreach ($z in 'z1', 'z2', 'hub', 'z4', 'z5', 'z6', 'z7') {
      PickFrame "${z}_arr" 'mid' 0 'arrival'; PickFrame "${z}_p1" 'start' 3 'boss_start'; PickFrame "${z}_p2" 'start' 6 'phase2'; PickFrame "${z}_kill" 'end' 1 'boss_death'; PickFrame "${z}_dead" 'mid' 0 'after_death'
      PickFrame "${z}_wave" 'start' 12 'wave'; PickFrame "${z}_rel" 'end' 1 'hold_release'
    }
    PickFrame 'z5_wave1' 'mid' 0 'wave'; PickFrame 'z6_wave1' 'mid' 0 'wave'; PickFrame 'z5_force' 'mid' 0 'player_death'; PickFrame 'z5_respawn' 'mid' 0 'respawn'; PickFrame 'z7_endscreen' 'mid' 0 'end_screen'; PickFrame 'z7_hearth' 'end' 1 'z7_camp'
  }
}
Done "segment $Segment done"



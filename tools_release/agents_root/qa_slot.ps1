# qa_slot.ps1 - machine-wide QA player slots for ALL lanes (COO 17 Nov 12:50).
# Why: per-lane free-RAM checks at start all pass in the same 20 s and then the players ramp
# together (12:32 and 12:41: 2-4 FD + 1 AR + 1 FK players, free RAM 0.9-4 GB, Ascent65 killed 3x).
# Rule: at most 2 QA players machine-wide, and only if free RAM >= (expected GB of the new player + 8).
# Usage (in a lane's QA launcher, BEFORE starting the player):
#   $slot = & "$($env:FK_ROOT)\_agents\qa_slot.ps1" -Acquire -Lane frontierdom -ExpectGB 9 -WaitMinutes 10
#   if (-not $slot) { exit 4 }       # refused / timed out
#   ...start the player, then:  & qa_slot.ps1 -Bind -Slot $slot -PlayerPid $p.Id
#   ...when the player ends:     & qa_slot.ps1 -Release -Slot $slot
# Stale slots (bound pid dead, or unbound for > 3 min) are cleared automatically.
# -DryRun prints the decision and takes nothing.
param(
  [switch]$Acquire, [switch]$Bind, [switch]$Release, [switch]$DryRun, [switch]$DomPlaying, [switch]$VoidDom,
  [switch]$BootCheck,   # Strategy ruling 18 Nov 11:5x: a CODE release's SHORT boot check (30 s max, 1280x720, -fpscap 30) is the ONLY QA player allowed while Dom plays; one machine-wide; never beside a -bench run
  [string]$Lane = 'unknown', [double]$ExpectGB = 10, [int]$WaitMinutes = 10,
  [string]$Slot, [int]$PlayerPid = 0, [int]$MaxPlayers = 3,   # COO 22:4x re-cost: 3 (Armagedom off the laptop; Strategy 22:36)
  [switch]$Exclusive   # fps BENCH: the only QA player on the machine (no other slot held, none admitted while it runs). Strategy 15:58: two FD bench players overlapped at 15:39.
)
$dir = "$env:FK_ROOT\_agents\qa_slots"
New-Item -ItemType Directory -Force $dir | Out-Null
$lockPath = Join-Path $dir '.mutex'

function Get-FreeGB { [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB, 1) }

function Clear-Stale {
  Get-ChildItem $dir -Filter 'slot_*.json' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $s = Get-Content $_.FullName -Raw | ConvertFrom-Json } catch { Remove-Item $_.FullName -Force; return }
    $age = ((Get-Date) - [datetime]$s.taken).TotalMinutes
    # An exclusive (fps bench) hold stops excluding others after 10 min (Strategy 14:40: quiet windows are short; COO 19:17: FK QA was waiting behind a long FD exclusive run). The player keeps running; its slot becomes a normal one.
    if ($s.exclusive -eq $true -and $age -gt 8) { $s.exclusive = $false; $s | ConvertTo-Json | Set-Content $_.FullName -Encoding utf8 }
    if ($s.pid -gt 0) {
      if (-not (Get-Process -Id $s.pid -ErrorAction SilentlyContinue)) { Remove-Item $_.FullName -Force }
    } elseif ($age -gt 3) { Remove-Item $_.FullName -Force }
  }
}

function With-Mutex([scriptblock]$body) {
  $fs = $null
  for ($i = 0; $i -lt 100 -and -not $fs; $i++) {
    try { $fs = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') } catch { Start-Sleep -Milliseconds 200 }
  }
  if (-not $fs) { throw 'qa_slot: mutex busy' }
  try { & $body } finally { $fs.Close() }
}

# COO 18 Nov 11:4x (Strategy ruling after Dom's 10:55 crash): -DomPlaying returns $true while Dom's own game runs
# (ARMAGEDOM_Slice0 without -bench, or any HadesSlice/FronteirDom/ARMAGEDOM process logging to a dom-runs folder).
# QA launchers poll it while their player runs and END THEIR OWN player (own pid) within ~5 s, marking the run VOID-DOM.
function Test-DomPlaying {
  foreach ($gp in (Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(ARMAGEDOM_Slice0|HadesSlice|FronteirDom)\.exe$' })) {
    $gcl = "$($gp.CommandLine)"
    if (($gp.Name -like 'ARMAGEDOM*' -and $gcl -notmatch '-bench') -or $gcl -match 'dom-runs') { return $true }
  }
  return $false
}
if ($DomPlaying) { return (Test-DomPlaying) }
# -VoidDom: a launcher ended its own player because Dom started playing; log the cost (lane, tag, minutes lost) and queue a rerun.
if ($VoidDom) { try { Add-Content (Join-Path $dir 'void_log.csv') ("{0},{1},{2},{3}" -f (Get-Date -Format 's'), $Lane, $Slot, $ExpectGB); Add-Content (Join-Path $dir 'requeue.txt') ("{0} {1} {2}" -f (Get-Date -Format 's'), $Lane, $Slot) } catch {}; return }
if ($Bind) {
  $f = Join-Path $dir "$Slot.json"
  if (Test-Path $f) { $s = Get-Content $f -Raw | ConvertFrom-Json; $s.pid = $PlayerPid; $s | ConvertTo-Json | Set-Content $f -Encoding utf8 }
  return
}
if ($Release) { Remove-Item (Join-Path $dir "$Slot.json") -Force -ErrorAction SilentlyContinue; Set-Content (Join-Path $dir 'last_release.txt') (Get-Date).ToString('o'); return }
# Idle gap (Strategy 18:30, both games): seconds since the last QA player on this machine ended. Every fps row records it (heat: a warm machine reads ~10-15 fps lower).
function Get-IdleGapS { $f = Join-Path $dir 'last_release.txt'; if (Test-Path $f) { [int]((Get-Date) - [datetime](Get-Content $f -Raw).Trim()).TotalSeconds } else { -1 } }

if ($Acquire -or $DryRun) {
  $deadline = (Get-Date).AddMinutes($WaitMinutes)
  do {
    $result = With-Mutex {
      Clear-Stale
      $held = @(Get-ChildItem $dir -Filter 'slot_*.json' -ErrorAction SilentlyContinue)
      $free = Get-FreeGB
      $need = $ExpectGB + 4   # COO 22:4x re-cost: the player's measured peak + the 4 GB own-pid stop (was + 8)
      $holders = ($held | ForEach-Object { (Get-Content $_.FullName -Raw | ConvertFrom-Json).lane }) -join ','
      # Open BUILD PHASE lines of OTHER lanes (COO 14:00: at 13:27 two AR players started inside FD's
      # 'BUILD PHASE player fronteirdom until 13:33' window beside its 19 GB Editor; free fell to 3.1 GB).
      $phases = @()
      try {
        $board = Get-Content "$env:FK_ROOT\_agents\COORDINATION.md" -Tail 60 -ErrorAction Stop
        foreach ($ln in $board) {
          $m = [regex]::Match($ln, 'BUILD PHASE player (\w+) until (\d\d):(\d\d)')
          if ($m.Success) {
            $pl = $m.Groups[1].Value
            if ($pl -like "$($Lane.Substring(0,[math]::Min(5,$Lane.Length)))*") { continue }
            $until = (Get-Date).Date.AddHours([int]$m.Groups[2].Value).AddMinutes([int]$m.Groups[3].Value)
            $ended = $board | Where-Object { $_ -match "BUILD PHASE player $pl ended" } | Select-Object -Last 1
            if ((Get-Date) -lt $until -and ($until - (Get-Date)).TotalMinutes -le 8 -and -not ($ended -and $board.IndexOf($ended) -gt $board.IndexOf($ln))) { $phases += "$pl until $($m.Groups[2].Value):$($m.Groups[3].Value)" }
          }
        }
        # ALONE bakes of other lanes (COO 15:55): an open 'EDITOR TAKEN <lane> ALONE' line with no later 'EDITOR RELEASED <lane>' holds every new QA player.
        for ($i = 0; $i -lt $board.Count; $i++) {
          $m2 = [regex]::Match($board[$i], 'EDITOR TAKEN (\w+) ALONE')
          if (-not $m2.Success) { continue }
          $al = $m2.Groups[1].Value
          if ($al -like "$($Lane.Substring(0,[math]::Min(5,$Lane.Length)))*") { continue }
          $rel = $false
          for ($j = $i + 1; $j -lt $board.Count; $j++) { if ($board[$j] -match "EDITOR RELEASED $al") { $rel = $true; break } }
          if (-not $rel) { $phases += "$al ALONE bake" }
        }
      } catch {}
      # COO 22:4x re-cost: BUILD PHASE and ALONE lines are logged, not blocking (Armagedom left the laptop; Strategy 22:36). Only the opt-in fps-bench exclusive still holds.
      $logged = @($phases | Select-Object -Unique); $phases = @()
      # COO 18 Nov 11:3x: Dom's own game blocks NEW QA players (running ones keep going; nothing is paused). Why: Dom's 10:55 Frankendom
      # session died with a D3D12 device-lost on the iGPU while FK QA ptb413 started beside it (10:54:36-10:55:27), then the laptop restarted.
      # Dom's game = ARMAGEDOM_Slice0 without -bench, or any game whose command line logs to a dom-runs folder. Wait time is in wait_log.csv.
      foreach ($gp in (Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(ARMAGEDOM_Slice0|HadesSlice|FronteirDom)\.exe$' })) {
        $gcl = "$($gp.CommandLine)"
        if (($gp.Name -like 'ARMAGEDOM*' -and $gcl -notmatch '-bench') -or $gcl -match 'dom-runs') { if (-not $BootCheck) { $phases += "DOM PLAYING ($($gp.Name) pid $($gp.ProcessId))" } elseif ($held | Where-Object { try { (Get-Content $_.FullName -Raw | ConvertFrom-Json).bootcheck -eq $true } catch { $false } }) { $phases += 'a boot check is already running (one machine-wide while Dom plays)' } }
        # Strategy 11:45: the 10:55 device-lost had THREE HDRP players on the iGPU, the main one an outside audit -bench run (621 fps, vsync off). No QA player starts beside a bench either.
        elseif ($gcl -match '(^|\s)-bench(\s|$)|-benchOut') { $phases += "OUTSIDE BENCH RUNNING ($($gp.Name) pid $($gp.ProcessId))" }
      }
      $exHeld = @($held | Where-Object { try { (Get-Content $_.FullName -Raw | ConvertFrom-Json).exclusive -eq $true } catch { $false } })
      $exBlock = ($exHeld.Count -gt 0) -or ($Exclusive -and $held.Count -gt 0)
      if ($exBlock) { $phases += $(if ($exHeld.Count -gt 0) { 'exclusive bench running' } else { 'exclusive bench needs 0 players' }) }
      # Exclusive CHAIN cap (COO 19:36): back-to-back exclusive runs by one lane (gap < 120 s) count as one window. After 10 min the
      # lane's next exclusive acquire waits 3 min so other lanes get a turn (FD's knob loop held the machine 13+ min in 1-min runs).
      $chainFile = Join-Path $dir "excl_chain_$Lane.txt"
      if ($Exclusive) {
        $chainStart = $null; $lastRel = $null
        if (Test-Path $chainFile) { $chainStart = [datetime](Get-Content $chainFile -Raw).Trim() }
        $lr = Join-Path $dir 'last_release.txt'; if (Test-Path $lr) { $lastRel = [datetime](Get-Content $lr -Raw).Trim() }
        if ($chainStart -and $chainStart -gt (Get-Date)) { $phases += "exclusive cooldown until $($chainStart.ToString('HH:mm:ss')) (other lanes' turn)" }
        elseif ($chainStart -and $lastRel -and ((Get-Date) - $lastRel).TotalSeconds -lt 120 -and ((Get-Date) - $chainStart).TotalMinutes -gt 10) {
          if (-not $DryRun) { Set-Content $chainFile ((Get-Date).AddMinutes(3).ToString('o')) }
          $phases += 'exclusive chain over 10 min: 3 min cooldown for other lanes'
        }
      }
      $ok = ($held.Count -lt $MaxPlayers) -and ($free -ge $need) -and ($phases.Count -eq 0)
      $msg = "QA SLOT $(if($DryRun){'DRYRUN '})lane=$Lane idleGapS=$(Get-IdleGapS) free=$free GB need=$need GB held=$($held.Count)/$MaxPlayers [$holders] blocking=[$($phases -join ', ')] logged=[$($logged -join ', ')] => $(if($ok){'GO'}else{'WAIT'})"
      if ($ok -and -not $DryRun) {
        $name = "slot_{0}_{1}" -f $Lane, (Get-Date -Format 'HHmmssfff')
        @{ lane = $Lane; pid = 0; expectGB = $ExpectGB; exclusive = [bool]$Exclusive; bootcheck = [bool]$BootCheck; taken = (Get-Date).ToString('o') } | ConvertTo-Json | Set-Content (Join-Path $dir "$name.json") -Encoding utf8
        if ($Exclusive) {
          # start a new chain unless this acquire continues one (previous release < 120 s ago and a chain start exists in the past)
          $lr2 = Join-Path $dir 'last_release.txt'; $cont = $false
          if ((Test-Path $chainFile) -and (Test-Path $lr2)) { $cs = [datetime](Get-Content $chainFile -Raw).Trim(); $cont = ($cs -le (Get-Date)) -and (((Get-Date) - [datetime](Get-Content $lr2 -Raw).Trim()).TotalSeconds -lt 120) }
          if (-not $cont) { Set-Content $chainFile (Get-Date).ToString('o') }
        }
        return @{ msg = $msg; slot = $name }
      }
      return @{ msg = $msg; slot = $null; ok = $ok }
    }
    Write-Host $result.msg
    if ($DryRun) { return }
    # COO 17 Nov 23:0x: measure the lane-minutes this guard costs (principle 5). One row per acquire that had to wait.
    $waitedMin = [math]::Round(((Get-Date) - $deadline.AddMinutes(-$WaitMinutes)).TotalMinutes, 2)
    if ($result.slot -and $waitedMin -ge 0.3) { try { Add-Content (Join-Path $dir 'wait_log.csv') ("{0},{1},{2},GO,{3}" -f (Get-Date -Format 's'), $Lane, $waitedMin, ($result.msg -replace ',', ';')) } catch {} }
    if ($result.slot) { return $result.slot }
    Start-Sleep 20
  } while ((Get-Date) -lt $deadline)
  Write-Host "QA SLOT lane=$Lane TIMEOUT after $WaitMinutes min"
  try { Add-Content (Join-Path $dir 'wait_log.csv') ("{0},{1},{2},TIMEOUT," -f (Get-Date -Format 's'), $Lane, $WaitMinutes) } catch {}
  return $null
}

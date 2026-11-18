# unity-dev: timed full chain, ROBUST version (laneCHAINFIX). usage: chain_timed.ps1 -Name v109n [-Grendel] [-SkipMonster] [-SkipZone] [-ChainDeadline HH:mm]
# Same flags and stage order as before (GrendelRunAll, MonsterBatch, Zone2Build x3, Tools\build_<Name>.ps1, PRUNE). New:
#  * every stage waits on the Editor PROCESS TREE (every Unity.exe whose command line holds the project path, workers included) AND the log sentinel
#    'Exiting batchmode successfully' - never on the launcher alone. A launcher that returns early is logged as EARLY-RETURN and waited out.
#  * every finished stage writes TestOut\<Name>_stage_<key>.ok; the chain loops over its stage list until every marker exists, so a skipped/short-circuited stage is run (idempotent: a stage whose marker exists is skipped).
#  * a detached GUARD (started through WMI, so it is not a child of this process, its console or its job) watches this script's pid. If the script dies without
#    writing TestOut\<Name>_chain_complete.txt (killed window, killed tree, anything), the guard waits for the Editor tree to finish and relaunches this script with -Resume,
#    which picks up at the first stage without a marker. -ChainDeadline (or TestOut\<Name>_chain_stop.txt) stops the guard from resuming.
#  * heartbeat file TestOut\<Name>_chain_hb.txt (every 20 s) and a 'script ending' line in a finally block: if the script is killed, the last heartbeat is the time of death and the missing 'script ending' line proves a kill (not an exception).
param([string]$Name = 'v109n', [switch]$Grendel, [switch]$SkipMonster, [switch]$SkipZone, [string]$ChainDeadline = '',
      [switch]$Resume, [int]$Gen = 0, [switch]$NoGuard, [switch]$GuardMode, [int]$GuardPid = 0,
      [string]$ProjRoot = $env:FK_ROOT, [string]$UnityExe = 'C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe', [switch]$Mock)
$ErrorActionPreference = 'Continue'
$proj = $ProjRoot; Set-Location $proj
$ue = $UnityExe; $projMatch = '*' + (Split-Path $proj -Leaf) + '*'
$log = "$proj\_agents\unitydev\${Name}_timing.txt"; $to = "$proj\TestOut"
$stateF = "$to\${Name}_chain_state.txt"; $doneF = "$to\${Name}_chain_complete.txt"; $stopF = "$to\${Name}_chain_stop.txt"; $hbF = "$to\${Name}_chain_hb.txt"
New-Item -ItemType Directory -Force $to | Out-Null
function Say($s) { try { ((Get-Date -f HH:mm:ss) + ' ' + $s) | Add-Content $log } catch { Start-Sleep -Milliseconds 300; try { ((Get-Date -f HH:mm:ss) + ' ' + $s) | Add-Content $log } catch {} } }
function EditorProcs() { @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object { $_.CommandLine -like $projMatch }) }
function Busy() { @(EditorProcs).Count -gt 0 }
function BuildScriptProcs() { @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like "*build_$Name.ps1*" -and $_.ProcessId -ne $PID }) }
function Hb($what) { try { (Get-Date -f 'yyyy-MM-dd HH:mm:ss') + " pid $PID gen $Gen $what" | Set-Content $hbF } catch {} }
function PastDeadline() { if (-not $ChainDeadline) { return $false }; try { return ((Get-Date) -gt [datetime]::Parse($ChainDeadline)) } catch { return $false } }
function Pass-Args() { $a = "-Name $Name -ProjRoot `"$proj`" -UnityExe `"$ue`""; if ($Grendel) { $a += ' -Grendel' }; if ($SkipMonster) { $a += ' -SkipMonster' }; if ($SkipZone) { $a += ' -SkipZone' }; if ($ChainDeadline) { $a += " -ChainDeadline $ChainDeadline" }; if ($Mock) { $a += ' -Mock' }; $a }
function Start-Detached($psArgs) {   # WMI Create: the new process is NOT a child of this process / console / job object
  $cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`" $psArgs"
  $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd; CurrentDirectory = $proj }
  if ($r.ReturnValue -ne 0) { Say "WARN detached start failed rc $($r.ReturnValue)"; return 0 }; return [int]$r.ProcessId }
function Wait-Gone([int]$maxSec, [string]$label) {   # Editor tree + build script gone
  $t = Get-Date; $n = 0
  while ($true) {
    $e = @(EditorProcs); $b = @(BuildScriptProcs)
    if ($e.Count -eq 0 -and $b.Count -eq 0) { return $true }
    if (((Get-Date) - $t).TotalSeconds -ge $maxSec) { Say "WAIT-GONE ${label}: still alive after $maxSec s (Unity.exe $($e.Count), build script $($b.Count))"; return $false }
    if ($n % 12 -eq 0) { Hb "waiting for $label tree (Unity.exe $($e.Count) build $($b.Count))" }
    $n++; Start-Sleep 5 }
}
function LogOk($logf) { if (-not (Test-Path $logf)) { return $false }; [bool](Select-String -Path $logf -Pattern 'Exiting batchmode successfully' -SimpleMatch -Quiet -ErrorAction SilentlyContinue) }
function Mark($key, $txt) { Set-Content "$to\${Name}_stage_${key}.ok" ("{0} {1}" -f (Get-Date -f 'yyyy-MM-dd HH:mm:ss'), $txt) }
function Marked($key) { Test-Path "$to\${Name}_stage_${key}.ok" }
function Finish($code, $msg) { Say "CHAIN exit ${code}: $msg"; Set-Content $doneF ("{0} exit {1} {2}" -f (Get-Date -f 'yyyy-MM-dd HH:mm:ss'), $code, $msg); exit $code }

# ---------------- guard mode (detached watcher) ----------------
if ($GuardMode) {
  $log2 = $log
  while (Get-Process -Id $GuardPid -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 5 }
  Start-Sleep 3
  if (Test-Path $doneF) { exit 0 }
  if ((Test-Path $stopF) -or (PastDeadline)) { Say "GUARD: chain pid $GuardPid gone, stop file / deadline set: NOT resuming"; exit 0 }
  if ($Gen -ge 4) { Say "GUARD: chain pid $GuardPid died again (gen $Gen), giving up"; exit 0 }
  Say "GUARD: chain pid $GuardPid ended WITHOUT chain_complete marker (killed or crashed); waiting for the Editor tree, then resuming (gen $($Gen + 1))"
  Wait-Gone 2700 'guard' | Out-Null
  if ((Test-Path $stopF) -or (PastDeadline)) { Say 'GUARD: stop file / deadline now set: NOT resuming'; exit 0 }
  $np = Start-Detached ((Pass-Args) + " -Resume -Gen $($Gen + 1)")
  Say "GUARD: resumed chain started pid $np"; exit 0
}

# ---------------- fresh start / resume bookkeeping ----------------
$parent = 0; try { $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId } catch {}
if (-not $Mock -and ((Get-PSDrive C).Free / 1GB) -lt 35) { Say ("REFUSED to start: C: free {0:N1} GB < 35 GB" -f ((Get-PSDrive C).Free / 1GB)); Finish 6 'C: free below 35 GB' }
if (-not $Resume) {
  Get-ChildItem "$to\${Name}_stage_*.ok" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
  Remove-Item $doneF, $stopF -Force -ErrorAction SilentlyContinue
  Set-Content $stateF (Get-Date -f 'yyyy-MM-dd HH:mm:ss')
  Say ("CHAIN $Name begin free GB {0:N1} (pid $PID parent $parent)" -f $(if ($Mock) { 0 } else { (Get-PSDrive C).Free / 1GB }))
} else { $have = ((Get-ChildItem "$to\${Name}_stage_*.ok" -ErrorAction SilentlyContinue | % { $_.Name -replace "^${Name}_stage_|\.ok$", '' }) -join ','); Say "CHAIN $Name RESUME gen $Gen (pid $PID parent $parent); markers present: $have" }
$chainStart = [datetime]::Parse((Get-Content $stateF -ErrorAction SilentlyContinue | Select-Object -First 1))
$t0 = $chainStart
if (-not $NoGuard) { $gp = Start-Detached ("-Name $Name -GuardMode -GuardPid $PID -Gen $Gen " + ((Pass-Args) -replace '^-Name \S+ ', '')); Say "guard pid $gp watching chain pid $PID" }

# ---------------- Editor stage: waits on the tree + sentinel, never on the launcher alone ----------------
function Run($label, $arg, $logf, $key) {
  $t = Get-Date
  if ($Resume) {
    if (Busy) { Say "RESUME: Editor tree still alive before $label, waiting for it (sentinel decides)"; Wait-Gone 2700 $label | Out-Null }
    if ((Test-Path $logf) -and (Get-Item $logf).LastWriteTime -ge $chainStart -and (LogOk $logf)) { Say "$label finished while the script was down: log sentinel found (log newer than chain start), exit assumed 0"; return 0 }
  } elseif (Busy) { Say "REFUSED ${label}: project Unity running"; Finish 2 "REFUSED $label" }
  Remove-Item $logf -Force -ErrorAction SilentlyContinue
  Say "start $label"
  $pr = Start-Process $ue -ArgumentList "$arg -projectPath $proj -logFile $logf" -PassThru
  $launcherPid = $pr.Id; $limit = (Get-Date).AddMinutes(40); $n = 0
  while (-not $pr.HasExited) { if ((Get-Date) -gt $limit) { Say "$label TIMEOUT 40 min: killing the Editor tree"; EditorProcs | % { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; break }; if ($n % 4 -eq 0) { Hb "$label launcher pid $launcherPid alive" }; $n++; [void]$pr.WaitForExit(5000) }
  $rc = $null; try { $rc = $pr.ExitCode } catch { $rc = $null }
  $early = $false
  Say ("{0}: launcher pid $launcherPid exited rc $rc after {1:N0} s; project Unity.exe alive: {2}; sentinel: {3}" -f $label, ((Get-Date) - $t).TotalSeconds, @(EditorProcs).Count, (LogOk $logf))
  $settle = (Get-Date).AddSeconds(20)   # a just-spawned child may not be listed yet (seen in the mock test): give it 20 s to show up or to write the sentinel
  while (-not (Busy) -and -not (LogOk $logf) -and (Get-Date) -lt $settle) { Start-Sleep -Seconds 2 }
  if (Busy) {
    $early = $true; $e = @(EditorProcs)
    Say ("EARLY-RETURN: $label launcher pid $launcherPid exited (rc $rc) after {0:N0} s but {1} Unity.exe of the project are still alive: waiting for the tree" -f ((Get-Date) - $t).TotalSeconds, $e.Count)
    if (-not (Wait-Gone 2400 $label)) { Say "$label tree did not finish in 40 min: killing"; EditorProcs | % { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } }
  }
  $ok = LogOk $logf
  if ($null -eq $rc) { if ($ok) { $rc = 0 } else { $rc = 1 } }
  if ($early) { if ($ok) { $rc = 0 } elseif ($rc -eq 0) { $rc = 1; Say "${label}: early return with exit 0 but NO sentinel in the log: counted as failed" } }
  Say ("end $label exit $rc secs {0:N0} sentinel $ok" -f ((Get-Date) - $t).TotalSeconds)
  return $rc
}
function Stage-Grendel() {
  $O = "$proj\_agents\laneMONSTER3\out"
  $rc = Run 'GrendelRunAll' "-batchmode -nographics -executeMethod SobekUnityBatch.RunAll -sobekCfg $proj\_agents\laneMONSTER3\data\grendel_unity_cfg.json -sobekOut $O" "$O\grendel_unity_runall.log" 'grendel'
  if ($rc -ne 0) { Say 'GrendelRunAll FAILED, chain stopped'; Finish 4 'GrendelRunAll failed' }
  Mark 'grendel' "rc 0" }
function Stage-Monster() {
  $rc = Run 'MonsterBatch' '-batchmode -quit -accept-apiupdate -silent-crashes -executeMethod MonsterBatch.Run' "$to\${Name}_MonsterBatch.log" 'monster'
  Mark 'monster' "rc $rc" }   # soft stage like before: a failed MonsterBatch does not stop the chain
function Stage-Zone() {
  $zc = 1
  for ($z = 1; $z -le 3 -and $zc -ne 0; $z++) { $zc = Run "Zone2Build try$z" '-batchmode -quit -accept-apiupdate -silent-crashes -executeMethod Zone2BuildBatch.Run' "$to\${Name}_Zone2Batch.log" 'zone' }
  if ($zc -ne 0) { Say 'Zone2Build FAILED 3 times, chain stopped (no build on a half-built scene)'; Finish 5 'Zone2Build failed 3 times' }
  Mark 'zone' 'rc 0' }
function Stage-Build() {
  $t = Get-Date
  if ($Resume) { if (-not (Wait-Gone 2700 'BuildScript')) { Say 'BuildScript tree still alive after 45 min' } ; $d = "$to\${Name}_done.txt"; if ((Test-Path $d) -and (Get-Item $d).LastWriteTime -ge $chainStart) { Say 'BuildScript finished while the script was down: done.txt is newer than the chain start'; Mark 'build' 'resumed-done'; return } }
  Say 'start BuildScript'
  $bp = Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File $proj\Tools\build_$Name.ps1" -PassThru -WindowStyle Minimized
  $n = 0; while (-not $bp.HasExited) { if ($n % 4 -eq 0) { Hb "BuildScript pid $($bp.Id) alive" }; $n++; [void]$bp.WaitForExit(5000) }
  if (-not (Wait-Gone 2700 'BuildScript')) { Say 'BuildScript Editor tree still alive after 45 min' }
  Say ("end BuildScript secs {0:N0}" -f ((Get-Date) - $t).TotalSeconds)
  Mark 'build' 'done' }

$plan = New-Object System.Collections.ArrayList
if ($Grendel) { [void]$plan.Add(@('grendel', 'Stage-Grendel')) }
if (-not $SkipMonster) { [void]$plan.Add(@('monster', 'Stage-Monster')) } else { Say 'MonsterBatch SKIPPED (-SkipMonster)' }
if (-not $SkipZone) { [void]$plan.Add(@('zone', 'Stage-Zone')) } else { Say 'Zone2Build SKIPPED (no scene-baked data changed: collider defaults 80/1)' }
[void]$plan.Add(@('build', 'Stage-Build'))
try {
  for ($pass = 1; $pass -le 3; $pass++) {
    $missing = 0
    foreach ($st in $plan) {
      $key = $st[0]
      if (Marked $key) { if ($pass -eq 1 -and $Resume) { Say "stage $key already done (marker), skipped" }; continue }
      if (PastDeadline) { Finish 7 "chain deadline $ChainDeadline reached before stage $key" }
      if (Test-Path $stopF) { Finish 8 "stop file present before stage $key" }
      Hb "stage $key (pass $pass)"; & $st[1]
      if (-not (Marked $key)) { Say "CHECK FAILED: stage $key returned without its marker (pass $pass)"; $missing++ } else { Say "CHECK ok: stage $key marker written" }
    }
    $left = @($plan | Where-Object { -not (Marked $_[0]) })
    if ($left.Count -eq 0) { break }
    Say ("CHECK: stages without marker after pass ${pass}: " + (($left | % { $_[0] }) -join ',') + ' -> running them now')
  }
  $left = @($plan | Where-Object { -not (Marked $_[0]) }); if ($left.Count -gt 0) { Finish 9 ('stages never completed: ' + (($left | % { $_[0] }) -join ',')) }
  Say ("CHAIN total secs {0:N0}; free GB {1:N1}; done: {2}" -f ((Get-Date) - $t0).TotalSeconds, $(if ($Mock) { 0 } else { (Get-PSDrive C).Free / 1GB }), (Get-Content "$to\${Name}_done.txt" -ErrorAction SilentlyContinue))
  # Dom standing order 2026-11-08: keep only the last 3 builds (never a launcher target); log in prune_builds.log
  if ($Mock) { Say 'PRUNE skipped (mock)' }
  elseif (Test-Path "$proj\Builds\HDRP_$Name\HadesSlice.exe") { try { Say ("PRUNE " + (& powershell -NoProfile -File "$proj\Tools\prune_builds.ps1")) } catch { Say ("PRUNE failed: " + $_) } } else { Say "PRUNE skipped: no HDRP_$Name\HadesSlice.exe (build failed)" }
  Set-Content $doneF ("{0} exit 0 complete" -f (Get-Date -f 'yyyy-MM-dd HH:mm:ss'))
}
catch { Say ("CHAIN script ERROR: " + $_.Exception.Message + ' at ' + $_.InvocationInfo.PositionMessage.Replace("`n", ' ')); throw }
finally { Say ("script ending (pid $PID, complete marker " + (Test-Path $doneF) + ')') }

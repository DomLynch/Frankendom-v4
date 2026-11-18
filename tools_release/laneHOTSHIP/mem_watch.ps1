# mem_watch.ps1: 2 s memory log + own-pid stop (COO 23:16 numbers). Stops MY Editor (project unity_test_hdrp) only, posts to the board.
# Dom 17 Nov 09:57 (Strategy+COO): the ONLY stop is the own-pid stop at free RAM < 4 GB. No game-start stop, no 10 GB / 12 GB caps, no Dom-game rule.
# -DryRun: no process is touched and nothing is posted; MEMWATCH_FAKE_FREE / MEMWATCH_FAKE_WS (GB) replace the readings, loops 3 samples, prints the verdict.
param([string]$Tag='pe', [switch]$DryRun, [switch]$Cold)   # -Cold (COO 23:27): a cold take legitimately reaches ~33 GB, so NO own working-set cap; stop only at free RAM < 10 GB (and the immediate < 4 GB)
$done="$($env:FK_ROOT)\TestOut\v109${Tag}_done.txt"; $log="$($env:FK_ROOT)\TestOut\v109${Tag}_mem.csv"
$board="$env:FK_ROOT\_agents\COORDINATION.md"
if (-not $DryRun) { 'time,freeGB,myUnityGB,peakMyUnityGB,openScenesInLog' | Out-File $log -Encoding ascii }
$peak=0; $n=0
function Verdict($free, $ws) {
  if ($free -lt 4)  { return "free RAM $free GB under 4 GB (immediate)" }
  # (stopFree 8/10 and the own-size 12 GB cap REMOVED 17 Nov 09:57: -Cold is now the same as the default)
  $null = $Cold
  return $null
}
while ($DryRun -or -not (Test-Path $done)) {
  $free=[math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB,1)
  $mine=@(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | ? { $_.CommandLine -match 'unity_test_hdrp' })
  $ws=0; foreach($m in $mine){ $p=Get-Process -Id $m.ProcessId -EA SilentlyContinue; if($p){ $ws+=$p.WorkingSet64/1GB } }; $ws=[math]::Round($ws,1)
  if ($env:MEMWATCH_FAKE_FREE) { $free=[double]$env:MEMWATCH_FAKE_FREE }; if ($env:MEMWATCH_FAKE_WS) { $ws=[double]$env:MEMWATCH_FAKE_WS }
  if($ws -gt $peak){$peak=$ws}
  $why = Verdict $free $ws
  if (-not $DryRun) {
    $sc=0; $bl="$($env:FK_ROOT)\TestOut\v109${Tag}_Build.log"; if(Test-Path $bl){ $sc=(Select-String $bl -Pattern '^Opening scene' -EA SilentlyContinue).Count }
    ("{0},{1},{2},{3},{4}" -f (Get-Date -Format HH:mm:ss),$free,$ws,$peak,$sc) | Add-Content $log
    # (a game starting no longer stops the build: rule REMOVED 17 Nov 09:57)
    if ($why -and $mine.Count -gt 0) {
      foreach ($m in $mine) { Stop-Process -Id $m.ProcessId -Force -EA SilentlyContinue }
      Add-Content $board ("`r`n" + (Get-Date -Format 'yyyy-MM-dd HH:mm') + " Frankendom unity-dev: OWN-PID STOP of my Editor ($Tag): $why; my Unity peak $peak GB; scenes opened in log $sc. EDITOR RELEASED, build not finished.")
      break
    }
  } else {
    $n++; "DRYRUN sample $n : free=$free GB my_unity=$ws GB -> " + $(if ($why) { "WOULD STOP: $why" } else { 'ok, no stop' })
    if ($n -ge 3) { break }
  }
  Start-Sleep 2
}

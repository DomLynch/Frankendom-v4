# chain_fast.ps1 - laneBUILDSPEED. The MonsterBatch -> Zone2BuildBatch -> player build chain with fingerprint skips. DRY RUN by default (prints the plan and the exact Unity commands).
#   powershell -NoProfile -File _agents\laneBUILDSPEED\chain_fast.ps1 -v v109l                 plan: which Unity launches are needed and why (no Unity, no writes)
#   powershell -NoProfile -File _agents\laneBUILDSPEED\chain_fast.ps1 -v v109l -Run            run only what the plan says; only the Unity dev runs this, with the slot (UNITY_SLOT.md) held
#   -Force monster,zones,player,all   run those steps regardless      -NoPlayer  stop after the batches
# Log: TestOut\<v>_chain_fast.log = one line per step: RAN/SKIPPED, why, seconds. A step's fingerprint is stored (fp.py record) only after its Unity run exited 0.
# Changing 1 zone of N rebuilds only that zone: the zones launch writes Tools\zone_skip.json (clean zones, 2 h expiry) that the Zone2Build hunk (apply_buildspeed.py) reads.
# Without the hunk the zones launch rebuilds every zone (same as today) but is still skipped when ALL zones are clean.
param([Parameter(Mandatory = $true)][string]$v, [switch]$Run, [string[]]$Force = @(), [switch]$NoPlayer)
$ErrorActionPreference = 'Stop'
$P = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path; Set-Location $P
if (-not $v.StartsWith('v')) { $v = "v$v" }
$cfg = Get-Content -Raw (Join-Path $P 'Tools\build_config.json') | ConvertFrom-Json
$u = [string]$cfg.paths.unityExe; $uargs = @($cfg.batch.unityArgs)
$testOut = Join-Path $P $cfg.paths.testOutDir; $log = Join-Path $testOut ($v + '_chain_fast.log')
$fp = Join-Path $PSScriptRoot 'fp.py'
function Say([string]$s) { $l = (Get-Date -Format HH:mm:ss) + ' ' + $s; Write-Host $l; if ($Run) { Add-Content $log $l } }
$fa = if ($Force.Count) { @('--force', (($Force | ForEach-Object { $_ -split ',' }) -join ',')) } else { @() }
$planArgs = @('-I', $fp, 'plan') + $fa; if ($Run) { $planArgs += '--write-skip' }
$plan = & python @planArgs 2>&1 | Out-String; Write-Host $plan
$runMon = $plan -match 'LAUNCH monster : yes'; $runZones = $plan -match 'LAUNCH zones   : yes'; $runPlayer = ($plan -match 'LAUNCH player  : yes') -and -not $NoPlayer
if (-not $Run) {
    Write-Host ("DRY RUN. Unity launches that would run: monster=" + $runMon + " zones=" + $runZones + " player=" + $runPlayer)
    if ($runMon) { Write-Host ("  `"$u`" " + ($uargs -join ' ') + " -projectPath `"$P`" -executeMethod MonsterBatch.Run -logFile `"$testOut\${v}_MonsterBatch.log`"") }
    if ($runZones) { Write-Host ("  `"$u`" " + ($uargs -join ' ') + " -projectPath `"$P`" -executeMethod Zone2BuildBatch.Run -logFile `"$testOut\${v}_Zone2Batch.log`"") }
    if ($runPlayer) { Write-Host ("  powershell -File Tools\build_and_gate.ps1 -v $v -NoGate   (HdrpBatch.Production, build folder Builds\HDRP_$v)") }
    exit 0
}
. (Join-Path $P 'Tools\lib\hckit3_common.ps1')
if (-not (Test-Path $u)) { throw "Unity.exe not found: $u" }
$busy = @(Get-Process -Name 'Unity', 'HadesSlice' -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'HadesSlice' -or ($_.Path -and $_.Path -like '*6000.6.7f1*') })
$prj = & python -I (Join-Path $P '_agents\pool\unity_busy.py') 2>&1 | Out-String
if ($prj -match 'BUSY') { throw "Unity is busy on this project: $prj" }
New-Item -ItemType Directory -Force $testOut | Out-Null
function Unity-Step([string]$name, [string]$method, [string]$logFile) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $a = $uargs + @('-projectPath', ('"' + $P + '"'), '-executeMethod', $method, '-logFile', ('"' + $logFile + '"'))
    $b = Start-Process $u -ArgumentList $a -PassThru
    if (-not $b.WaitForExit(2400000)) { $b.Kill(); Say "$name TIMEOUT"; return $false }
    Say ("RAN     $name exit " + $b.ExitCode + " in " + [math]::Round($sw.Elapsed.TotalSeconds) + " s")
    return ($b.ExitCode -eq 0)
}
if ($runMon) { if (Unity-Step 'monster' 'MonsterBatch.Run' "$testOut\${v}_MonsterBatch.log") { & python -I $fp record monster --apply | Out-Null } else { throw 'MonsterBatch failed: chain stopped' } } else { Say 'SKIPPED monster (inputs and outputs unchanged)' }
if ($runZones) { if (Unity-Step 'zones' 'Zone2BuildBatch.Run' "$testOut\${v}_Zone2Batch.log") { & python -I $fp record zones --apply | Out-Null } else { throw 'Zone2BuildBatch failed: chain stopped' } } else { Say 'SKIPPED zones (every zone clean)' }
if ($runPlayer) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $P 'Tools\build_and_gate.ps1') -v $v -NoGate
    $ok = Test-Path (Join-Path $P ("Builds\HDRP_" + $v + "\build_info.json"))
    Say ("RAN     player build ok=" + $ok + " in " + [math]::Round($sw.Elapsed.TotalSeconds) + " s")
    if ($ok) { & python -I $fp record player --apply | Out-Null }
} else { Say 'SKIPPED player (no code/scene/asset input changed; data-only changes: deploy_data.ps1)' }

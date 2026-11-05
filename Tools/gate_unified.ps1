# HCKIT3 v93: ONE gate script (replaces Tools\gate.ps1 + Tools\gate_lean.ps1). All numbers live in Tools\gate_config.json, the exe / _Data names in <build>\build_info.json
# (older builds: Tools\build_config.json build.exeName). Same outputs as before in the build folder: gate_mem.log, gate_run.txt, gate_done.txt, qc_visual.txt, *_player.log.
#   old:  gate.ps1 -v v90                      new:  gate_unified.ps1 -v v90 -Profile full
#   old:  gate_lean.ps1 -v v90 -proofs loop    new:  gate_unified.ps1 -v v90 -Profile lean -proofs loop      (lean is the default)
# -proofs accepts loop,cam style lists even through powershell -File. -BuildDir overrides Builds\HDRP_<v>. -DryRun prints the plan and launches nothing.
param(
    [Parameter(Mandatory = $true)][string]$v,
    [ValidateSet('full', 'lean')][string]$Profile = 'lean',
    [string]$mode = '',
    [string[]]$proofs = @(),
    [string]$BuildDir = '',
    [switch]$DryRun
)
. "$PSScriptRoot\lib\hckit3_common.ps1"
if ($mode -eq 'full' -or $mode -eq 'lean') { $Profile = $mode }   # old gate.ps1 had a (unused) -mode parameter
$proofs = @($proofs | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$root = Get-ProjectRoot
$bcfg = Get-BuildConfig
$g = Get-GateConfig
if ($BuildDir) { $d = $BuildDir } else { $d = Join-Path $root ($bcfg.build.buildRoot + '\' + $bcfg.build.buildDirPrefix + $v) }
if (-not (Test-Path -LiteralPath $d)) { throw "build folder not found: $d" }
$bi = Get-BuildInfo $d $bcfg
if (-not (Test-Path -LiteralPath $bi.ExePath)) { throw "exe not found: $($bi.ExePath)" }
Set-Location $d
$flagsPath = Join-Path $bi.DataPath 'StreamingAssets\runtime_flags_balance.json'
$memLog = $g.outputs.memLog

function Select-Steps {
    $sel = @()
    foreach ($s in $g.steps) {
        $inProfile = @($s.profiles) -contains $Profile
        $byProof = ($s.proof) -and ($proofs -contains $s.proof)
        if ($inProfile -or $byProof) { $sel += $s }
    }
    return $sel
}

function Expand-Flags($step) {
    $o = @()
    foreach ($f in $step.flags) { $o += ([string]$f).Replace('{out}', [string]$step.outDir).Replace('{flagsPath}', $flagsPath) }
    return $o
}

function Invoke-GateStep($step) {
    $name = [string]$step.log
    $res = $g.resolutions.($step.res); if (-not $res) { $res = $g.resolutions.default }
    $w = $res[0]; $h = $res[1]
    $argList = @(Expand-Flags $step) + @($g.commonFlags) + @("-screen-width $w", "-screen-height $h", "-logFile $name.log")
    if ($DryRun) { return "DRYRUN $name : $($bi.Exe) $($argList -join ' ') (timeout $($step.timeoutSec) s)" }
    if ($step.outDir) { Move-Aside (Join-Path $d $step.outDir) }
    $p = Start-Process $bi.ExePath -ArgumentList $argList -PassThru -WorkingDirectory $d
    $t0 = Get-Date; $killed = $false; $memKilled = $false; $timedOut = $false; $nx = 0
    while (-not $p.HasExited) {
        Start-Sleep $g.pollSec; $p.Refresh()
        $ws = [math]::Round($p.WorkingSet64 / 1GB, 1); $el = ((Get-Date) - $t0).TotalSeconds
        if ($el -ge $nx) { Add-Content $memLog ("{0} {1} {2:F0}s {3} GB" -f (Get-Date -Format HH:mm:ss), $name, $el, $ws); $nx += $g.memLogEverySec }
        if ($ws -gt $g.memKillGB) { $p.Kill(); $killed = $true; $memKilled = $true; Add-Content $memLog "$name MEMKILL at $ws GB"; break }
        if ($el -gt $step.timeoutSec) { $p.Kill(); $killed = $true; $timedOut = $true; break }
    }
    if ($step.copyAfter -and (Test-Path -LiteralPath $step.copyAfter.from)) { Copy-Item -LiteralPath $step.copyAfter.from -Destination $step.copyAfter.to -Force }
    if ($memKilled) { return "$name MEMKILL (working set > $($g.memKillGB) GB, see $memLog)" }
    if ($timedOut) { return "$name TIMEOUT" }
    return "$name exit=$($p.ExitCode)"
}

$steps = @(Select-Steps)
"gate_unified: build=$d exe=$($bi.Exe) profile=$Profile proofs=[$($proofs -join ',')] steps=[$((($steps | ForEach-Object { $_.name }) -join ','))]"
if (-not $DryRun) { Move-Aside (Join-Path $d $g.outputs.doneMarker) }
$out = @()
foreach ($s in $steps) { $line = Invoke-GateStep $s; $out += $line; if ($DryRun) { $line } }
if ($DryRun) {
    "DRYRUN qc: $($g.pythonExe) $(Join-Path $root $g.qcScript) $d"
    return
}
$qc = & $g.pythonExe (Join-Path $root $g.qcScript) $d
$qc | Set-Content $g.outputs.qcReport
$out += ($qc -match 'QC RESULT:.*')
$out | Set-Content $g.outputs.runSummary
'done' | Set-Content $g.outputs.doneMarker

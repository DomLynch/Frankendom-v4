# HCKIT3 v93: ONE parameterised build + gate script. Replaces the ~31 Tools\build_and_gate_vNN.ps1 clones and the ~64 Tools\run_vNN.ps1 clones.
#   Tools\build_and_gate.ps1 -v v92                                 same as v91: HdrpBatch.Production build -> lean gate -> -proofs loop
#   Tools\build_and_gate.ps1 -v v92 -Pre finisher                   same as v90: FinisherClipSetup.RunBatch in its own Unity run first
#   Tools\build_and_gate.ps1 -v v92 -Pre joan -Py 'Tools\bake_attack_clips.py Joan','Tools\bake_dodge_clips.py Joan'   same as v76
#   Tools\build_and_gate.ps1 -v v92 -Profile full -proofs loop      same as v88 (full gate + loop)
#   -NoGraphics   adds -nographics to the Unity runs (iGPU / D3D12 out-of-memory at editor start, v91 try 1). Steps that render (HdrpShot) must be off in build_config.json chain.* then.
#   -Chain V51    runs the old chain instead of HdrpBatch.Production.   -NoGate builds only.   -DryRun prints every command, launches nothing.
# Unity path, exe name, build folder prefix, timeouts: Tools\build_config.json. No Tools\build_dir.txt (the output folder goes to Unity as -buildDir). Nothing is deleted.
param(
    [Parameter(Mandatory = $true)][string]$v,
    [string]$Chain = '',
    [string[]]$Pre = @(),
    [string[]]$Py = @(),
    [ValidateSet('full', 'lean')][string]$Profile = 'lean',
    [string[]]$proofs = @('loop'),
    [switch]$NoGraphics,
    [switch]$NoGate,
    [switch]$Force,
    [switch]$DryRun
)
. "$PSScriptRoot\lib\hckit3_common.ps1"
$p = Get-ProjectRoot
$cfg = Get-BuildConfig
$u = [string]$cfg.paths.unityExe
if (-not $Chain) { $Chain = [string]$cfg.chain.name }
$Pre = @($Pre | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$proofs = @($proofs | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$testOut = Join-Path $p $cfg.paths.testOutDir
$buildRel = ($cfg.build.buildRoot + '/' + $cfg.build.buildDirPrefix + $v)      # forward slashes: Unity gets it as -buildDir
$buildDir = Join-Path $p ($cfg.build.buildRoot + '\' + $cfg.build.buildDirPrefix + $v)
Set-Location $p

# ---- preflight (no Unity yet)
if (-not $DryRun -and -not (Test-Path -LiteralPath $u)) { throw "Unity.exe not found: $u (edit build_config.json paths.unityExe)" }
$batchSrc = Get-Content -Raw (Join-Path $p 'Assets\Editor\HdrpBatch.cs')
if ($batchSrc -notmatch ('public static void ' + [regex]::Escape($Chain) + '\s*\(')) { throw "HdrpBatch.$Chain does not exist: merge the HCKIT3 HdrpBatch.cs first, or pass -Chain V51" }
foreach ($k in $Pre) { if (-not $cfg.preSteps.$k) { throw "unknown -Pre '$k' (known: $((($cfg.preSteps | Get-Member -MemberType NoteProperty).Name) -join ', '))" } }
if (-not $DryRun -and -not $Force) {
    $exeBase = [System.IO.Path]::GetFileNameWithoutExtension([string]$cfg.build.exeName)
    $busy = @(Get-Process -Name 'Unity', $exeBase -ErrorAction SilentlyContinue)
    if ($busy.Count -gt 0) { throw "another heavy job is running ($(($busy | ForEach-Object { $_.Name + ' pid ' + $_.Id }) -join ', ')): one heavy job at a time. Close it or pass -Force." }
}
if (-not $DryRun) { New-Item -ItemType Directory -Force $testOut | Out-Null }

function Invoke-UnityStep([string]$method, [string]$logFile, [int]$timeoutSec, [string[]]$extra) {
    $a = @($cfg.batch.unityArgs)
    if ($NoGraphics) { $a += '-nographics' }
    $a += @('-projectPath', ('"' + $p + '"'), '-executeMethod', $method)
    $a += @($extra)
    $a += @('-logFile', ('"' + $logFile + '"'))
    if ($DryRun) { Write-Host "DRYRUN unity: $u $($a -join ' ') (timeout $timeoutSec s)"; return 0 }
    $b = Start-Process $u -ArgumentList $a -PassThru
    if (-not $b.WaitForExit($timeoutSec * 1000)) { $b.Kill(); return -999 }
    return $b.ExitCode
}

# ---- 1. optional separate Unity runs (v90: FinisherClipSetup.RunBatch, v76/v82: JoanSwap.RunAllBatch)
foreach ($k in $Pre) {
    $s = $cfg.preSteps.$k
    $marker = Join-Path $testOut ($v + '_' + $s.tag + '_done.txt')
    if (-not $DryRun) { Move-Aside $marker }
    $rc = Invoke-UnityStep $s.method (Join-Path $testOut ($v + '_' + $s.logSuffix + '.log')) ([int]$s.timeoutSec) @()
    $rc = @($rc)[-1]
    if (-not $DryRun) { Set-Content $marker ($s.tag + '=' + $rc) }
}
# ---- 2. optional python steps (v76: bake_attack_clips.py / bake_dodge_clips.py)
$i = 0
foreach ($line in $Py) {
    $i++
    $plog = Join-Path $testOut ($v + '_py' + $i + '.log')
    if ($DryRun) { Write-Host "DRYRUN python: cmd /c `"python $line > $plog 2>&1`""; continue }
    cmd /c "python $line > `"$plog`" 2>&1"
}
# ---- 3. the build (HdrpBatch.<Chain>); the output folder is an argument, not a global file
$doneFile = Join-Path $testOut ($v + '_done.txt')
$buildLog = Join-Path $testOut ($v + '_Build.log')
if (-not $DryRun) { Move-Aside $doneFile }
$tBuild = Get-Date
$rc = Invoke-UnityStep ('HdrpBatch.' + $Chain) $buildLog ([int]$cfg.batch.buildTimeoutSec) @('-buildDir', $buildRel, '-buildTag', $v)
$rc = @($rc)[-1]
if (-not $DryRun) { Set-Content $doneFile ('build=' + $rc) }

# ---- 4. did the build produce a fresh exe + data, and no 'Result: Failure' in the log?
$gateArgs = @('-ExecutionPolicy', 'Bypass', '-File', (Join-Path $p 'Tools\gate_unified.ps1'), '-v', $v, '-Profile', $Profile)
if ($proofs.Count -gt 0) { $gateArgs += @('-proofs', ($proofs -join ',')) }
if ($DryRun) {
    Write-Host "DRYRUN check: exe + _Data\StreamingAssets in $buildDir, exe newer than build start, no '$($cfg.batch.buildFailedMarker)' in $buildLog"
    if (-not $NoGate) { Write-Host "DRYRUN gate: powershell $($gateArgs -join ' ')" }
    return
}
$bi = Get-BuildInfo $buildDir $cfg
$fresh = (Test-Path -LiteralPath $bi.ExePath) -and ((Get-Item -LiteralPath $bi.ExePath).LastWriteTime -ge $tBuild.AddMinutes(-1))
$ok = $fresh -and (Test-Path -LiteralPath (Join-Path $bi.DataPath 'StreamingAssets')) -and -not (Test-BuildLogFailed $buildLog ([string]$cfg.batch.buildFailedMarker))
if ($ok) {
    if (-not $NoGate) { & powershell @gateArgs }
} else {
    Set-Content (Join-Path $p ($cfg.build.buildRoot + '\' + $v + '_BUILD_FAILED.txt')) 'build produced no fresh exe, no StreamingAssets, or the build log says Result: Failure'
}

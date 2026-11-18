# laneFIX2 (FIX2 v1): ONE-command RELEASE build. The dev build is unchanged (Tools\build_and_gate.ps1 / build_vNNN.ps1 still build with the harnesses in).
#   Tools\build_release.ps1 -v v110                 static gates -> compile (both defines) -> Unity build with -noharness -> verify -> md5 tree==build -> prints the run order
#   Tools\build_release.ps1 -v v110 -DryRun         prints every command, launches nothing, writes nothing (use it to read the plan)
#   -SkipStatic   skip the static gates and the compile check (Unity dev already ran them for this tree)
#   -SkipMd5      skip the Data-vs-build comparison      -Force   build even if Unity / the game is running
#   -Method HdrpBatch.V51   the Unity entry point (default = the production chain, which ends in BuildPlayerCmd.Run). -Method BuildPlayerCmd.Run builds the player only (no look / world steps).
# What -noharness does: BuildPlayerCmd.Run adds FRANKEN_NO_HARNESS to THIS player build (Assets\Editor\BuildPlayer.cs, FIXHARNESS v1), which compiles ~31 test / proof / capture files out.
#   The ship therefore CANNOT run -looptest / -physproof / -balanceauto / run-through ...; proofs belong on the dev build of the same tree. Nothing is deleted; no setting file is written.
param(
    [Parameter(Mandatory = $true)][string]$v,
    [string]$Method = 'HdrpBatch.V51',
    [switch]$DryRun,
    [switch]$SkipStatic,
    [switch]$SkipMd5,
    [switch]$Force,
    [int]$Tries = 4
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\lib\hckit3_common.ps1"
$p = Get-ProjectRoot
$cfg = Get-BuildConfig
$u = [string]$cfg.paths.unityExe
$py = 'python'
$testOut = Join-Path $p $cfg.paths.testOutDir
$buildRel = ($cfg.build.buildRoot + '/' + $cfg.build.buildDirPrefix + $v)
$buildDir = Join-Path $p ($cfg.build.buildRoot + '\' + $cfg.build.buildDirPrefix + $v)
$buildLog = Join-Path $testOut ($v + '_Build.log')
$step = 0
function Say([string]$m) { Write-Host ("[release {0}] {1}" -f $v, $m) }
function Stop-Release([string]$why) { Write-Host ("RELEASE FAIL: {0}" -f $why) -ForegroundColor Red; exit 1 }
function Run-Step([string]$name, [scriptblock]$sb, [string]$shown) {
    $script:step++
    if ($DryRun) { Say ("DRYRUN step {0} {1}: {2}" -f $script:step, $name, $shown); return }
    Say ("step {0} {1}" -f $script:step, $name)
    & $sb
    if ($LASTEXITCODE -ne 0) { Stop-Release ("{0} failed (exit {1})" -f $name, $LASTEXITCODE) }
}
Set-Location $p

# ---- 0. preflight
if (-not $DryRun) {
    if (-not (Test-Path -LiteralPath $u)) { Stop-Release "Unity.exe not found: $u (edit Tools\build_config.json paths.unityExe)" }
    $exeBase = [System.IO.Path]::GetFileNameWithoutExtension([string]$cfg.build.exeName)
    $busy = @(Get-Process -Name 'Unity', $exeBase -ErrorAction SilentlyContinue)
    if ($busy.Count -gt 0 -and -not $Force) { Stop-Release ("another heavy job is running ({0}): one heavy job at a time. Close it or pass -Force." -f (($busy | ForEach-Object { $_.Name + ' pid ' + $_.Id }) -join ', ')) }
    if (Test-Path -LiteralPath $buildDir) { Stop-Release "$buildDir already exists: pick a new -v (the script never deletes a build)" }
    New-Item -ItemType Directory -Force $testOut | Out-Null
}
foreach ($f in 'build_dev.txt', 'build_gpupass.txt', 'build_profile.txt') {
    if (Test-Path -LiteralPath (Join-Path $p ('Tools\' + $f))) { Say ("NOTE Tools\{0} exists: it changes the player build ({1}). -releasebuild below forces a non-development build; remove the file for a clean ship (the script never deletes it)." -f $f, $(if ($f -eq 'build_profile.txt') { 'Frame Timing Stats on' } elseif ($f -eq 'build_dev.txt') { 'development flag, overridden by -releasebuild' } else { 'GPU-pass timing define' })) }
}
$batchSrc = Get-Content -Raw (Join-Path $p 'Assets\Editor\HdrpBatch.cs')
$bpSrc = Get-Content -Raw (Join-Path $p 'Assets\Editor\BuildPlayer.cs')
if ($bpSrc -notmatch 'FRANKEN_NO_HARNESS' -or $bpSrc -notmatch '-noharness') { Stop-Release 'Assets\Editor\BuildPlayer.cs has no -noharness switch (FIXHARNESS v1): merge it first' }
$short = $Method.Split('.')[-1]
if ($Method -like 'HdrpBatch.*' -and $batchSrc -notmatch ('public static void ' + [regex]::Escape($short) + '\s*\(')) { Stop-Release "$Method does not exist in Assets\Editor\HdrpBatch.cs" }

# ---- 1. static gates (hard stops)
if (-not $SkipStatic) {
    Run-Step 'hardcode gate'      { & $py -I (Join-Path $p 'Tools\hc_gate.py') --check --quiet } 'python -I Tools\hc_gate.py --check --quiet   (exit 1 = new literals in a hot category)'
    Run-Step 'data gates'         { & $py -I (Join-Path $p 'Tools\data_gates.py') } 'python -I Tools\data_gates.py'
    Run-Step 'missing keys'       { & $py -I (Join-Path $p 'Tools\missing_keys.py') } 'python -I Tools\missing_keys.py'
    Run-Step 'unknown json keys'  { & $py -I (Join-Path $p 'Tools\json_keys_gate.py') --max-unknown 114 --quiet } 'python -I Tools\json_keys_gate.py --max-unknown 114 --quiet   (ratchet: lower the number when keys are fixed, never raise it)'
    Run-Step 'compile, harness ON'  { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $p 'Tools\compile_check_defs.ps1') -Out (Join-Path $env:TEMP 'release_cc') } 'Tools\compile_check_defs.ps1   (0 errors)'
    Run-Step 'compile, FRANKEN_NO_HARNESS' { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $p 'Tools\compile_check_defs.ps1') -NoHarness -Out (Join-Path $env:TEMP 'release_cc') } 'Tools\compile_check_defs.ps1 -NoHarness   (0 errors: the release define compiles)'
}

# ---- 2. the build: Unity batch, -noharness (the only difference to the dev build), output folder as an argument
$a = @($cfg.batch.unityArgs) + @('-projectPath', ('"' + $p + '"'), '-executeMethod', $Method, '-noharness', '-releasebuild', '-buildDir', $buildRel, '-buildTag', $v, '-logFile', ('"' + $buildLog + '"'))
if ($DryRun) {
    Say ("DRYRUN step build: `"$u`" $($a -join ' ')   (retries up to $Tries x when the log says 'threw exception', like the build_vNNN scripts)")
} else {
    $tBuild = Get-Date; $rc = -1
    for ($try = 1; $try -le $Tries; $try++) {
        Say ("step build, try $try : Unity $Method -noharness -> $buildRel")
        $b = Start-Process $u -ArgumentList $a -PassThru
        if (-not $b.WaitForExit([int]$cfg.batch.buildTimeoutSec * 1000)) { $b.Kill(); $rc = -999 } else { $rc = $b.ExitCode }
        if (-not (Test-Path -LiteralPath $buildLog) -or -not (Select-String -LiteralPath $buildLog -Pattern 'threw exception' -Quiet)) { break }
    }
    Set-Content (Join-Path $testOut ($v + '_done.txt')) ("build=$rc tries=$try mode=release-noharness")
    # ---- 3. verify
    $bi = Get-BuildInfo $buildDir $cfg
    if (-not (Test-Path -LiteralPath $bi.ExePath)) { Stop-Release "no exe at $($bi.ExePath) (Unity exit $rc, log $buildLog)" }
    if ((Get-Item -LiteralPath $bi.ExePath).LastWriteTime -lt $tBuild.AddMinutes(-1)) { Stop-Release 'the exe is older than this run: the build did not write it' }
    if (Test-BuildLogFailed $buildLog ([string]$cfg.batch.buildFailedMarker)) { Stop-Release "the build log says $($cfg.batch.buildFailedMarker)" }
    if (-not (Select-String -LiteralPath $buildLog -Pattern 'BUILD FIXHARNESS' -SimpleMatch -Quiet)) { Stop-Release 'the log has no "BUILD FIXHARNESS" line: FRANKEN_NO_HARNESS was NOT applied, this is not a release build' }
    Say 'verify OK: exe fresh, no Result: Failure, FRANKEN_NO_HARNESS applied'
    $asm = Get-ChildItem (Join-Path $bi.DataPath 'Managed') -Filter 'Assembly-CSharp.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($asm) { Say ("Assembly-CSharp.dll {0:N0} bytes (the v109y_rc -noharness build was 1,773,056; a harness build 2,350,592)" -f $asm.Length) }
    # ---- 4. md5: Assets\Data json (minus editor-only) == <build>\<exe>_Data\StreamingAssets json
    if (-not $SkipMd5) {
        $skip = @($cfg.build.editorOnlyData); $diff = 0; $n = 0
        foreach ($f in Get-ChildItem (Join-Path $p 'Assets\Data') -Filter *.json) {
            if ($skip -contains $f.Name) { continue }
            $q = Join-Path $bi.DataPath ('StreamingAssets\' + $f.Name); $n++
            if (-not (Test-Path -LiteralPath $q)) { Say "MD5 MISSING in build: $($f.Name)"; $diff++; continue }
            if ((Get-FileHash $f.FullName -Algorithm MD5).Hash -ne (Get-FileHash $q -Algorithm MD5).Hash) { Say "MD5 DIFF: $($f.Name)"; $diff++ }
        }
        Say ("md5 tree == build: {0} json compared, {1} differ" -f $n, $diff)
        if ($diff -gt 0) { Stop-Release "$diff data files differ between Assets\Data and the build" }
    }
}
if ($DryRun) { Say 'DRY RUN complete: nothing was run or written' } else { Say 'RELEASE BUILD OK (nothing else was changed; Tools\build_dir.txt and the launchers are untouched)' }
Write-Host @"

RUN ORDER (see Tools\RELEASE_BUILD.md)
  1. Tools\build_release.ps1 -v <tag>            this script (static gates -> compile x2 -> -noharness build -> verify -> md5)
  2. boot check of Builds\HDRP_<tag>\HadesSlice.exe by hand (menu, one fight, quit): the build has no harness flags, so -looptest / -physproof / run-through cannot run on it
  3. proofs (kill run, run-through, simhash) on the DEV build of the same tree: Tools\build_and_gate.ps1 -v <tag>d
  4. only then move the launchers / PLAY_*.bat to the release folder
"@
exit 0

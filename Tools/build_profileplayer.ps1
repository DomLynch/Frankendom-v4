# build_profileplayer.ps1 [-Tag <tag>] [-Dev] [-DryRun]
# PROFILE PLAYER (Lead, 18 Nov; plan _agents\laneLG\profile_run_plan.md): a player with PlayerSettings.enableFrameTimingStats = 1 so ProfileMode (-profile) can split CPU main / render thread / GPU ms.
# Uses HdrpBatch.Build (BuildPlayerCmd.Run, player only; NOT V51). Output Builds\HDRP_v109<tag>, passed as -buildDir (no global Tools\build_dir.txt written).
# -Dev adds a DEVELOPMENT player + UNITY_USE_RECORDER (-devbuild -gpupass) for counters and per-pass GPU ms (slower player; compare only within the same build).
# Flag files (Tools\build_profile.txt, build_dev.txt, build_gpupass.txt) are removed in a finally block, always: a left-over build_profile.txt turns EVERY later build into a profile build (BuildCfg.cs:135).
# Never touches PLAY launchers, never flips, never prunes. -DryRun prints the plan and the guard verdict only (no tag reserved, no file written, no Unity).
param([string]$Tag = '', [switch]$Dev, [switch]$DryRun)
$u = 'C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p = 'C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
$env:FK_OWNER = 'unitydev'
$forcing = 'player setting enableFrameTimingStats (PlayerSettings, baked at player build; needed to read CPU vs GPU frame time)'
$flags = @('Tools\build_profile.txt', 'Tools\build_dev.txt', 'Tools\build_gpupass.txt')
$dirArg = ''
$qaProfile = "$p\_agents\laneLG\prof_in\profile_on.json"

function Show-QaLine([string]$t) {
    "QA RUN LINE (after the build, nothing else running; repeat for 3 variants by swapping FK_DATA_OVERRIDE: zones_z5_empty.json / zones_wake_z5.json (Cedar) / zones_z5_druid_awake.json (Root Druid), with -profiletag z5_empty / z5_cedar / z5_druid):"
    '  $env:FK_OWNER=''unitydev''; $env:FK_FRAMEHZ=''0.2''; $env:FK_DATA_OVERRIDE = "zones.json=' + $p + '\_agents\laneHOTSHIP\memvar\zones_wake_z5.json"'
    '  $env:FK_QA_EXTRA = ''-profile -profiledata:' + $qaProfile + ' -profileout:' + $p + '\_agents\laneLG\prof_out -profiletag:z5_cedar -profilewarmup:12 -profileseconds:30'''
    "  powershell -NoProfile -File $p\_agents\laneHOTSHIP\gateproof.ps1 -Mode archerz5 -Tag ptbNNN -V 109k<2 letters> -Build HDRP_v109$t -NoSlot -ExtraCsv none -Purpose 'z5 profile, one enemy awake'"
    "  then: python $p\_agents\laneOPT4\profile_report.py $p\_agents\laneLG\prof_out\z5_empty $p\_agents\laneLG\prof_out\z5_cedar $p\_agents\laneLG\prof_out\z5_druid"
    "  (archerz5 holds 45 s at z5, so warmup 12 + 30 s window stays inside it; no spaces in any path; profile.json in Assets\Data stays enabled=false, do not flip it)"
}

if ($DryRun) {
    $t = if ($Tag) { $Tag } else { '<tag from next_tag.ps1 -Owner unitydev>' }
    "DRY RUN build_profileplayer (nothing reserved, written or started):"
    "  1. `$env:FK_OWNER='unitydev'; tag = $t"
    "  2. . Tools\_build_guard.ps1; Assert-BuildSafe $t   (default Bake mode: Editor alone)"
    "  3. `$env:FK_FORCING_ASSET = '$forcing'"
    "  4. create flag files: Tools\build_profile.txt" + $(if ($Dev) { ', Tools\build_dev.txt, Tools\build_gpupass.txt' } else { '' })
    "  5. board: BUILD PHASE player frankendom until <now+25 min> (profile player Editor $t)"
    "  6. $u -batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod HdrpBatch.Build -buildDir Builds/HDRP_v109$t -profilebuild" + $(if ($Dev) { ' -devbuild -gpupass' } else { '' }) + " -logFile $p\TestOut\v109${t}_Build.log   (30 min cap)"
    "  7. finally: remove " + ($flags -join ', ')
    "  8. write TestOut\v109${t}_done.txt, write $qaProfile (profile.json copy, enabled=true, companionArgs=[], quitWhenDone=false), Add-BuiltLine $t (PATH FULL, forcing asset above)"
    "  9. print the QA run line, 'do not flip'. PLAY launchers, flip.ps1, prune_builds.ps1 are never called."
    ''
    Show-QaLine $t
    ''
    $script:BuildMode = 'Bake'; . "$p\Tools\_build_guard.ps1"
    $w = @(Get-BuildBlockers -MinFreeGB 16)
    if ($w.Count -gt 0) { 'GUARD (Bake mode) would REFUSE now: ' + ($w -join ' | ') } else { 'GUARD (Bake mode) would be all clear now' }
    foreach ($f in $flags) { if (Test-Path "$p\$f") { "NOTE: stale flag file exists right now: $f (the real run removes it in finally; until then any build is a profile build)" } }
    exit 0
}

if (-not $Tag) { $Tag = (& powershell -NoProfile -File "$p\Tools\next_tag.ps1" -Owner unitydev | Select-Object -Last 1).Trim() }
if ($Tag -notmatch '^[a-z]{2,3}$') { "no usable tag from next_tag.ps1 ('$Tag')"; exit 3 }
. "$p\Tools\_build_guard.ps1"; Assert-BuildSafe $Tag
$env:FK_FORCING_ASSET = $forcing
Remove-Item "TestOut\v109${Tag}_done.txt" -ErrorAction SilentlyContinue
$t0 = Get-Date; $code = 99
try {
    New-Item "$p\Tools\build_profile.txt" -ItemType File -Force | Out-Null
    $extra = ''
    if ($Dev) { New-Item "$p\Tools\build_dev.txt" -ItemType File -Force | Out-Null; New-Item "$p\Tools\build_gpupass.txt" -ItemType File -Force | Out-Null; $extra = ' -devbuild -gpupass' }
    $bpU = (Get-Date).AddMinutes(25).ToString('HH:mm')
    try { $fs = [IO.File]::Open("$p\_agents\COORDINATION.md", 'Append', 'Write', 'ReadWrite'); $bb = [Text.Encoding]::UTF8.GetBytes("`r`n" + (Get-Date -Format 'yyyy-MM-dd HH:mm') + " BUILD PHASE player frankendom until $bpU (profile player Editor $Tag, frame timing$(if ($Dev) { ' + dev + gpupass' }), NOT for flip)"); $fs.Write($bb, 0, $bb.Length); $fs.Close() } catch { }
    $b = Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod HdrpBatch.Build -buildDir Builds/HDRP_v109$Tag -profilebuild$extra -logFile $p\TestOut\v109${Tag}_Build.log" -PassThru
    if (-not $b.WaitForExit(1800000)) { try { $b.Kill() } catch { } }
    $code = $b.ExitCode
}
finally {
    foreach ($f in $flags) { Remove-Item "$p\$f" -Force -ErrorAction SilentlyContinue }
    $left = @($flags | Where-Object { Test-Path "$p\$_" }); if ($left.Count -gt 0) { 'WARNING flag file(s) still present: ' + ($left -join ', ') } else { 'flag files removed (build_profile.txt / build_dev.txt / build_gpupass.txt)' }
}
Set-Content "TestOut\v109${Tag}_done.txt" "build=$code profileplayer dev=$([bool]$Dev) secs=$([int]((Get-Date) - $t0).TotalSeconds)"
# QA copy of profile.json: enabled=true, no companion args, no quit (gateproof ends the run). Assets\Data\profile.json itself is never touched.
try {
    $j = Get-Content "$p\Assets\Data\profile.json" -Raw | ConvertFrom-Json
    $j.enabled = $true; $j.companionArgs = @(); $j.quitWhenDone = $false
    New-Item -ItemType Directory -Force (Split-Path $qaProfile) | Out-Null
    ($j | ConvertTo-Json -Depth 20) | Set-Content $qaProfile -Encoding UTF8
    "QA profile data written: $qaProfile"
} catch { "could not write $qaProfile : $($_.Exception.Message) (make the copy by hand)" }
Add-BuiltLine $Tag
''
Show-QaLine $Tag
''
"DO NOT FLIP HDRP_v109${Tag}:profile player, never point a launcher at it, never run flip.ps1 on it. Delete Builds\HDRP_v109$Tag when the runs are read (prune_builds.ps1 would otherwise delete an older build instead)."

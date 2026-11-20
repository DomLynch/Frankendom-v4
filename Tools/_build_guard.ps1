# Frankendom build preflight (COO standing rule 09:13, Dom: recurring faults get a guard IN THE SCRIPT).
# Dot-source from any Tools\build_v109*.ps1:   . "$PSScriptRoot\_build_guard.ps1"; Assert-BuildSafe 'oq'
# Dry run (prints every check, never starts anything):   powershell -File Tools\_build_guard.ps1 -DryRun
# Refuses (exit 3, prints WHY) when ANY of these holds:
#   1. another Unity.exe is open (our Editor runs alone: one Editor at a time across all games)
#   2. a git backup / pack / push runs on any repo, or the backup lock exists (git reading files mid-write = IOException 1224, Ascent53 08:52)
#   3. free RAM is under MinFreeGB (default 24; these builds peak about 36 GB)
#   4. a Dom game is running: a HadesSlice without -camscene/-camtrace, or a frankendom dom-runs log written in the last 3 minutes with a live HadesSlice
#   5. the Unity licence probe fails (licence file missing/expired, or the newest Editor log shows a licence error)
param([switch]$DryRun, [int]$MinFreeGB = 24, [string]$Mode = 'Bake', [string]$DryRunTag = '', [string]$FixtureDomGame = '', [string]$FixtureBoard = '')   # DryRunTag (was Tag): a script-level $Tag param blanked the caller's own $Tag whenever a build script dot-sourced this file (BUILD REFUSED () 15:36, unitydev/COO 15:46)
# COO 15:55 17 Nov: an ALONE bake of ANOTHER lane that is still open (EDITOR TAKEN <lane> ALONE without a later EDITOR RELEASED <lane> in the last 3 h) refuses this build.
function Get-OtherLaneAlone {
    $out = @(); try {
        $own = 'frankendom'; $lines = Get-Content 'C:\Users\dom12\Documents\unity_test_hdrp\_agents\COORDINATION.md' -Tail 400 -ErrorAction Stop; $open = @{}
        foreach ($l in $lines) {
            if ($l -match '^(\d{4}-\d\d-\d\d \d\d:\d\d)\s+.*?EDITOR TAKEN\s+(\w+)\s+ALONE') { $open[$Matches[2].ToLower()] = $Matches[1] }
            elseif ($l -match '^(\d{4}-\d\d-\d\d \d\d:\d\d)\s+.*EDITOR RELEASED\s+(\w+)') { $open.Remove($Matches[2].ToLower()) }
        }
        foreach ($k in $open.Keys) { if ($k -ne $own) { $t = [datetime]::ParseExact($open[$k], 'yyyy-MM-dd HH:mm', $null); if (((Get-Date) - $t).TotalHours -lt 3) { $out += "another lane's ALONE bake is open: $k (TAKEN $($open[$k]), no later RELEASED)" } } }
    } catch {}
    return $out
}
function Get-BuildBlockers {
    param([int]$MinFreeGB = 24)
    $why = @()
    $p = 'C:\Users\dom12\Documents\unity_test_hdrp'
    # COO 10:46 rule C amended: a CODE-ONLY build (HdrpBatch.Build, no scene bake) may run BESIDE another game's Editor (free RAM floor still applies); a scene-bake build runs alone.
    # Our OWN project's Unity.exe always blocks (one Editor per project). Mode: $script:BuildMode = 'CodeOnly' | 'Bake' (default Bake = alone).
    $mode = if ($script:BuildMode) { $script:BuildMode } else { 'Bake' }
    # COO 12:40: pg 'code-only' peaked at 40.9 GB (20 scenes). Until a build logs a peak under 12 GB (delete Tools\_codeonly_unproven.txt then), CodeOnly is treated as Bake: it runs ALONE, 30 GB+ free.
    # COO/Strategy 12:57 (third time: 12:37 free RAM 0.1 GB as Dom launched): EVERY Frankendom Editor start needs 45 GB+ free, NO game running (Dom or QA, any project) and NO other Editor. mem_watch.ps1 stops MY Editor the moment any game process starts.
    # COO 14:10 ruling: a WARM CodeOnly build (no FK_BAKE_TEST, no _codeonly_unproven.txt; last code-only peak 4.3 GB) needs 24 GB+ free; other Editors and QA players are allowed. It still refuses for a Dom game (checked below), our own project's Editor, or a backup lock. COLD / FK_BAKE_TEST builds keep the 45 GB, ALONE, no-game rule.
    $warm = ($mode -eq 'CodeOnly' -and $env:FK_BAKE_TEST -ne '1' -and -not (Test-Path (Join-Path $p 'Tools\_codeonly_unproven.txt')))
if ($warm) { $MinFreeGB = 14 }   # COO ruling 23:03: warm code-only peak ~4.5 GB, floor 24 -> 14 (14 - 4.5 = 9.5 GB, above the 8 GB own-pid stop); cold/FK_BAKE_TEST keep 45 GB ALONE

# COO 23:09 (qe peaked 33.5 / 29.1 GB under a "warm" flag): a take is COLD (45 GB, ALONE) when (a) the LAST measured Editor peak (newest TestOut\v109*_mem.csv, column peakMyUnityGB) was over 12 GB, or (b) any non-code Assets file (not .cs/.cs.meta/.json, not Data/StreamingAssets) changed since the newest real build's Assembly-CSharp.dll.
if ($warm) {
    $script:SerWhy = $null
    $coldWhy = $null
    try {
        $mc = Get-ChildItem (Join-Path $p 'TestOut') -Filter 'v109*_mem.csv' -ErrorAction Stop | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($mc) { $pk = (Import-Csv $mc.FullName | ForEach-Object { [double]$_.peakMyUnityGB } | Measure-Object -Maximum).Maximum; if ($pk -gt 12) { $coldWhy = "last measured Editor peak $pk GB ($($mc.Name)) is over 12 GB" } }
        $dll = Get-ChildItem (Join-Path $p 'Builds') -Directory -Filter 'HDRP_v109*' -ErrorAction Stop | ForEach-Object { Get-Item (Join-Path $_.FullName 'HadesSlice_Data\Managed\Assembly-CSharp.dll') -ErrorAction SilentlyContinue } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($dll -and -not $coldWhy) {
            $ch = @(Get-ChildItem (Join-Path $p 'Assets') -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt $dll.LastWriteTime -and $_.Extension -notin '.cs', '.json' -and $_.FullName -notmatch '\\(Data|StreamingAssets)\\' -and $_.Name -notmatch '\.cs\.meta$' })
            if ($ch.Count -gt 0) { $coldWhy = "$($ch.Count) non-code Assets file(s) changed since the last real build (e.g. $($ch[0].Name))" }
        }
    } catch { }
    # Audit 18 Nov (Pin compile-only to the build it extends): the 60-file added-line heuristic is replaced by signature compare against the BASE build (live build named in PLAY.bat): serialized field set, boot-hook set, script file list, all files, no cap (Tools\_build_sig.ps1).
    # Refuses (names the items) when the serialized layout changed, a script file was removed/renamed, or the [RuntimeInitializeOnLoadMethod] set grew. Fails closed when the compare cannot run.
    if (-not $coldWhy) {
        try {
            . (Join-Path $PSScriptRoot '_build_sig.ps1')
            $sc = Test-CodeOnlySig -Repo $p
            foreach ($n in $sc.notes) { Write-Output ("signature note: " + $n) }
            if ($sc.legacy) { Write-Output ("signature note: base " + $sc.baseName + " has no signature fields (" + $sc.baseHow + "); a full build stamps them") }
            if ($sc.refuse) { $script:SerWhy = "REFUSED, needs a full build (signature guard vs base $($sc.baseName), $($sc.baseHow)): " + ($sc.reasons -join ' | ') }
        } catch { $script:SerWhy = "REFUSED, signature guard could not run (fails closed): " + $_.Exception.Message }
    }
    # Strategy/COO 08:20 17 Nov: the scripts-only PROOF take (HdrpScriptsOnly, env FK_SCRIPTSONLY_PROOF=1) is exempt from cold detection for this one proof: floor 24 GB, no other Unity.exe, no game; mem_watch stops it at own > 12 GB or free < 8 GB.
    if ($env:FK_SCRIPTSONLY_PROOF -eq '1') { $coldWhy = $null; $script:ColdWhy = $null; $MinFreeGB = 24 }
    if ($coldWhy) { $warm = $false; $script:ColdWhy = "treated as COLD (45 GB, ALONE): $coldWhy" }
}
    # Dom 17 Nov 09:57 (via Strategy+COO): no Dom-game refusal and no 24/35/45 GB waits any more. Cold/bake floor 16 GB; own-pid stop (mem_watch) at free < 4 GB is the only RAM guard once running.
    if (-not $warm) { $MinFreeGB = [Math]::Max($MinFreeGB, 16) }
    $codeOnlyUnproven = $false; if ($mode -eq 'CodeOnly' -and (Test-Path (Join-Path $p 'Tools\_codeonly_unproven.txt'))) { $mode = 'Bake'; $MinFreeGB = [Math]::Max($MinFreeGB, 30); $codeOnlyUnproven = $true }
    # COO 12:06: NO Frankendom scene-bake builds while Tools\_bake_frozen.txt exists (the rider bake took free RAM to 0.7-2 GB twice today). Delete the file when RiderClipSetup is fixed and its per-scene peak memory is logged under 20 GB.
    # COO 12:30: -Mode CodeOnly means NOTHING under Assets\ changes except code: refuse when git sees a changed/new file under Assets\ that is not .cs/.asmdef/.asmref/.meta and not data (Assets\Data, Assets\StreamingAssets), or when the setup step declares outputs in $env:BUILD_SETUP_OUTPUTS (semicolon list). An asset import re-processes whatever references it (the 30 GB main-scene load).
if ($mode -eq 'CodeOnly') { $chg = @(& git -C $p status --porcelain --untracked-files=all -- Assets 2>$null | ForEach-Object { ($_.Substring(3)).Trim('"') -replace '\\','/' } | Where-Object { $_ -notmatch '\.(cs|asmdef|asmref|meta)$' -and $_ -notmatch '^Assets/Data/' -and $_ -notmatch '^Assets/StreamingAssets/.*\.(json|txt|csv|md|glb|png|jpg|bc7|ogg|wav)$' }); if ($env:BUILD_SETUP_OUTPUTS) { $chg += @($env:BUILD_SETUP_OUTPUTS -split ';' | Where-Object { $_ }) }; $stray = @($chg | Where-Object { $_ -match '\.(bak|orig|rej|tmp)(_[A-Za-z0-9]+)?$|\.(bak|orig)[._]' }); if ($stray.Count -gt 0) { $why += 'stray backup in Assets: ' + (($stray | Select-Object -First 8) -join ', ') + ' - move it OUT of Assets (kit folder); apply scripts must write backups outside Assets' }; if ($chg.Count -gt 0) { $why += 'not code-only: ' + (($chg | Select-Object -First 8) -join ', ') + $(if ($chg.Count -gt 8) { ' (+' + ($chg.Count - 8) + ' more)' } else { '' }) } }
    # COO 15:35 17 Nov: per-slot lift of the bake freeze: FK_BAKE_LIFT must equal the build tag; refused if the chain (build script of that tag or build_texstream.ps1) contains RiderClipSetup (the 23-scene bake the freeze is for).
    $lifted = $false
    if ($env:FK_BAKE_LIFT -and $script:GuardTag -and $env:FK_BAKE_LIFT -eq $script:GuardTag) {
        $chain = @((Join-Path $p "Tools\build_v109$($script:GuardTag).ps1"), (Join-Path $p 'Tools\build_texstream.ps1')) | Where-Object { Test-Path $_ }
        $rc = @($chain | Where-Object { Select-String -Path $_ -Pattern 'RiderClipSetup' -Quiet })
        if ($rc.Count -gt 0) { $why += "FK_BAKE_LIFT refused: the chain contains RiderClipSetup ($($rc -join ', '))" } else { $lifted = $true; Write-Host "BAKE FREEZE LIFTED for $($script:GuardTag) (COO 15:35, texture-streaming bake, not RiderClipSetup)" }
    }
if (-not $lifted -and $mode -eq 'Bake' -and $env:FK_BAKE_TEST -ne '1' -and (Test-Path (Join-Path $p 'Tools\_bake_frozen.txt'))) { $why += "scene-bake builds are FROZEN (Tools\_bake_frozen.txt): " + ((Get-Content (Join-Path $p 'Tools\_bake_frozen.txt') -TotalCount 1)) }
    $ps = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction SilentlyContinue)
    $own = @($ps | Where-Object { $_.CommandLine -match 'unity_test_hdrp' })
    $other = @($ps | Where-Object { $_.CommandLine -notmatch 'unity_test_hdrp' })
    if ($own.Count -gt 0) { $why += "this project's Unity.exe is already open (pids " + (($own | ForEach-Object { $_.ProcessId }) -join ',') + ")" }
    if ($mode -ne 'CodeOnly' -and $other.Count -gt 0) { $why += "another game's Unity.exe is open (pids " + (($other | ForEach-Object { $_.ProcessId }) -join ',') + ") and this is a scene-bake build, which runs alone" }
    # git: refuse only for an operation on THIS repo (command line names it, or the backup script of this repo), never another repo's push
    $g = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.CommandLine -match 'unity_test_hdrp' -and (
        ($_.Name -match '^git(-[a-z-]+)?\.exe$' -and $_.CommandLine -match '\b(pack-objects|gc|repack|push|add|commit|index-pack)\b') -or
        ($_.Name -match 'powershell|pwsh' -and $_.CommandLine -match 'backup(_hourly)?\.ps1')) })
    if ($g.Count -gt 0) { $why += "a git backup/pack/push on THIS repo is running (pids " + (($g | ForEach-Object { $_.ProcessId }) -join ',') + ")" }
    $lock = Join-Path $p '_agents\pool\backup.lock'
    if (Test-Path $lock) { if (((Get-Date) - (Get-Item $lock).LastWriteTime).TotalMinutes -lt 30) { $why += "backup lock exists ($lock)" } }
    $free = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB, 1)
    # COO 20 Nov 13:3x (Dom: 'remove the blocks if they are restrictive or not common sense'): a compile-only build peaks about 3 GB, so for -Mode CodeOnly
    # the RAM floor is a WARNING down to 8 GB free (the 4 GB own-pid stop still protects the machine). Bake/cold builds keep the refusal. Cause: ir refused at 15 GB free (floor 16) beside a QA player.
    if ($free -lt $MinFreeGB) { if ($mode -eq 'CodeOnly' -and $free -ge 8) { Write-Host "WARN: free RAM $free GB is under $MinFreeGB GB (compile-only proceeds; floor is 8 GB)" } else { $why += "free RAM $free GB is under $MinFreeGB GB" } }
# (cold detection is informational now: it raises the RAM floor to 16 GB, it no longer refuses; serialized-field refusal below stays)
if ($script:SerWhy) { $why += $script:SerWhy }
# COO 16:15 17 Nov: a full C: failed the texture bake at the file copy after 9 minutes. Refuse in second 1 when C: free is under the expected build size + 10 GB (bake / full build 25 GB, code-only 12 GB; FK_MINDISK_GB overrides).
try { $dfree = [math]::Round((Get-PSDrive C).Free / 1GB, 1); $dneed = $(if ($env:FK_MINDISK_GB) { [double]$env:FK_MINDISK_GB } elseif ($mode -eq 'CodeOnly') { 12 } else { 25 }); if ($dfree -lt $dneed) { $why += "C: free disk $dfree GB is under $dneed GB (expected build size + 10 GB; free space first)" } } catch { }
    # (Dom-game refusal and fresh-dom-runs-log refusal REMOVED 17 Nov 09:57 on Dom's order; the build folder his running game uses is never overwritten: see release.ps1 / prune_builds.ps1.)
    # COO 19 Nov: after a restart the Unity Hub is not running and the licensing client has no access token. If Tools\hub_path.txt names the Hub exe (recorded from the running process once Dom has signed in), start it minimised and re-probe up to 60 s before refusing. No-op while the file is absent. Credentials are never typed here.
    try { $hp = Join-Path $p 'Tools\hub_path.txt'; $lc0 = Join-Path $env:LOCALAPPDATA 'Unity\Unity.Licensing.Client.log'; if ((Test-Path $hp) -and -not (Get-Process -Name 'Unity Hub' -ErrorAction SilentlyContinue)) { $hx = (Get-Content $hp -Raw).Trim(); if ($hx -and ($hx -like 'shell:*' -or (Test-Path $hx))) { $t0h = Get-Date; if ($hx -like 'shell:*') { Start-Process explorer.exe -ArgumentList $hx | Out-Null } else { Start-Process $hx -WindowStyle Minimized | Out-Null }; for ($hi = 0; $hi -lt 12; $hi++) { Start-Sleep 5; if ((Test-Path $lc0) -and (Get-Item $lc0).LastWriteTime -gt $t0h) { $tl = Get-Content $lc0 -Tail 200 -ErrorAction SilentlyContinue; if ($tl | Select-String -Pattern 'Entitlements? (were )?(updated|granted)|licen[cs]e (is )?(valid|updated)|Successfully (acquired|updated)' -Quiet) { break } } } } } } catch { }
    # Unity 6 keeps no .ulf: probe = the Licensing Client log (newest 400 lines) must not end in a sign-in / entitlement failure
    $lc = Join-Path $env:LOCALAPPDATA 'Unity\Unity.Licensing.Client.log'
    if (-not (Test-Path $lc)) { $why += "licence probe: no Unity.Licensing.Client.log (Hub never signed in?)" }
    else {
        $tail = Get-Content $lc -Tail 400 -ErrorAction SilentlyContinue
        $bad = $tail | Select-String -Pattern 'Access token is unavailable|not logged in|No valid (Unity )?(Editor )?licen[cs]e|entitlement.*(not granted|failed|missing)|Update licen[cs]e failed|Unable to acquire' | Select-Object -Last 1
        $good = $tail | Select-String -Pattern 'Entitlements? (were )?(updated|granted)|licen[cs]e (is )?(valid|updated)|Successfully (acquired|updated)' | Select-Object -Last 1
        if ($bad -and (-not $good -or $bad.LineNumber -gt $good.LineNumber)) { $why += "licence probe: Licensing Client log shows: " + $bad.Line.Substring(0, [Math]::Min(120, $bad.Line.Length)) }
    }
    $log = Get-ChildItem (Join-Path $p 'TestOut') -Filter '*_Build.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    $lcFresh = $false; try { if ($log -and (Test-Path $lc) -and (Get-Item $lc).LastWriteTime -gt $log.LastWriteTime -and -not (Get-Content $lc -Tail 80 -ErrorAction SilentlyContinue | Select-String -Pattern 'Access token is unavailable|Code 404|0 entitlement|No valid' -Quiet)) { $lcFresh = $true } } catch { }   # a licence success newer than the failed build log clears the stale refusal (19 Nov: v109ww_Build.log would have blocked 2 h after sign-in)
    if ($log -and -not $lcFresh -and ((Get-Date) - $log.LastWriteTime).TotalHours -lt 2 -and (Select-String -Path $log.FullName -Pattern 'No valid Unity Editor license|license (is )?(invalid|expired)|Licensing: .*(fail|denied)' -Quiet)) { $why += "licence probe: newest build log $($log.Name) shows a licence error" }
    return $why
}
# Strategy 18 Nov: a FULL (Bake-mode) build starts WITH graphics (no -nographics: it bakes / compiles shaders and lighting) and may start while Dom's game is up. It must say so on the board.
# Detection REUSES the existing mechanisms: _agents\qa_slot.ps1 -DomPlaying (Dom's game = ARMAGEDOM_Slice0 without -bench outside coo_qa, or any player logging to dom-runs) for the yes/no,
# and the board's open 'DOM PLAYING <game>' line (posted by _agents\dom_playing_watch.ps1, no later DOM STOPPED) for the game name; process name is the fallback.
# Test without an Editor: -DryRun -FixtureDomGame <game|none> [-FixtureBoard <temp copy>]  (the fixture replaces the process probe; with no -FixtureBoard nothing is written, the line is only printed).
function Get-DomGameNow {
    param([string]$Board = 'C:\Users\dom12\Documents\unity_test_hdrp\_agents\COORDINATION.md')
    if ($FixtureDomGame) { return $(if ($FixtureDomGame -eq 'none') { '' } else { $FixtureDomGame }) }
    $q = 'C:\Users\dom12\Documents\unity_test_hdrp\_agents\qa_slot.ps1'
    $yes = $false; try { $yes = [bool](& $q -DomPlaying) } catch { }
    if (-not $yes) { return '' }
    $name = ''
    try { $open = $null; foreach ($l in (Get-Content $Board -Tail 600 -ErrorAction Stop)) { if ($l -match 'DOM PLAYING\s+(\w+)') { $open = $Matches[1] } elseif ($l -match 'DOM STOPPED\s+(\w+)') { if ($open -eq $Matches[1]) { $open = $null } } }; if ($open) { $name = $open } } catch { }
    if (-not $name) { $pr = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(ARMAGEDOM_Slice0|HadesSlice|FronteirDom)\.exe$' -and ("$($_.CommandLine)" -match 'dom-runs' -or $_.Name -like 'ARMAGEDOM*') } | Select-Object -First 1; if ($pr) { $name = @{ 'ARMAGEDOM_Slice0.exe' = 'Armagedom'; 'HadesSlice.exe' = 'Frankendom'; 'FronteirDom.exe' = 'FrontierDom' }[$pr.Name] } }
    if (-not $name) { $name = 'unknown' }
    return $name
}
function Add-GuardBoard {
    param([string]$Text, [string]$Board = 'C:\Users\dom12\Documents\unity_test_hdrp\_agents\COORDINATION.md')
    try { $fs = [IO.File]::Open($Board, 'Append', 'Write', 'ReadWrite'); $bb = [Text.Encoding]::UTF8.GetBytes("`r`n" + (Get-Date -Format 'yyyy-MM-dd HH:mm') + " " + $Text); $fs.Write($bb, 0, $bb.Length); $fs.Close() } catch { }
}
# Exact board text: 'FULL STARTED BESIDE DOM <game> <tag>'. Once per tag per process. Returns the line (or '' when Dom is not playing).
function Add-FullBesideDomLine {
    param([string]$Tag, [string]$Board = '')
    if ($script:FullBesideDone -eq $Tag) { return '' }
    $g = Get-DomGameNow
    if (-not $g) { return '' }
    $script:FullBesideDone = $Tag
    $line = "FULL STARTED BESIDE DOM $g $Tag"
    if ($Board) { Add-GuardBoard -Text $line -Board $Board }
    elseif (-not $script:GuardDryRun) { Add-GuardBoard -Text $line }
    return $line
}
function Assert-BuildSafe {
    param([string]$Tag = '?', [int]$MinFreeGB = 16, [ValidateSet('Bake', 'CodeOnly')][string]$Mode = 'Bake')
    $script:BuildMode = $Mode
    # BANNED ASSET GUARD (19 Nov, goblin purge): any Tools\banned_assets.txt match under Assets refuses the build before a tag is reserved.
    $bannedHits = @(& (Join-Path $PSScriptRoot 'banned_assets_check.ps1') -Root (Join-Path $PSScriptRoot '..\Assets') -Caller 'build_guard'); if ($bannedHits.Count) { $bannedHits | ForEach-Object { Write-Host $_ }; "BUILD REFUSED ($Tag): banned asset in Assets"; exit 3 }
    . (Join-Path $PSScriptRoot '_tag_reserve.ps1'); try { $null = Reserve-Tag -Tag $Tag -Owner $(if ($env:FK_OWNER) { $env:FK_OWNER } else { "build:$Tag" }) } catch { "BUILD REFUSED ($Tag): $($_.Exception.Message)"; exit 3 }
    $script:GuardTag = $Tag
    # Audit 18 Nov: remember the commit + signatures this build starts from; Add-BuiltLine writes them into build_info.json (baseCommit, fullBakeId, serializedSig, bootSig, scriptFilesSig)
    try { . (Join-Path $PSScriptRoot '_build_sig.ps1'); $script:BuildSigAtStart = Get-BuildSig $p; $script:BuildHeadAtStart = Get-GitHeadState $p } catch { $script:BuildSigAtStart = $null }
    $w = @(Get-BuildBlockers -MinFreeGB $MinFreeGB) + @(Get-OtherLaneAlone)
    if ($w.Count -gt 0) {
        "BUILD REFUSED ($Tag) at $(Get-Date -Format 'HH:mm'): " + ($w -join ' | ')
        try { $fsb = [IO.File]::Open((Join-Path $p '_agents\COORDINATION.md'), 'Append', 'Write', 'ReadWrite'); $bbb = [Text.Encoding]::UTF8.GetBytes("`r`n" + (Get-Date -Format 'yyyy-MM-dd HH:mm') + " BUILD REFUSED frankendom ${Tag}: " + ($w -join ' | ')); $fsb.Write($bbb, 0, $bbb.Length); $fsb.Close() } catch {}   # refusal reasons on the board (COO 15:44): a refused build was invisible
        exit 3
    }
    "build guard ($Tag): all clear at $(Get-Date -Format 'HH:mm')"
    # Strategy 18 Nov: say which mode runs. FULL = graphics ON (never -nographics: shaders / lighting bake); CODE-ONLY = -nographics + BelowNormal (build_compileonly.ps1).
    if ($Mode -eq 'CodeOnly') { "build mode ($Tag): CODE-ONLY (headless -nographics + BelowNormal where build_compileonly.ps1 starts the Editor)" }
    else { "build mode ($Tag): FULL / Bake (graphics ON, NO -nographics: this build bakes / compiles shaders and lighting)"; $fb = Add-FullBesideDomLine -Tag $Tag; if ($fb) { $fb } }
}
# Last step of every build script (COO 10:48): post 'BUILT <tag> ready to flip' on the board automatically, so a finished build is never invisible.
#   . "$p\Tools\_build_guard.ps1"; Add-BuiltLine 'ov'   (reads TestOut\v109<tag>_done.txt and Builds\HDRP_v109<tag>\build_info.json)
function Add-BuiltLine {
    param([string]$Tag)
    $p = 'C:\Users\dom12\Documents\unity_test_hdrp'
    $done = Join-Path $p "TestOut\v109${Tag}_done.txt"
    $info = Join-Path $p "Builds\HDRP_v109${Tag}\build_info.json"
    $res = if (Test-Path $done) { (Get-Content $done -Raw).Trim() } else { 'no done file' }
    $ok = (Test-Path $info) -and ($res -match 'build=0')
    # Audit 18 Nov: a successful FULL/bake build records the commit and signatures it was compiled from (compile-only takes compare against these). Compile-only/hot takes inherit them.
    if ($ok -and $res -notmatch 'compileonly|scriptsonly') {
        try {
            . (Join-Path $PSScriptRoot '_build_sig.ps1'); $bd = Split-Path $info -Parent; $cur = Get-Content $info -Raw | ConvertFrom-Json
            if (-not $cur.fullBakeId -or $cur.fullBakeId -eq "HDRP_v109$Tag" -or $cur.source -ne 'deploy_data') {
                if ($script:BuildSigAtStart) { $sg = $script:BuildSigAtStart; $hs = $script:BuildHeadAtStart; $at = 'buildStart' } else { $sg = Get-BuildSig $p; $hs = Get-GitHeadState $p; $at = 'buildEnd' }
                Set-BuildInfoSig $bd "HDRP_v109$Tag" $sg $hs.commit ([bool]$hs.dirty) $at
            }
        } catch { Write-Output ("build_info signature stamp FAILED: " + $_.Exception.Message) }
    }    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm') BUILT frankendom HDRP_v109$Tag " + $(if ($ok) { 'ready to flip' } else { 'FAILED / incomplete (do not flip)' }) + " ($res; build_info.json " + $(if (Test-Path $info) { 'present' } else { 'MISSING' }) + ")"
    Add-Content -Path (Join-Path $p '_agents\COORDINATION.md') -Value ("`n" + $line)
    $line
    # PATH line (COO/Dom 17 Nov 23:1x): every build says HOT | CODE | FULL and the asset that forced a FULL. A FULL with no forcing asset refuses (exit 5): set $env:FK_FORCING_ASSET to the asset or scene that cannot go any lighter path.
    $pth = if ($res -match 'compileonly') { 'CODE' } else { 'FULL' }
    $fa = $env:FK_FORCING_ASSET
    if ($pth -eq 'FULL' -and [string]::IsNullOrWhiteSpace($fa)) {
        $m = "$(Get-Date -Format 'yyyy-MM-dd HH:mm') PATH FULL REFUSED frankendom HDRP_v109$Tag : a full build needs a forcing asset (set FK_FORCING_ASSET=<asset or scene>); data = HOT (deploy_data), code = CODE (build_compileonly)"
        Add-Content -Path (Join-Path $p '_agents\COORDINATION.md') -Value ("`n" + $m); $m; exit 5
    }
    $pl = "$(Get-Date -Format 'yyyy-MM-dd HH:mm') PATH $pth frankendom HDRP_v109$Tag" + $(if ($pth -eq 'FULL') { " (forcing asset: $fa)" } else { '' })
    Add-Content -Path (Join-Path $p '_agents\COORDINATION.md') -Value ("`n" + $pl); $pl
}
if ($DryRun) {
    $script:BuildMode = $Mode
    $script:GuardTag = $DryRunTag
    $script:GuardDryRun = $true
    $w = Get-BuildBlockers -MinFreeGB $MinFreeGB
    if ($w.Count -gt 0) { "DRY RUN: build would be REFUSED at $(Get-Date -Format 'HH:mm'): " + ($w -join ' | ') } else { "DRY RUN: build would run (all clear) at $(Get-Date -Format 'HH:mm')" }
    if ($Mode -ne 'CodeOnly') {
        "DRY RUN build mode: FULL / Bake (graphics ON, NO -nographics)"
        $fb = Add-FullBesideDomLine -Tag $(if ($DryRunTag) { $DryRunTag } else { 'xx' }) -Board $FixtureBoard
        if ($fb) { "DRY RUN board line: $fb" + $(if ($FixtureBoard) { "  (appended to fixture board $FixtureBoard)" } else { "  (printed only, real board untouched)" }) } else { "DRY RUN: Dom's game is not up, no FULL STARTED BESIDE DOM line" }
    } else { "DRY RUN build mode: CODE-ONLY (no FULL STARTED BESIDE DOM line in this mode)" }
}

















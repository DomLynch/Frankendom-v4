# Frankendom build preflight (COO standing rule 09:13, Dom: recurring faults get a guard IN THE SCRIPT).
# Dot-source from any Tools\build_v109*.ps1:   . "$PSScriptRoot\_build_guard.ps1"; Assert-BuildSafe 'oq'
# Dry run (prints every check, never starts anything):   powershell -File Tools\_build_guard.ps1 -DryRun
# Refuses (exit 3, prints WHY) when ANY of these holds:
#   1. another Unity.exe is open (our Editor runs alone: one Editor at a time across all games)
#   2. a git backup / pack / push runs on any repo, or the backup lock exists (git reading files mid-write = IOException 1224, Ascent53 08:52)
#   3. free RAM is under MinFreeGB (default 24; these builds peak about 36 GB)
#   4. a Dom game is running: a HadesSlice without -camscene/-camtrace, or a frankendom dom-runs log written in the last 3 minutes with a live HadesSlice
#   5. the Unity licence probe fails (licence file missing/expired, or the newest Editor log shows a licence error)
param([switch]$DryRun, [int]$MinFreeGB = 24, [string]$Mode = 'Bake', [string]$DryRunTag = '')   # DryRunTag (was Tag): a script-level $Tag param blanked the caller's own $Tag whenever a build script dot-sourced this file (BUILD REFUSED () 15:36, unitydev/COO 15:46)
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
    # COO 23:11: a NEW public / [SerializeField] field without [NonSerialized] in a .cs changed since the last real build re-serializes scenes (qe lastSpend): treat as COLD and name it.
    if (-not $coldWhy -and $dll) {
        try {
            Push-Location $p
            $cs = @(Get-ChildItem (Join-Path $p 'Assets\Scripts') -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt $dll.LastWriteTime })
            $hits = @()
            foreach ($c in ($cs | Select-Object -First 60)) {
                $ex = @(Get-Content (Join-Path $p 'Tools\_serfield_exempt.txt') -ErrorAction SilentlyContinue | Where-Object { $_ -and $_ -notmatch '^\s*#' } | ForEach-Object { $_.Trim() }); if ($ex -contains $c.Name) { continue }
                $rel = $c.FullName.Substring($p.Length + 1).Replace('\', '/')
                $base = (& git log --before=($dll.LastWriteTime.ToString('s')) -1 --format=%H -- $rel 2>$null)
                $d = if ($base) { & git diff $base -- $rel 2>$null } else { & git diff HEAD -- $rel 2>$null }
                foreach ($l in @($d)) {
                    if ($l -notmatch '^\+(?!\+)') { continue }
                    if ($l -match 'NonSerialized|\bstatic\b|\bconst\b|\bclass\b|\bstruct\b|\benum\b|\binterface\b|\bdelegate\b|\bevent\b|\boverride\b|\babstract\b|\bvirtual\b|\bpartial\b') { continue }
                    if ($l -match '^\+\s*(//|/\*|\*)') { continue }
                    if ($l -match '^\+\s*(\[SerializeField\]|public\s+[\w<>\[\],\.\?]+(\s*,\s*[\w<>\[\],\.\?]+)*\s+\w+\s*(=[^;(){}]*)?\s*(,\s*\w+\s*(=[^;(){}]*)?\s*)*;)') { $hits += "$($c.Name): $($l.Substring(1).Trim())" }
                }
            }
            # COO 14:09: a NEW [RuntimeInitializeOnLoadMethod] never runs in a compile-only take (stale method table): warn loudly.
            try { $rt = @(); foreach ($c in ($cs | Select-Object -First 60)) { $rel2 = $c.FullName.Substring($p.Length + 1).Replace('\', '/'); $base2 = (& git log --before=($dll.LastWriteTime.ToString('s')) -1 --format=%H -- $rel2 2>$null); $d2 = if ($base2) { & git diff $base2 -- $rel2 2>$null } else { & git diff HEAD -- $rel2 2>$null }; if (@($d2) -match '^\+.*RuntimeInitializeOnLoadMethod') { $rt += $c.Name } }; if ($rt.Count -gt 0) { Write-Output ("WARNING compile-only trap: a NEW [RuntimeInitializeOnLoadMethod] in " + ($rt -join ', ') + " will NOT run in a compile-only take (stale method table). Hook it from an existing boot path (e.g. ZoneGroundMat.Boot) or do a full build.") } } catch { }            Pop-Location
            if ($hits.Count -gt 0) { $script:SerWhy = "REFUSED, new serialized field(s) in a code take (re-serializes scenes; make them private/[NonSerialized] and load from json, Strategy 08:45): " + (($hits | Select-Object -First 3) -join ' | ') }
        } catch { try { Pop-Location } catch { } }
    }    # Strategy/COO 08:20 17 Nov: the scripts-only PROOF take (HdrpScriptsOnly, env FK_SCRIPTSONLY_PROOF=1) is exempt from cold detection for this one proof: floor 24 GB, no other Unity.exe, no game; mem_watch stops it at own > 12 GB or free < 8 GB.
    if ($env:FK_SCRIPTSONLY_PROOF -eq '1') { $coldWhy = $null; $script:ColdWhy = $null; $MinFreeGB = 24 }
    if ($coldWhy) { $warm = $false; $script:ColdWhy = "treated as COLD (45 GB, ALONE): $coldWhy" }
}
    # Dom 17 Nov 09:57 (via Strategy+COO): no Dom-game refusal and no 24/35/45 GB waits any more. Cold/bake floor 16 GB; own-pid stop (mem_watch) at free < 4 GB is the only RAM guard once running.
    if (-not $warm) { $MinFreeGB = [Math]::Max($MinFreeGB, 16) }
    $codeOnlyUnproven = $false; if ($mode -eq 'CodeOnly' -and (Test-Path (Join-Path $p 'Tools\_codeonly_unproven.txt'))) { $mode = 'Bake'; $MinFreeGB = [Math]::Max($MinFreeGB, 30); $codeOnlyUnproven = $true }
    # COO 12:06: NO Frankendom scene-bake builds while Tools\_bake_frozen.txt exists (the rider bake took free RAM to 0.7-2 GB twice today). Delete the file when RiderClipSetup is fixed and its per-scene peak memory is logged under 20 GB.
    # COO 12:30: -Mode CodeOnly means NOTHING under Assets\ changes except code: refuse when git sees a changed/new file under Assets\ that is not .cs/.asmdef/.asmref/.meta and not data (Assets\Data, Assets\StreamingAssets), or when the setup step declares outputs in $env:BUILD_SETUP_OUTPUTS (semicolon list). An asset import re-processes whatever references it (the 30 GB main-scene load).
if ($mode -eq 'CodeOnly') { $chg = @(& git -C $p status --porcelain --untracked-files=all -- Assets 2>$null | ForEach-Object { ($_.Substring(3)).Trim('"') -replace '\\','/' } | Where-Object { $_ -notmatch '\.(cs|asmdef|asmref|meta)$' -and $_ -notmatch '^Assets/Data/' -and $_ -notmatch '^Assets/StreamingAssets/.*\.(json|txt|csv|md|glb|png|jpg)$' }); if ($env:BUILD_SETUP_OUTPUTS) { $chg += @($env:BUILD_SETUP_OUTPUTS -split ';' | Where-Object { $_ }) }; if ($chg.Count -gt 0) { $why += 'not code-only: ' + (($chg | Select-Object -First 8) -join ', ') + $(if ($chg.Count -gt 8) { ' (+' + ($chg.Count - 8) + ' more)' } else { '' }) } }
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
    if ($free -lt $MinFreeGB) { $why += "free RAM $free GB is under $MinFreeGB GB" }
# (cold detection is informational now: it raises the RAM floor to 16 GB, it no longer refuses; serialized-field refusal below stays)
if ($script:SerWhy) { $why += $script:SerWhy }
# COO 16:15 17 Nov: a full C: failed the texture bake at the file copy after 9 minutes. Refuse in second 1 when C: free is under the expected build size + 10 GB (bake / full build 25 GB, code-only 12 GB; FK_MINDISK_GB overrides).
try { $dfree = [math]::Round((Get-PSDrive C).Free / 1GB, 1); $dneed = $(if ($env:FK_MINDISK_GB) { [double]$env:FK_MINDISK_GB } elseif ($mode -eq 'CodeOnly') { 12 } else { 25 }); if ($dfree -lt $dneed) { $why += "C: free disk $dfree GB is under $dneed GB (expected build size + 10 GB; free space first)" } } catch { }
    # (Dom-game refusal and fresh-dom-runs-log refusal REMOVED 17 Nov 09:57 on Dom's order; the build folder his running game uses is never overwritten: see release.ps1 / prune_builds.ps1.)
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
    if ($log -and ((Get-Date) - $log.LastWriteTime).TotalHours -lt 2 -and (Select-String -Path $log.FullName -Pattern 'No valid Unity Editor license|license (is )?(invalid|expired)|Licensing: .*(fail|denied)' -Quiet)) { $why += "licence probe: newest build log $($log.Name) shows a licence error" }
    return $why
}
function Assert-BuildSafe {
    param([string]$Tag = '?', [int]$MinFreeGB = 16, [ValidateSet('Bake', 'CodeOnly')][string]$Mode = 'Bake')
    $script:BuildMode = $Mode
    . (Join-Path $PSScriptRoot '_tag_reserve.ps1'); try { $null = Reserve-Tag -Tag $Tag -Owner $(if ($env:FK_OWNER) { $env:FK_OWNER } else { "build:$Tag" }) } catch { "BUILD REFUSED ($Tag): $($_.Exception.Message)"; exit 3 }
    $script:GuardTag = $Tag
    $w = @(Get-BuildBlockers -MinFreeGB $MinFreeGB) + @(Get-OtherLaneAlone)
    if ($w.Count -gt 0) {
        "BUILD REFUSED ($Tag) at $(Get-Date -Format 'HH:mm'): " + ($w -join ' | ')
        try { $fsb = [IO.File]::Open((Join-Path $p '_agents\COORDINATION.md'), 'Append', 'Write', 'ReadWrite'); $bbb = [Text.Encoding]::UTF8.GetBytes("`r`n" + (Get-Date -Format 'yyyy-MM-dd HH:mm') + " BUILD REFUSED frankendom ${Tag}: " + ($w -join ' | ')); $fsb.Write($bbb, 0, $bbb.Length); $fsb.Close() } catch {}   # refusal reasons on the board (COO 15:44): a refused build was invisible
        exit 3
    }
    "build guard ($Tag): all clear at $(Get-Date -Format 'HH:mm')"
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
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm') BUILT frankendom HDRP_v109$Tag " + $(if ($ok) { 'ready to flip' } else { 'FAILED / incomplete (do not flip)' }) + " ($res; build_info.json " + $(if (Test-Path $info) { 'present' } else { 'MISSING' }) + ")"
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
    $w = Get-BuildBlockers -MinFreeGB $MinFreeGB
    if ($w.Count -gt 0) { "DRY RUN: build would be REFUSED at $(Get-Date -Format 'HH:mm'): " + ($w -join ' | ') } else { "DRY RUN: build would run (all clear) at $(Get-Date -Format 'HH:mm')" }
}

















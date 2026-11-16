# Frankendom build preflight (COO standing rule 09:13, Dom: recurring faults get a guard IN THE SCRIPT).
# Dot-source from any Tools\build_v109*.ps1:   . "$PSScriptRoot\_build_guard.ps1"; Assert-BuildSafe 'oq'
# Dry run (prints every check, never starts anything):   powershell -File Tools\_build_guard.ps1 -DryRun
# Refuses (exit 3, prints WHY) when ANY of these holds:
#   1. another Unity.exe is open (our Editor runs alone: one Editor at a time across all games)
#   2. a git backup / pack / push runs on any repo, or the backup lock exists (git reading files mid-write = IOException 1224, Ascent53 08:52)
#   3. free RAM is under MinFreeGB (default 24; these builds peak about 36 GB)
#   4. a Dom game is running: a HadesSlice without -camscene/-camtrace, or a frankendom dom-runs log written in the last 3 minutes with a live HadesSlice
#   5. the Unity licence probe fails (licence file missing/expired, or the newest Editor log shows a licence error)
param([switch]$DryRun, [int]$MinFreeGB = 24, [string]$Mode = 'Bake')
function Get-BuildBlockers {
    param([int]$MinFreeGB = 24)
    $why = @()
    $p = 'C:\Users\dom12\Documents\unity_test_hdrp'
    # COO 10:46 rule C amended: a CODE-ONLY build (HdrpBatch.Build, no scene bake) may run BESIDE another game's Editor (free RAM floor still applies); a scene-bake build runs alone.
    # Our OWN project's Unity.exe always blocks (one Editor per project). Mode: $script:BuildMode = 'CodeOnly' | 'Bake' (default Bake = alone).
    $mode = if ($script:BuildMode) { $script:BuildMode } else { 'Bake' }
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
    $hs = @(Get-CimInstance Win32_Process -Filter "Name='HadesSlice.exe'" -ErrorAction SilentlyContinue)
    # COO 10:53: Dom = a fresh dom-runs log, or a launcher/explorer parent; 'no QA flags' alone is NOT proof (boot-check players have none)
    $plain = @($hs | Where-Object {
        $_.CommandLine -notmatch '-camscene|-camtrace|-fpshots|-benchOut' -and (
            ((Get-ChildItem (Join-Path $p 'dom-runs') -Filter 'frankendom_*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 | Where-Object { ((Get-Date) - $_.LastWriteTime).TotalMinutes -lt 3 }) -ne $null) -or
            ((Get-CimInstance Win32_Process -Filter "ProcessId=$($_.ParentProcessId)" -ErrorAction SilentlyContinue).Name -match '^(explorer|cmd|powershell|pwsh|conhost)\.exe$')) })
    if ($plain.Count -gt 0) { $why += "a Dom game is running (HadesSlice pid " + (($plain | ForEach-Object { $_.ProcessId }) -join ',') + ", fresh dom-runs log or launcher parent)" }
    $dl = Get-ChildItem (Join-Path $p 'dom-runs') -Filter 'frankendom_*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($dl -and $hs.Count -gt 0 -and ((Get-Date) - $dl.LastWriteTime).TotalMinutes -lt 3) { $why += "fresh Dom run log $($dl.Name) with a live HadesSlice" }
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
    param([string]$Tag = '?', [int]$MinFreeGB = 24, [ValidateSet('Bake', 'CodeOnly')][string]$Mode = 'Bake')
    $script:BuildMode = $Mode
    $w = Get-BuildBlockers -MinFreeGB $MinFreeGB
    if ($w.Count -gt 0) {
        "BUILD REFUSED ($Tag) at $(Get-Date -Format 'HH:mm'): " + ($w -join ' | ')
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
}
if ($DryRun) {
    $script:BuildMode = $Mode
    $w = Get-BuildBlockers -MinFreeGB $MinFreeGB
    if ($w.Count -gt 0) { "DRY RUN: build would be REFUSED at $(Get-Date -Format 'HH:mm'): " + ($w -join ' | ') } else { "DRY RUN: build would run (all clear) at $(Get-Date -Format 'HH:mm')" }
}

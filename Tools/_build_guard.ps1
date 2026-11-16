# Frankendom build preflight (COO standing rule 09:13, Dom: recurring faults get a guard IN THE SCRIPT).
# Dot-source from any Tools\build_v109*.ps1:   . "$PSScriptRoot\_build_guard.ps1"; Assert-BuildSafe 'oq'
# Dry run (prints every check, never starts anything):   powershell -File Tools\_build_guard.ps1 -DryRun
# Refuses (exit 3, prints WHY) when ANY of these holds:
#   1. another Unity.exe is open (our Editor runs alone: one Editor at a time across all games)
#   2. a git backup / pack / push runs on any repo, or the backup lock exists (git reading files mid-write = IOException 1224, Ascent53 08:52)
#   3. free RAM is under MinFreeGB (default 24; these builds peak about 36 GB)
#   4. a Dom game is running: a HadesSlice without -camscene/-camtrace, or a frankendom dom-runs log written in the last 3 minutes with a live HadesSlice
#   5. the Unity licence probe fails (licence file missing/expired, or the newest Editor log shows a licence error)
param([switch]$DryRun, [int]$MinFreeGB = 24)
function Get-BuildBlockers {
    param([int]$MinFreeGB = 24)
    $why = @()
    $p = 'C:\Users\dom12\Documents\unity_test_hdrp'
    try { $u = @(Get-Process Unity -ErrorAction Stop) } catch { $u = @() }
    if ($u.Count -gt 0) { $why += "another Unity.exe is open (pids " + (($u | ForEach-Object { $_.Id }) -join ',') + ")" }
    $g = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        ($_.Name -match '^git(-[a-z-]+)?\.exe$' -and $_.CommandLine -match '\b(pack-objects|gc|repack|push|add|commit|index-pack)\b') -or
        ($_.Name -match 'powershell|pwsh' -and $_.CommandLine -match 'backup(_hourly)?\.ps1') })
    if ($g.Count -gt 0) { $why += "a git backup/pack/push is running (pids " + (($g | ForEach-Object { $_.ProcessId }) -join ',') + ")" }
    $lock = Join-Path $p '_agents\pool\backup.lock'
    if (Test-Path $lock) { if (((Get-Date) - (Get-Item $lock).LastWriteTime).TotalMinutes -lt 30) { $why += "backup lock exists ($lock)" } }
    $free = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB, 1)
    if ($free -lt $MinFreeGB) { $why += "free RAM $free GB is under $MinFreeGB GB" }
    $hs = @(Get-CimInstance Win32_Process -Filter "Name='HadesSlice.exe'" -ErrorAction SilentlyContinue)
    $plain = @($hs | Where-Object { $_.CommandLine -notmatch '-camscene|-camtrace' })
    if ($plain.Count -gt 0) { $why += "a Dom game is running (plain HadesSlice pid " + (($plain | ForEach-Object { $_.ProcessId }) -join ',') + ")" }
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
    param([string]$Tag = '?', [int]$MinFreeGB = 24)
    $w = Get-BuildBlockers -MinFreeGB $MinFreeGB
    if ($w.Count -gt 0) {
        "BUILD REFUSED ($Tag) at $(Get-Date -Format 'HH:mm'): " + ($w -join ' | ')
        exit 3
    }
    "build guard ($Tag): all clear at $(Get-Date -Format 'HH:mm')"
}
if ($DryRun) {
    $w = Get-BuildBlockers -MinFreeGB $MinFreeGB
    if ($w.Count -gt 0) { "DRY RUN: build would be REFUSED at $(Get-Date -Format 'HH:mm'): " + ($w -join ' | ') } else { "DRY RUN: build would run (all clear) at $(Get-Date -Format 'HH:mm')" }
}

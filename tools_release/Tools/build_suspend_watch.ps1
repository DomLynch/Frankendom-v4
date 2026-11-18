# build_suspend_watch.ps1: COO 09:24 17 Nov. A Frankendom build (this project's Unity.exe + its children) PAUSES within ~1-2 s of a Dom launch of any game (a fresh dom-runs log in Frankendom / Armagedom / FrontierDom with a live game process) and RESUMES when that game ends. Process suspend = NtSuspendProcess (nothing is killed, the build continues after the resume).
# Started detached by Assert-BuildSafe (Tools\_build_guard.ps1). Exits when no Editor of this project has been seen for 90 s after one was, or after -MaxMinutes.
# Test: powershell -File Tools\build_suspend_watch.ps1 -DryRun   (prints what it would do; uses no process)
param([switch]$DryRun, [int]$PollMs = 1000, [int]$MaxMinutes = 60, [int[]]$TargetPids = @(), [string[]]$DomRunsDirs = @(), [string[]]$GameProcNames = @('HadesSlice','FronteirDom','FrontierDom','ARMAGEDOM_Slice0'), [int]$FreshSeconds = 20, [switch]$NoBoard)
$p = $env:FK_ROOT
if ($DomRunsDirs.Count -eq 0) { $DomRunsDirs = @("$p\dom-runs", "$env:USERPROFILE\work\armagedom-unity\Logs\dom-runs", "$env:USERPROFILE\work\fronteirdom\Logs\dom-runs") }
Add-Type -Namespace Nt -Name P -MemberDefinition '[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(System.IntPtr h); [DllImport("ntdll.dll")] public static extern int NtResumeProcess(System.IntPtr h);' -ErrorAction SilentlyContinue
function Board($t) { if ($NoBoard -or $DryRun) { Write-Output "BOARD(not written): $t"; return }; Add-Content "$p\_agents\COORDINATION.md" ("`n" + (Get-Date -Format 'yyyy-MM-dd HH:mm') + " build_suspend_watch: " + $t) }
function Get-OurEditorPids {
    if ($TargetPids.Count -gt 0) { return @($TargetPids | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }) }
    $roots = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match 'unity_test_hdrp' } | ForEach-Object { $_.ProcessId })
    if ($roots.Count -eq 0) { return @() }
    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue); $set = [System.Collections.Generic.HashSet[int]]::new(); foreach ($r in $roots) { [void]$set.Add($r) }
    $grew = $true; while ($grew) { $grew = $false; foreach ($x in $all) { if ($set.Contains([int]$x.ParentProcessId) -and -not $set.Contains([int]$x.ProcessId)) { [void]$set.Add([int]$x.ProcessId); $grew = $true } } }
    return @($set)
}
function Test-DomGameLive {
    $live = @(Get-Process -Name $GameProcNames -ErrorAction SilentlyContinue); if ($live.Count -eq 0) { return $null }
    foreach ($d in $DomRunsDirs) { $l = Get-ChildItem $d -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1; if ($l -and (((Get-Date) - $l.LastWriteTime).TotalMinutes -lt 3 -or ((Get-Date) - $l.CreationTime).TotalSeconds -lt 600)) { return "$($live[0].ProcessName) pid $($live[0].Id) + fresh log $($l.Name)" } }
    return $null
}
if ($DryRun) {
    "DRY RUN build_suspend_watch: poll $PollMs ms; dirs: $($DomRunsDirs -join ' | '); games: $($GameProcNames -join ',')"
    "current Dom game: " + ($(Test-DomGameLive) -replace '^$', 'none'); "our Editor pids: " + ((Get-OurEditorPids) -join ',')
    "WOULD: on a fresh dom-runs log + live game -> NtSuspendProcess on every our-Editor pid within $([math]::Round($PollMs/1000,1)) s, post BUILD SUSPENDED; on game exit -> NtResumeProcess, post RESUMED."
    exit 0
}
$suspended = @{}; $started = Get-Date; $seen = $false; $lastSeen = Get-Date
while (((Get-Date) - $started).TotalMinutes -lt $MaxMinutes) {
    $pids = Get-OurEditorPids; if ($pids.Count -gt 0) { $seen = $true; $lastSeen = Get-Date } elseif ($seen -and ((Get-Date) - $lastSeen).TotalSeconds -gt 90 -and $suspended.Count -eq 0) { break }
    $dom = Test-DomGameLive
    if ($dom -and $suspended.Count -eq 0 -and $pids.Count -gt 0) {
        foreach ($id in $pids) { $h = (Get-Process -Id $id -ErrorAction SilentlyContinue).Handle; if ($h) { [void][Nt.P]::NtSuspendProcess($h); $suspended[$id] = $true } }
        Board "BUILD SUSPENDED ($($suspended.Count) processes, pids $(($suspended.Keys) -join ',')): Dom launched a game ($dom). Resumes when it ends."
    } elseif (-not $dom -and $suspended.Count -gt 0) {
        foreach ($id in @($suspended.Keys)) { $pr = Get-Process -Id $id -ErrorAction SilentlyContinue; if ($pr) { [void][Nt.P]::NtResumeProcess($pr.Handle) }; $suspended.Remove($id) }
        Board "BUILD RESUMED: Dom's game ended."
    }
    Start-Sleep -Milliseconds $PollMs
}
foreach ($id in @($suspended.Keys)) { $pr = Get-Process -Id $id -ErrorAction SilentlyContinue; if ($pr) { [void][Nt.P]::NtResumeProcess($pr.Handle) } }

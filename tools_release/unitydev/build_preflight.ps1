# Frankendom build preflight (COO rule 09:08, 16 Nov): refuse to start an Editor build when it would collide.
# Usage: & _agents\unitydev\build_preflight.ps1 [-Licence] ; exit 0 = go, non-zero = refused (reason printed).
param([switch]$Licence, [double]$FloorGB = 12, [ValidateSet('Full','CodeOnly')][string]$Mode = 'Full')   # CodeOnly (Strategy/COO 10:46): a plain code build may run beside ANOTHER game's Editor at 24 GB free; THIS project's Unity.exe and this repo's git/lock still block
$why = @()
# (1) any Unity.exe open
$u = Get-Process Unity -EA SilentlyContinue; if ($Mode -eq 'CodeOnly' -and $u) { $FloorGB = [math]::Max($FloorGB, 24); $mine = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | ? { $_.CommandLine -match 'unity_test_hdrp' } | % ProcessId); $u = $u | ? { $mine -contains $_.Id } }; if ($u) { $why += "Unity.exe open (pid " + (($u | % Id) -join ',') + ")" }
# (2) a git backup/pack/push running on any repo, or the backup lock file
$g = Get-CimInstance Win32_Process -EA SilentlyContinue | ? { $_.Name -eq 'git.exe' -and $_.CommandLine -match 'pack-objects|send-pack|push|gc|repack' -and ($Mode -ne 'CodeOnly' -or $_.CommandLine -match 'unity_test_hdrp|DomLynch/Frank|frankendom') }; if ($g) { $why += "git backup/pack/push running (pid " + (($g | % ProcessId) -join ',') + ")" }
foreach ($lk in "$env:FK_ROOT\.git\index.lock","$env:FK_ROOT\_agents\pool\backup.lock") { if (Test-Path $lk) { $why += "lock file $lk" } }
# (3) free RAM floor
$free = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB, 1); if ($free -lt $FloorGB) { $why += "free RAM $free GB under the $FloorGB GB floor" }
# (4) a Dom game running (plain launch of any game exe)
$gm = Get-Process HadesSlice, FronteirDom, FrontierDom -EA SilentlyContinue; if ($Mode -eq 'CodeOnly' -and $gm) { $fresh = $false; foreach ($dr in @("$env:FK_ROOT\dom-runs","$env:USERPROFILE\work\fronteirdom\dom-runs","$env:USERPROFILE\work\armagedom-unity\dom-runs")) { if (Test-Path $dr) { $dl = Get-ChildItem $dr -Filter *.log -EA SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1; if ($dl -and ((Get-Date) - $dl.LastWriteTime).TotalMinutes -lt 3) { $fresh = $true } } }; $gm = $gm | ? { $pr = Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)"; $cl = "$($pr.CommandLine)"; $par = Get-Process -Id $pr.ParentProcessId -EA SilentlyContinue; ($cl -notmatch '-fpshots|-benchOut|-camscene|-camtrace|-qa') -and ($fresh -or ($par -and $par.Name -match '^(explorer|cmd)$')) } }; if ($gm) { $why += "a game is running (" + (($gm | % { $_.Name + ' ' + $_.Id }) -join ', ') + "), Dom's or a QA copy" }
# (5) licence probe (only with -Licence: 20 s batchmode probe)
if ($Licence -and -not $u) {
  $ex = 'C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $lg = Join-Path $env:TEMP 'fk_licence_probe.log'
  $pr = Start-Process $ex -ArgumentList "-batchmode -quit -nographics -silent-crashes -logFile `"$lg`"" -PassThru
  if (-not $pr.WaitForExit(20000)) { try { $pr.Kill() } catch {} }
  if ((Test-Path $lg) -and (Select-String $lg -Pattern 'Access token is unavailable|No valid Unity Editor license' -Quiet)) { $why += "Unity licence probe failed (Access token unavailable)" }
}
if ($why.Count -gt 0) { Write-Host ("PREFLIGHT REFUSED: " + ($why -join '; ')); exit 3 }
Write-Host "PREFLIGHT OK (free RAM $free GB, no Unity.exe, no backup, no game)"; exit 0





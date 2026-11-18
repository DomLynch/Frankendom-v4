param([string]$NotBefore = '')
$P = $env:FK_ROOT
function WindowBusy {
  $now = Get-Date; $lines = @(Get-Content "$P\_agents\COORDINATION.md" -Tail 120 -EA SilentlyContinue); $busy = @()
  for ($i = 0; $i -lt $lines.Count; $i++) { $l = $lines[$i]
    if ($false -and $l -match 'FOCUS TAKEN (\w[\w ]*?) until (\d{1,2}):(\d{2})' -and $l -notmatch '(?i)FOCUS TAKEN Frankendom unity-dev') { $o = $Matches[1]; $t = (Get-Date).Date.AddHours([int]$Matches[2]).AddMinutes([int]$Matches[3]); $relAfter = $false; for ($k = $i + 1; $k -lt $lines.Count; $k++) { if ($lines[$k] -match "FOCUS RELEASED $o") { $relAfter = $true } }; if ($t -gt $now -and -not $relAfter) { $busy += "FOCUS $o until $($t.ToString('HH:mm'))" } }
    if ($l -match '(?i)(priority|exclusive)' -and $l -notmatch 'COO SWEEP|z-Strategy|SWEEP RULINGS') { $ts = @([regex]::Matches($l, '(?<![\d.])(\d{1,2}):(\d{2})(?![\d])') | % { (Get-Date).Date.AddHours([int]$_.Groups[1].Value).AddMinutes([int]$_.Groups[2].Value) } | ? { $_.Hour -lt 24 }); if ($ts.Count -ge 1) { $hi = ($ts | measure -Maximum).Maximum; $lo = ($ts | measure -Minimum).Minimum; $head = [regex]::Match($l, '^\d{4}-\d\d-\d\d (\d{1,2}):(\d{2})'); if ($head.Success) { $stamp = (Get-Date).Date.AddHours([int]$head.Groups[1].Value).AddMinutes([int]$head.Groups[2].Value); $lo = @($lo, $stamp | measure -Minimum).Minimum }; if ($now -ge $lo.AddMinutes(-3) -and $now -lt $hi) { $busy += "WINDOW line until $($hi.ToString('HH:mm')): " + $l.Substring(0, [Math]::Min(70, $l.Length)) } } }
  }
  # COO 10:53: Dom = a FRESH dom-runs log (written in the last 3 min) or a launcher/explorer/cmd/powershell PARENT; 'no QA flags' alone is NOT proof (boot-check and capture players have none)
  $freshDom = $false; foreach ($dr in @("$env:FK_ROOT\dom-runs","$env:USERPROFILE\work\fronteirdom\dom-runs","$env:USERPROFILE\work\armagedom-unity\dom-runs")) { if (Test-Path $dr) { $dl = Get-ChildItem $dr -Filter *.log -EA SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1; if ($dl -and ((Get-Date) - $dl.LastWriteTime).TotalMinutes -lt 3) { $freshDom = $true } } }
  foreach ($pr in @(Get-CimInstance Win32_Process -EA SilentlyContinue | ? { $_.Name -match '^(HadesSlice|ARMAGEDOM.*|FrontierDom|FronteirDom|Frankendom.*)\.exe$' })) { $cl = "$($pr.CommandLine)"; $par = Get-Process -Id $pr.ParentProcessId -EA SilentlyContinue; $launcher = $par -and $par.Name -match '^(explorer|cmd|powershell|pwsh|conhost|steam|steamwebhelper)$'
    if ($cl -match '-camscene|-camtrace|-fpshots|-benchOut') { Write-Host "note: another lane's QA player running (pid $($pr.ProcessId)), allowed beside it at 16 GB+ free (COO 10:59)" }
    elseif ($freshDom -or ($launcher -and $par.Name -match '^(explorer|cmd)$')) { $busy += "DOM PLAYER running (pid $($pr.ProcessId), $($pr.Name)): fresh dom-runs log or explorer/cmd parent" }
    else { Write-Host "note: pid $($pr.ProcessId) $($pr.Name) has no QA flags but no fresh dom-runs log and no explorer/cmd parent: treated as a boot-check/capture player, not Dom" } }
  if ($NotBefore -ne '' -and $now -lt [datetime]::Parse($NotBefore)) { $busy += "clock hold until $NotBefore" }
  $ramGb = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB, 1); if ($ramGb -lt 12) { $busy += "free RAM $ramGb GB < 12 (COO rule)" }
  return $busy
}
$b = WindowBusy; if ($b.Count -gt 0) { $b | % { Write-Output $_ }; exit 9 } else { Write-Output 'free'; exit 0 }

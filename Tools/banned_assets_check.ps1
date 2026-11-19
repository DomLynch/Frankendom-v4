# banned_assets_check.ps1 -Root <dir[,dir]> -Caller <name> [-Quiet]
# Shared guard (19 Nov, goblin purge). Scans every file under -Root for names and SHA256s listed in Tools\banned_assets.txt.
# Output: one string per hit ("BANNED ASSET <path> (<why>)"). Writes ONE board line when there are hits. Never exits (callers run it in-process with &): the CALLER refuses when the output is non-empty.
# Lead-only override (rollback to an old build): $env:FK_BANNED_ALLOW = '<reason>' -> hits are reported as WARN and the caller is told nothing (output empty), the reason goes on the board.
param([Parameter(Mandatory)][string[]]$Root, [string]$Caller = '?', [switch]$Quiet)
$p = 'C:\Users\dom12\Documents\unity_test_hdrp'
$names = @{}; $shas = @{}; $sizes = @{}
foreach ($l in Get-Content (Join-Path $p 'Tools\banned_assets.txt') -ErrorAction Stop) {
  $t = $l.Trim(); if (-not $t -or $t.StartsWith('#')) { continue }
  $f = $t -split '\s+'
  if ($f[0] -eq 'name' -and $f.Count -ge 2) { $names[$f[1].ToLower()] = 1; $names[($f[1] + '.meta').ToLower()] = 1 }
  elseif ($f[0] -eq 'sha256' -and $f.Count -ge 3) { $shas[$f[1].ToLower()] = $f[3]; $sizes[[int64]$f[2]] = 1 }
}
if ($names.Count -eq 0 -and $shas.Count -eq 0) { throw 'banned_assets.txt has no entries (fails closed)' }
$hits = @()
foreach ($r in $Root) {
  if (-not (Test-Path $r)) { continue }
  foreach ($fp in [IO.Directory]::EnumerateFiles($r, '*', [IO.SearchOption]::AllDirectories)) {
    $leaf = [IO.Path]::GetFileName($fp).ToLower(); $why = $null
    if ($names.ContainsKey($leaf)) { $why = 'name' }
    else { try { $len = ([IO.FileInfo]$fp).Length; if ($sizes.ContainsKey([int64]$len)) { $h = (Get-FileHash $fp -Algorithm SHA256).Hash.ToLower(); if ($shas.ContainsKey($h)) { $why = 'sha256 ' + $shas[$h] } } } catch { } }
    if ($why) { $hits += "BANNED ASSET $fp ($why)" }
  }
}
if ($hits.Count) {
  $msg = "BANNED ASSET REFUSED by $Caller ($($hits.Count) file(s)): " + (($hits | Select-Object -First 3 | ForEach-Object { $_ -replace '^BANNED ASSET ', '' }) -join ' | ') + ' ... remove them (quarantine D:\_quarantine_goblin_20261119) and retry'
  if ($env:FK_BANNED_ALLOW) { $msg = "BANNED ASSET OVERRIDE by $Caller, reason: $($env:FK_BANNED_ALLOW) ($($hits.Count) file(s))" }
  try { $fs = [IO.File]::Open((Join-Path $p '_agents\COORDINATION.md'), 'Append', 'Write', 'ReadWrite'); $bb = [Text.Encoding]::UTF8.GetBytes("`r`n" + (Get-Date -Format 'yyyy-MM-dd HH:mm') + ' ' + $msg); $fs.Write($bb, 0, $bb.Length); $fs.Close() } catch { }
  if ($env:FK_BANNED_ALLOW) { if (-not $Quiet) { Write-Host $msg }; return }
  $hits
}

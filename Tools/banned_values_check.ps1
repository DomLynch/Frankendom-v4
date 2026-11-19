# banned_values_check.ps1 -Root <dir[,dir]> -Caller <name>
# Shared guard (19 Nov, COO ruling): refuses data values listed in Tools\banned_values.json (file/selector/key/bannedValue).
# Scans every <entry.file> under -Root. Output: one string per hit. On hits writes ONE board line
# 'BANNED VALUE REFUSED: <file> <selector>.<key> = <value> (<reason>)' and exits 5 (callers run it with & and also test the output).
# Lead-only override: $env:FK_BANNED_ALLOW = '<reason>' (reported on the board as OVERRIDE, no refusal).
param([Parameter(Mandatory)][string[]]$Root, [string]$Caller = '?', [switch]$Quiet)
$p = 'C:\Users\dom12\Documents\unity_test_hdrp'
$cfg = (Get-Content (Join-Path $p 'Tools\banned_values.json') -Raw -ErrorAction Stop).TrimStart([char]0xFEFF) | ConvertFrom-Json
$entries = @($cfg.entries); if ($entries.Count -eq 0) { throw 'banned_values.json has no entries (fails closed)' }
function Test-Match($val, $ban) {
  if ($null -eq $val) { return $false }
  if ($ban -is [string]) { return ([string]$val -match $ban) }
  try { return ([double]$val -eq [double]$ban) } catch { return $false }
}
function Walk($node, $e, $file, [ref]$hits) {
  if ($null -eq $node -or $node -is [string] -or $node -is [ValueType]) { return }
  if ($node -is [System.Collections.IEnumerable]) { foreach ($i in $node) { Walk $i $e $file $hits }; return }
  $props = $node.PSObject.Properties
  $idp = $props['id']; $isSel = ($idp -and [string]$idp.Value -eq $e.selector)
  foreach ($pr in $props) {
    if ($isSel -and $pr.Name -eq $e.key -and (Test-Match $pr.Value $e.bannedValue)) { $hits.Value += "BANNED VALUE REFUSED: $file $($e.selector).$($e.key) = $($pr.Value) ($($e.reason))" }
    elseif ($pr.Name -eq $e.selector -and $pr.Value -and $pr.Value -isnot [string] -and $pr.Value -isnot [ValueType]) {
      $kp = $pr.Value.PSObject.Properties[$e.key]
      if ($kp -and (Test-Match $kp.Value $e.bannedValue)) { $hits.Value += "BANNED VALUE REFUSED: $file $($e.selector).$($e.key) = $($kp.Value) ($($e.reason))" }
    }
    Walk $pr.Value $e $file $hits
  }
}
$hits = @()
foreach ($r in $Root) {
  if (-not (Test-Path $r)) { continue }
  foreach ($e in $entries) {
    foreach ($fp in [IO.Directory]::EnumerateFiles($r, $e.file, [IO.SearchOption]::AllDirectories)) {
      try { $j = (Get-Content $fp -Raw).TrimStart([char]0xFEFF, [char]0xEF, [char]0xBB, [char]0xBF) | ConvertFrom-Json -ErrorAction Stop }
      catch { $hits += "BANNED VALUE CHECK could not parse $fp (fails closed)"; continue }
      $h = @(); Walk $j $e $fp ([ref]$h); $hits += $h
    }
  }
}
$hits = @($hits | Select-Object -Unique)
if ($hits.Count) {
  $msg = ($hits | Select-Object -First 3) -join ' | '; $msg += " [$Caller, $($hits.Count) hit(s)]"
  if ($env:FK_BANNED_ALLOW) { $msg = "BANNED VALUE OVERRIDE by $Caller, reason: $($env:FK_BANNED_ALLOW) ($($hits.Count) hit(s))" }
  try { $fs = [IO.File]::Open((Join-Path $p '_agents\COORDINATION.md'), 'Append', 'Write', 'ReadWrite'); $bb = [Text.Encoding]::UTF8.GetBytes("`r`n" + (Get-Date -Format 'yyyy-MM-dd HH:mm') + ' ' + $msg); $fs.Write($bb, 0, $bb.Length); $fs.Close() } catch { }
  if ($env:FK_BANNED_ALLOW) { return }
  $hits; exit 5
}

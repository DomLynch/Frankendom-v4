param([Parameter(Mandatory=$true)][string]$Dest,[switch]$NoCompile)
# laneINT107: the static gates on a scratch tree laneINT107\<Dest> (read-only on live; output in laneINT107\out\<Dest>_*.txt).
#   Roslyn Runtime + Editor 0 errors, missing_keys (runtime + editor), data_gates (--added-keys-ok), no_dracula_check --allow-owned.
$L=$env:FK_ROOT; $H="$L\_agents\laneINT107"; $R="$L\_agents\laneINT107\scratch\$Dest"; $O="$L\_agents\laneINT107\out"; New-Item -ItemType Directory -Force $O | Out-Null
$res=@()
if(-not $NoCompile){
  $res += (& powershell -NoProfile -File "$H\compile107.ps1" -Root $R -Name $Dest) | Out-String
  $res += (& powershell -NoProfile -File "$H\compile107.ps1" -Root $R -Name $Dest -Editor) | Out-String
}
$env:HCKIT7_LIVE=$R; $env:HCKIT2_LIVE=$R
Set-Location $R
$mk = & python -I Tools\missing_keys.py --quiet 2>&1 | Select-String 'RESULT'; $res += "missing_keys runtime: $mk"
$mke = & python -I Tools\missing_keys.py --quiet --editor 2>&1 | Select-String 'RESULT'; $res += "missing_keys editor : $mke"
$env:HCKIT2_LIVE=$null
$dg = & python -I Tools\data_gates.py --added-keys-ok --quiet --data "$R\Assets\Data" --sa "$R\Assets\StreamingAssets" 2>&1; $res += "data_gates: " + (($dg | Select-String 'RESULT') -join ''); ($dg | Select-String 'FAIL \]') | %{ $res += "   $_" }
Set-Location $L
$nd = & python -I _agents\laneNODRAC\tools\no_dracula_check.py --root $L --overlay $R --allow-owned --quiet 2>&1; $res += "no_dracula: " + (($nd | Select-String 'SUMMARY|RESULT') -join ' | ')
$res | Set-Content "$O\${Dest}_gates.txt" -Encoding UTF8
$res | ForEach-Object { $_.TrimEnd() }

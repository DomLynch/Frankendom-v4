# hot_vs_tree.ps1 (unity-dev2): BEFORE any CODE/CHAIN build, list every json where the LIVE build's StreamingAssets differs from Assets\Data
# (a CODE build is made from the tree, so a hot release not written back would be lost). Exit 3 if any differ. Usage: .\hot_vs_tree.ps1 [-Build HDRP_v109xx]
param([string]$Build)
$P=$env:FK_ROOT
if(-not $Build){ $Build=([regex]::Match((Get-Content "$P\Builds\PLAY.bat" -Raw),'HDRP_\w+')).Value }
$S="$P\Builds\$Build\HadesSlice_Data\StreamingAssets"; $diff=@()
foreach($f in Get-ChildItem "$S\*.json"){ $t="$P\Assets\Data\$($f.Name)"; if(Test-Path $t){ if((Get-FileHash $f.FullName -Algorithm MD5).Hash -ne (Get-FileHash $t -Algorithm MD5).Hash){ $diff+=$f.Name } } }
"hot_vs_tree $Build : $($diff.Count) json differ from Assets\Data"; $diff | % { "  $_" }
if($diff.Count){ exit 3 }

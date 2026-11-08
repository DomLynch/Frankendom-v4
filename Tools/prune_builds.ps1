# unity-dev: keep the LAST 3 HDRP_v109* builds (Dom standing order 2026-11-08). Never delete a build any launcher (Builds\*.bat/*.ps1) points at.
# Builds only (Builds\HDRP_v109*), never saves/Library/other folders. Aborts if a HadesSlice from one of them runs. Log: _agents\unitydev\prune_builds.log. -WhatIf only lists.
param([int]$KeepN=3,[switch]$WhatIf)
$B='C:\Users\dom12\Documents\unity_test_hdrp\Builds'; $log='C:\Users\dom12\Documents\unity_test_hdrp\_agents\unitydev\prune_builds.log'
$all=Get-ChildItem $B -Directory -Filter 'HDRP_v109*' | Sort LastWriteTime
$keep=@($all | Select -Last $KeepN | %{$_.Name})
$ref=@(Get-ChildItem $B -File | ?{$_.Name -like 'PLAY*.bat' -or $_.Name -like 'play_zone_at*.ps1'} | Select-String 'HDRP_v109\w*' -AllMatches | %{$_.Matches.Value} | Sort -Unique)
$run=@(Get-Process HadesSlice -EA SilentlyContinue | %{$_.Path})
$free0=(Get-PSDrive C).Free/1GB; $gone=@()
foreach($d in $all){ if($keep -contains $d.Name -or $ref -contains $d.Name){continue}; if($run | ?{$_ -like "$($d.FullName)*"}){Add-Content $log "skip (running) $($d.Name)";continue}
  if($WhatIf){"would remove $($d.Name)";continue}; Remove-Item $d.FullName -Recurse -Force -EA Continue; $gone+=$d.Name }
$free1=(Get-PSDrive C).Free/1GB
$msg="{0} prune keep={1} launcher-refs={2} removed=[{3}] freed {4:N1} GB (free {5:N1})" -f (Get-Date -Format s),($keep -join ','),($ref -join ','),($gone -join ','),($free1-$free0),$free1
Add-Content $log $msg; $msg


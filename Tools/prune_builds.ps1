# unity-dev: keep the LAST 3 HDRP_v109* builds (Dom standing order 2026-11-08). Never delete a build any launcher (Builds\*.bat/*.ps1) points at.
# Builds only (Builds\HDRP_v109*), never saves/Library/other folders. Aborts if a HadesSlice from one of them runs. Log: _agents\unitydev\prune_builds.log. -WhatIf only lists.
param([int]$KeepN=3,[int]$MinAgeMin=10,[switch]$WhatIf)   # MinAgeMin: a build folder touched in the last N minutes (a QA copy a proof is about to start or is running) is never pruned. 21 Nov fix (FK Lead 20 Nov): was 30, and with a release every few minutes nothing ever aged out (23 folders, 12 GB each, the keep-last-3 rule silently not running)
$B='C:\Users\dom12\Documents\unity_test_hdrp\Builds'; $log='C:\Users\dom12\Documents\unity_test_hdrp\_agents\unitydev\prune_builds.log'
$all=Get-ChildItem $B -Directory -Filter 'HDRP_v109*' | Sort LastWriteTime
# FK Lead 20 Nov: the last KeepN are counted over RELEASE builds only (QA copies HDRP_v109k?? are made every minute and had pushed the real releases out of the 'last 3'); builds pinned by a still-pending proof request (-Build in fk_proofbus\requests\*.json whose id is not DONE/FAIL/CANCELLED in state.json) are kept too.
$rel=@($all | ?{$_.Name -notmatch '^HDRP_v109k[a-z]{2}$'})
$keep=@($rel | Select -Last $KeepN | %{$_.Name})
try { $stj=Get-Content 'C:\Users\dom12\Documents\unity_test_hdrp\_agents\laneLG\fk_proofbus\state.json' -Raw | ConvertFrom-Json
  foreach($rf in Get-ChildItem 'C:\Users\dom12\Documents\unity_test_hdrp\_agents\laneLG\fk_proofbus\requests' -Filter *.json -EA SilentlyContinue){ try { $j=Get-Content $rf.FullName -Raw | ConvertFrom-Json; $sp=$stj.PSObject.Properties[$j.id]; if($sp -and ($sp.Value.status -in 'DONE','FAIL','CANCELLED','SKIPPED')){continue}; $aa=@($j.args); $bi=[array]::IndexOf($aa,'-Build'); if($bi -ge 0 -and $aa[$bi+1] -match '^HDRP_v109\w+$'){ $keep+=$aa[$bi+1] } } catch {} } } catch {}
$ref=@(Get-ChildItem $B -File | ?{$_.Name -like 'PLAY*.bat' -or $_.Name -like 'play_zone_at*.ps1'} | Select-String 'HDRP_v109\w*' -AllMatches | %{$_.Matches.Value} | Sort -Unique)
$run=@(Get-Process HadesSlice -EA SilentlyContinue | %{$_.Path})
$free0=(Get-PSDrive C).Free/1GB; $gone=@()
foreach($d in $all){ if($keep -contains $d.Name -or $ref -contains $d.Name){continue}; if(Test-Path (Join-Path $d.FullName 'KEEP.txt')){continue}; if(Test-Path (Join-Path $B ($d.Name+'.reserved'))){ $rv=(Get-Item (Join-Path $B ($d.Name+'.reserved'))).LastWriteTime; if(((Get-Date)-$rv).TotalMinutes -lt 240){Add-Content $log "skip (tag reserved in the last 4 h) $($d.Name)";continue} }   # a reserved tag = an owner (gateproof, deploy_data, a build) still works on it
$t0=@($d.LastWriteTime,$d.CreationTime); $bi0=Join-Path $d.FullName 'build_info.json'; if(Test-Path $bi0){$t0+=(Get-Item $bi0).LastWriteTime}; $newest=($t0|Sort -Descending|Select -First 1); $minAge=$(if($d.Name -match '^HDRP_v109k[a-z]{2}$'){[Math]::Max($MinAgeMin,90)}else{$MinAgeMin}); if(((Get-Date)-$newest).TotalMinutes -lt $minAge){Add-Content $log "skip (younger than $minAge min) $($d.Name)";continue}   # 20 Nov 23:2x: QA copies keep 90 min by the NEWEST of folder/creation/build_info time (rows3_decap_off lost its copy HDRP_v109kxr to the prune between deploy_data and the player start: 'WorkingDirectory invalid')
 if($run | ?{$_ -like "$($d.FullName)*"}){Add-Content $log "skip (running) $($d.Name)";continue}
  if($WhatIf){"would remove $($d.Name)";continue}; Remove-Item $d.FullName -Recurse -Force -EA Continue; $gone+=$d.Name }
$free1=(Get-PSDrive C).Free/1GB
$msg="{0} prune keep={1} launcher-refs={2} removed=[{3}] freed {4:N1} GB (free {5:N1})" -f (Get-Date -Format s),($keep -join ','),($ref -join ','),($gone -join ','),($free1-$free0),$free1
Add-Content $log $msg; $msg


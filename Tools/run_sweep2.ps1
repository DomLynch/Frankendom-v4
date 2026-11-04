# Perf sweep v2 (perf helper). Waits for UnrealEditor to exit, then runs one player at a time. Results: Shots\sweep2\_sweep2_results.txt
# -Only limits the runs (use the old-switch subset until the player is rebuilt with the new RuntimeQuality.cs: baseline,no_clouds,no_fog,no_ssgi,no_ssr,no_contact,no_shadows,scale67,scale50,all_off_scale67).
param([int]$MaxWaitMin=120,[string[]]$Only=@())
$root="C:\Users\dom12\Documents\unity_test_hdrp"; $exe="$root\Builds\HDRP_sweep\HadesSlice.exe"; $out="$root\Shots\sweep2"; New-Item -ItemType Directory -Force $out | Out-Null
$Only=@($Only | ForEach-Object { $_ -split "," });
$t0=Get-Date; while((Get-Process UnrealEditor* -ea 0) -and ((Get-Date)-$t0).TotalMinutes -lt $MaxWaitMin){ Start-Sleep 30 }
if(Get-Process UnrealEditor* -ea 0){ "UE still running after wait" | Set-Content "$out\_sweep2_results.txt"; exit }
$base=@{clouds=$true;volumetricFog=$true;ssgi=$true;ssr=$true;contactShadows=$true;shadows=$true;ssao=$true;ssgiFullRes=$true;renderScale=100}
$runs=[ordered]@{
 baseline=@{}; no_clouds=@{clouds=$false}; no_fog=@{volumetricFog=$false}; no_ssgi=@{ssgi=$false}; no_ssr=@{ssr=$false}; no_ssao=@{ssao=$false}; no_contact=@{contactShadows=$false}; no_shadows=@{shadows=$false}
 ssgi_halfres=@{ssgiFullRes=$false}; ssgi_q0=@{ssgiQuality=0}; ssr_q0=@{ssrQuality=0}; fog_q0=@{fogQuality=0}; cloud_perf=@{cloudMode=0}; cascades2=@{cascades=2;shadowDistance=80}
 scale67=@{renderScale=67}; scale50=@{renderScale=50}; all_off_scale67=@{clouds=$false;volumetricFog=$false;ssgi=$false;ssr=$false;contactShadows=$false;ssao=$false;renderScale=67}
 preset_ultra=$null; preset_high=$null; preset_medium=$null }
$res=@()
foreach($k in $runs.Keys){
  if($Only -and ($Only -notcontains $k)){ continue }
  if(Get-Process UnrealEditor* -ea 0){ $res += "$k SKIPPED unreal running"; break }
  $jp="$out\flags_$k.json"
  if($k -like 'preset_*'){ Copy-Item "$root\Assets\Data\runtime_flags_$($k.Substring(7)).json" $jp -Force }
  else { $f=$base.Clone(); foreach($kk in $runs[$k].Keys){ $f[$kk]=$runs[$k][$kk] }; ($f | ConvertTo-Json) | Set-Content $jp }
  $o="$out\$k"; New-Item -ItemType Directory -Force $o | Out-Null; Remove-Item "$o\report.txt" -Force -ea 0
  $pr=Start-Process $exe -ArgumentList "-screen-width","1920","-screen-height","1080","-screen-fullscreen","0","-autoplay","-out:$o","-flags:$jp" -PassThru
  $pr | Wait-Process -Timeout 200 -ea 0; if(!$pr.HasExited){ $pr.Kill() }
  $r=if(Test-Path "$o\report.txt"){ ((Get-Content "$o\report.txt" -Raw) -split ' hp:')[0] }else{'no report'}
  $res += "$k $r"; $res | Set-Content "$out\_sweep2_results.txt"
}

$root="C:\Users\dom12\Documents\unity_test_hdrp"; $exe="$root\Builds\HDRP_v16\HadesSlice.exe"; $out="$root\Shots\sweep_v16"; New-Item -ItemType Directory -Force $out | Out-Null
$base=@{clouds=$true;volumetricFog=$true;ssgi=$true;ssr=$true;contactShadows=$true;shadows=$true;renderScale=100}
$runs=[ordered]@{ v16_base=@{}; v16_scale67=@{renderScale=67}; v16_scale50=@{renderScale=50}; v16_noshadow=@{shadows=$false} }
$res=@()
foreach($k in $runs.Keys){
  if(Get-Process UnrealEditor* -ea 0){ $res += "$k SKIPPED unreal running"; break }
  $f=$base.Clone(); foreach($kk in $runs[$k].Keys){ $f[$kk]=$runs[$k][$kk] }
  $jp="$out\flags_$k.json"; ($f | ConvertTo-Json) | Set-Content $jp
  $o="$out\$k"; Remove-Item $o -Recurse -Force -ea 0
  $pr=Start-Process $exe -ArgumentList "-screen-width","1920","-screen-height","1080","-screen-fullscreen","0","-autoplay","-out:$o","-flags:$jp" -PassThru
  $pr | Wait-Process -Timeout 200 -ea 0; if(!$pr.HasExited){ $pr.Kill() }
  $r=if(Test-Path "$o\report.txt"){ ((Get-Content "$o\report.txt" -Raw) -split ' hp:')[0] }else{'no report'}
  $res += "$k $r"
}
$res | Set-Content "$out\_results.txt"

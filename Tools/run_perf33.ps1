$root="C:\Users\dom12\Documents\unity_test_hdrp"; $exe="$root\Builds\HDRP_v33\HadesSlice.exe"; $out="$root\Shots\perf33"; Remove-Item $out -Recurse -Force -ea 0; New-Item -ItemType Directory -Force $out | Out-Null
$runs=[ordered]@{
 base='{"renderScale":67}'
 a_fogmed='{"renderScale":67,"fogQuality":1}'
 b_ssgilow='{"renderScale":67,"ssgiQuality":0,"ssgiFullRes":false}'
 ab='{"renderScale":67,"fogQuality":1,"ssgiQuality":0,"ssgiFullRes":false}'
 abd='{"renderScale":67,"fogQuality":1,"ssgiQuality":0,"ssgiFullRes":false,"shadowDistance":25}'
}
$res=@()
foreach($k in $runs.Keys){ if(Get-Process UnrealEditor* -ea 0){ $res+="$k SKIPPED unreal"; break }; $jp="$out\$k.json"; $runs[$k] | Set-Content $jp; $o="$out\$k"
 $pr=Start-Process $exe -ArgumentList "-screen-width","1920","-screen-height","1080","-screen-fullscreen","0","-autoplay","-out:$o","-flags:$jp" -PassThru; $pr | Wait-Process -Timeout 170 -ea 0; if(!$pr.HasExited){ $pr.Kill() }
 $r=if(Test-Path "$o\report.txt"){ ((Get-Content "$o\report.txt" -Raw) -split ' hp:')[0] }else{'no report'}; $res+="$k $r" }
$res | Set-Content "$out\_results.txt"

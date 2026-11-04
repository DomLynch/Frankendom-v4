$root="C:\Users\dom12\Documents\unity_test_hdrp"; $exe="$root\Builds\HDRP_v36b\HadesSlice.exe"; $out="$root\Shots\perf36"; Remove-Item $out -Recurse -Force -ea 0; New-Item -ItemType Directory -Force $out | Out-Null
$runs=[ordered]@{
 a_s67='{"renderScale":67}'
 b_s67_fogmed_ssgimed='{"renderScale":67,"fogQuality":1,"ssgiQuality":1}'
 c_plus_ssgihalf='{"renderScale":67,"fogQuality":1,"ssgiQuality":1,"ssgiFullRes":false}'
 d_plus_shadow='{"renderScale":67,"fogQuality":1,"ssgiQuality":1,"ssgiFullRes":false,"cascades":2,"shadowDistance":60}'
}
$res=@()
foreach($k in $runs.Keys){ $jp="$out\$k.json"; $runs[$k] | Set-Content $jp; $o="$out\$k"
 $pr=Start-Process $exe -ArgumentList "-screen-width","1920","-screen-height","1080","-screen-fullscreen","0","-autoplay","-out:$o","-flags:$jp" -PassThru; $pr | Wait-Process -Timeout 170 -ea 0; if(!$pr.HasExited){ $pr.Kill() }
 $r=if(Test-Path "$o\report.txt"){ ((Get-Content "$o\report.txt" -Raw) -split ' hp:')[0] }else{'no report'}; $res+="$k $r" }
$res | Set-Content "$out\_results.txt"

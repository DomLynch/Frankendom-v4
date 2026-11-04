$root="C:\Users\dom12\Documents\unity_test_hdrp"; $exe="$root\Builds\HDRP_v21\HadesSlice.exe"; $out="$root\Shots\look_v21"; New-Item -ItemType Directory -Force $out | Out-Null
$runs=[ordered]@{
 A_ev11_ind04 = '{"exposureEV":11.0,"indirectDiffuse":0.4}'
 B_key025_ind04 = '{"indirectDiffuse":0.4,"lightMul":[{"name":"Key_ColdSun","mul":0.25},{"name":"FillCool","mul":0.25},{"name":"MoonFill","mul":0.5}]}'
 C_ev105_key05_ind03 = '{"exposureEV":10.5,"indirectDiffuse":0.3,"lightMul":[{"name":"Key_ColdSun","mul":0.5},{"name":"FillCool","mul":0.4}]}'
}
foreach($k in $runs.Keys){ $jp="$out\$k.json"; $runs[$k] | Set-Content $jp; $o="$out\$k"; Remove-Item $o -Recurse -Force -ea 0
 $pr=Start-Process $exe -ArgumentList "-screen-width","1920","-screen-height","1080","-screen-fullscreen","0","-autoplay","-out:$o","-flags:$jp" -PassThru
 $pr | Wait-Process -Timeout 150 -ea 0; if(!$pr.HasExited){ $pr.Kill() } }
"done" | Set-Content "$out\_done.txt"

$root="C:\Users\dom12\Documents\unity_test_hdrp"; $exe="$root\Builds\HDRP_v21\HadesSlice.exe"; $out="$root\Shots\look_v21b"; New-Item -ItemType Directory -Force $out | Out-Null
$runs=[ordered]@{
 D_key05_ind05 = '{"indirectDiffuse":0.5,"lightMul":[{"name":"Key_ColdSun","mul":0.5},{"name":"FillCool","mul":0.5},{"name":"MoonFill","mul":0.6}]}'
 E_key04_ind05 = '{"indirectDiffuse":0.5,"lightMul":[{"name":"Key_ColdSun","mul":0.4},{"name":"FillCool","mul":0.4},{"name":"MoonFill","mul":0.6}]}'
}
foreach($k in $runs.Keys){ $jp="$out\$k.json"; $runs[$k] | Set-Content $jp; $o="$out\$k"; Remove-Item $o -Recurse -Force -ea 0
 $pr=Start-Process $exe -ArgumentList "-screen-width","1920","-screen-height","1080","-screen-fullscreen","0","-autoplay","-out:$o","-flags:$jp" -PassThru
 $pr | Wait-Process -Timeout 150 -ea 0; if(!$pr.HasExited){ $pr.Kill() } }
"done" | Set-Content "$out\_done.txt"


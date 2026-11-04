$root="C:\Users\dom12\Documents\unity_test_hdrp"; $exe="$root\Builds\HDRP_v35b\HadesSlice.exe"; $out="$root\Shots\skin35b"; Remove-Item $out -Recurse -Force -ea 0; New-Item -ItemType Directory -Force $out | Out-Null
$runs=[ordered]@{
 a_current='{"renderScale":100}'
 b_greenhide='{"renderScale":100,"matOverrides":[{"match":"pitborn_Body","color":[0.52,0.60,0.47],"smoothness":0.32,"metallic":0.45}]}'
 c_ash='{"renderScale":100,"matOverrides":[{"match":"pitborn_Body","color":[0.34,0.34,0.36],"smoothness":0.30,"metallic":0.45}]}'
}
foreach($k in $runs.Keys){ $jp="$out\$k.json"; $runs[$k] | Set-Content $jp; $o="$out\$k"
 $pr=Start-Process $exe -ArgumentList "-screen-width","1920","-screen-height","1080","-screen-fullscreen","0","-autoplay","-out:$o","-flags:$jp" -PassThru; $pr | Wait-Process -Timeout 75 -ea 0; if(!$pr.HasExited){ $pr.Kill() } }
"done" | Set-Content "$out\_done.txt"

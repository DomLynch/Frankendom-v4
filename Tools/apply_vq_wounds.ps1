$a='C:\Users\dom12\Documents\unity_test_hdrp'
$t=0; while($t -lt 3600){ $u=Get-Process Unity -ErrorAction SilentlyContinue | ? { $_.Id -ne 39944 }; if(-not $u -and (Test-Path "$a\Builds\HDRP_v97\HadesSlice.exe") -and (Test-Path "$a\TestOut\v97_done.txt")){ break }; Start-Sleep 10; $t+=10 }
foreach($d in 'Data','StreamingAssets'){ Copy-Item "$a\Assets\$d\wounds.json" "$a\Tools\stage_v98\wounds_before_vq_$d.json" -Force; Copy-Item "$a\Tools\stage_v98\wounds_merged.json" "$a\Assets\$d\wounds.json" -Force }
Copy-Item "$a\Tools\stage_v98\wounds_merged.json" "$a\Builds\HDRP_v97\HadesSlice_Data\StreamingAssets\wounds.json" -Force
"applied $(Get-Date)" | Set-Content "$a\Tools\stage_v98\vq_applied.txt"

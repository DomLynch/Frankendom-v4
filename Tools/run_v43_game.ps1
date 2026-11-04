$root = "C:\Users\dom12\Documents\unity_test_hdrp"
$exe = "$root\Builds\HDRP_v43\HadesSlice.exe"
$out = "$root\Shots\v43\game"
Remove-Item $out -Recurse -Force -ea 0
New-Item -ItemType Directory -Force $out | Out-Null
$pr = Start-Process $exe -ArgumentList "-screen-width","1920","-screen-height","1080","-screen-fullscreen","0","-autoplay","-out:$out" -PassThru
$pr | Wait-Process -Timeout 120 -ea 0
if (!$pr.HasExited) { $pr.Kill() }
"done" | Set-Content "$out\_done.txt"

$root = "C:\Users\dom12\Documents\unity_test_hdrp"
$exe = "$root\Builds\HDRP_v36\HadesSlice.exe"
$out = "$root\Shots\v36_game"
Remove-Item $out -Recurse -Force -ea 0
New-Item -ItemType Directory -Force $out | Out-Null
$flags = '{"renderScale":67}'
$flags | Set-Content "$out\flags.json"
$pr = Start-Process $exe -ArgumentList "-screen-width","1920","-screen-height","1080","-screen-fullscreen","0","-autoplay","-out:$out","-flags:$out\flags.json" -PassThru
$pr | Wait-Process -Timeout 120 -ea 0
if (!$pr.HasExited) { $pr.Kill() }
"done" | Set-Content "$out\_done.txt"

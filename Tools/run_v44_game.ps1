$root = "C:\Users\dom12\Documents\unity_test_hdrp"
$exe = "$root\Builds\HDRP_v44\HadesSlice.exe"
$out = "$root\Shots\v44\game"
Remove-Item $out -Recurse -Force -ea 0
New-Item -ItemType Directory -Force $out | Out-Null
$pr = Start-Process $exe -ArgumentList "-screen-width","1920","-screen-height","1080","-screen-fullscreen","0","-autoplay","-out:$out" -PassThru
Start-Sleep -Milliseconds 800
try {
    $wshell = New-Object -ComObject WScript.Shell
    $wshell.AppActivate($pr.Id)
} catch {}
$pr | Wait-Process -Timeout 120 -ea 0
if (!$pr.HasExited) { $pr.Kill() }
"done" | Set-Content "$out\_done.txt"

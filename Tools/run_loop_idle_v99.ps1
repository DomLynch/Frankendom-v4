$p='C:\Users\dom12\Documents\unity_test_hdrp'; Set-Location $p; $d="$p\Builds\HDRP_v99"; Set-Location $d; Remove-Item loop_out_idle -Recurse -ErrorAction SilentlyContinue
$x=Start-Process "$d\HadesSlice.exe" -ArgumentList '-looptest','-loopout:loop_out_idle','-silent-crashes','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-logFile loop_idle.log' -PassThru; if(-not $x.WaitForExit(500000)){ $x.Kill() }
'done' | Set-Content "$p\Builds\loop_idle_done.txt"

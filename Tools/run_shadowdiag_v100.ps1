$d='C:\Users\dom12\Documents\unity_test_hdrp\Builds\HDRP_v100'; Set-Location $d
$x=Start-Process "$d\HadesSlice.exe" -ArgumentList '-shadowdiag',"-out:$d\shadowdiag",'-silent-crashes','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-logFile shadowdiag_player.log' -PassThru; if(-not $x.WaitForExit(400000)){ $x.Kill() }
Set-Content "$d\shadowdiag_done.txt" "exit=$($x.ExitCode)"

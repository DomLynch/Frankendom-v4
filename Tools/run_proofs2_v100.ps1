$p='C:\Users\dom12\Documents\unity_test_hdrp'; $d="$p\Builds\HDRP_v100"; Set-Location $d
foreach($j in @(@('finisher_player',@('-finisherauto',"-finisherout:$d\finisher_out")),@('loop_player',@('-looptest','-loopout:loop_out')))){
  $x=Start-Process "$d\HadesSlice.exe" -ArgumentList ($j[1]+@('-silent-crashes','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0',"-logFile $($j[0]).log")) -PassThru; if(-not $x.WaitForExit(600000)){ $x.Kill() }; Add-Content "$d\proofs2.txt" "$($j[0]) exit=$($x.ExitCode)" }
Set-Content "$d\proofs2_done.txt" 'done'

$b='C:\Users\dom12\Documents\unity_test_hdrp\Builds\HDRP_v93'; Set-Location $b
$t=0; while(-not (Test-Path "$b\gate_done.txt") -and $t -lt 3000){ Start-Sleep 10; $t+=10 }
if(Test-Path "$b\gate_done.txt"){
  Remove-Item "$b\woundshot" -Recurse -ErrorAction SilentlyContinue
  $p=Start-Process "$b\HadesSlice.exe" -ArgumentList '-woundshot','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-silent-crashes',"-out:$b\woundshot",'-logFile woundshot_player.log' -PassThru
  if(-not $p.WaitForExit(600000)){ $p.Kill(); Set-Content "$b\woundshot_exit.txt" 'killed after 600 s' } else { Set-Content "$b\woundshot_exit.txt" "exit=$($p.ExitCode)" }
} else { Set-Content "$b\woundshot_exit.txt" 'gate never finished' }

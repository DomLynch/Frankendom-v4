$p='C:\Users\dom12\Documents\unity_test_hdrp'
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v88.ps1
if(Test-Path "$p\Builds\HDRP_v88\HadesSlice.exe"){
  & powershell -ExecutionPolicy Bypass -File $p\Tools\gate.ps1 -v v88
  Set-Location "$p\Builds\HDRP_v88"; Remove-Item loop_out -Recurse -ErrorAction SilentlyContinue
  $pr=Start-Process .\HadesSlice.exe -ArgumentList '-looptest','-loopout:loop_out','-silent-crashes','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-logFile loop_player.log' -PassThru; if(-not $pr.WaitForExit(420000)){ $pr.Kill() }
  Set-Content loop_done.txt "loop exit=$($pr.ExitCode)"
} else { Set-Content "$p\Builds\v88_BUILD_FAILED.txt" "build produced no exe" }

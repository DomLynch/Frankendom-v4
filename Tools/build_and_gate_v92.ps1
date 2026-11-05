$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v92.ps1
if((Test-Path "$p\Builds\HDRP_v92\HadesSlice.exe") -and (Test-Path "$p\Builds\HDRP_v92\HadesSlice_Data\StreamingAssets") -and -not (Select-String -Path "$p\TestOut\v92_Build.log" -Pattern "Build Finished, Result: Failure" -Quiet)){
  & powershell -ExecutionPolicy Bypass -File $p\Tools\gate.ps1 -v v92
  Set-Location "$p\Builds\HDRP_v92"; Remove-Item loop_out -Recurse -ErrorAction SilentlyContinue
  $lp=Start-Process .\HadesSlice.exe -ArgumentList '-looptest','-loopout:loop_out','-silent-crashes','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-logFile loop_player.log' -PassThru; if(-not $lp.WaitForExit(420000)){ $lp.Kill() }
  Set-Content "$p\Builds\HDRP_v92\loop_exit.txt" "loop=$($lp.ExitCode)"
  "chain done" | Set-Content "$p\Builds\HDRP_v92\chain_done.txt"
} else { Set-Content "$p\Builds\v92_BUILD_FAILED.txt" "build produced no exe or build failed" }

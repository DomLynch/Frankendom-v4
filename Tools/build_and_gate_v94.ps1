$p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v94.ps1
if((Test-Path "$p\Builds\HDRP_v94\HadesSlice.exe") -and (Test-Path "$p\Builds\HDRP_v94\HadesSlice_Data\StreamingAssets") -and -not (Select-String -Path "$p\TestOut\v94_Build.log" -Pattern "Build Finished, Result: Failure" -Quiet)){
  & $p\Tools\gate_lean.ps1 -v v94 -proofs loop,finisher
  Set-Location "$p\Builds\HDRP_v94"
  $w=Start-Process "$p\Builds\HDRP_v94\HadesSlice.exe" -ArgumentList '-woundshot','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-silent-crashes',"-out:$p\Builds\HDRP_v94\woundshot",'-logFile woundshot_player.log' -PassThru; if(-not $w.WaitForExit(600000)){ $w.Kill() }
  Set-Content "$p\Builds\HDRP_v94\chain_done.txt" "woundshot exit=$($w.ExitCode)"
} else { Set-Content "$p\Builds\v94_BUILD_FAILED.txt" "build produced no exe or build failed" }

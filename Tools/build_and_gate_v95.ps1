$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v95_fin_done.txt -ErrorAction SilentlyContinue
$b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod FinisherClipSetup.RunBatch -logFile $p\TestOut\v95_FinClips.log" -PassThru; if(-not $b.WaitForExit(1500000)){ $b.Kill() }
Set-Content TestOut\v95_fin_done.txt "fin=$($b.ExitCode)"
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v95.ps1
if((Test-Path "$p\Builds\HDRP_v95\HadesSlice.exe") -and (Test-Path "$p\Builds\HDRP_v95\HadesSlice_Data\StreamingAssets") -and -not (Select-String -Path "$p\TestOut\v95_Build.log" -Pattern "Build Finished, Result: Failure" -Quiet)){
  & powershell -ExecutionPolicy Bypass -File $p\Tools\gate.ps1 -v v95
  Set-Location "$p\Builds\HDRP_v95"; Remove-Item loop_out -Recurse -ErrorAction SilentlyContinue
  $lp=Start-Process .\HadesSlice.exe -ArgumentList '-looptest','-loopout:loop_out','-silent-crashes','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-logFile loop_player.log' -PassThru; if(-not $lp.WaitForExit(420000)){ $lp.Kill() }
  Set-Content "$p\Builds\HDRP_v95\loop_exit.txt" "loop=$($lp.ExitCode)"
  $w=Start-Process "$p\Builds\HDRP_v95\HadesSlice.exe" -ArgumentList '-woundshot','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-silent-crashes',"-out:$p\Builds\HDRP_v95\woundshot",'-logFile woundshot_player.log' -PassThru; if(-not $w.WaitForExit(600000)){ $w.Kill() }
  Set-Content "$p\Builds\HDRP_v95\chain_done.txt" "woundshot exit=$($w.ExitCode)"
} else { Set-Content "$p\Builds\v95_BUILD_FAILED.txt" "build produced no exe or build failed" }

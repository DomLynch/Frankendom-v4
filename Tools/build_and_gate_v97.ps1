$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v97_fin_done.txt -ErrorAction SilentlyContinue
$b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod FinisherClipSetup.RunBatch -logFile $p\TestOut\v97_FinClips.log" -PassThru; if(-not $b.WaitForExit(1500000)){ $b.Kill() }
Set-Content TestOut\v97_fin_done.txt "fin=$($b.ExitCode)"
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v97.ps1
if((Test-Path "$p\Builds\HDRP_v97\HadesSlice.exe") -and (Test-Path "$p\Builds\HDRP_v97\HadesSlice_Data\StreamingAssets") -and -not (Select-String -Path "$p\TestOut\v97_Build.log" -Pattern "Build Finished, Result: Failure" -Quiet)){
  & $p\Tools\gate_lean.ps1 -v v97 -proofs finisher,loop
  & powershell -ExecutionPolicy Bypass -File $p\_agents\lanePHYS1\run_physproof.ps1 -Exe "$p\Builds\HDRP_v97\HadesSlice.exe" -Mode all | Out-Null
  Set-Content "$p\Builds\HDRP_v97\physproof_exit.txt" "exit=$LASTEXITCODE"
  Set-Location "$p\Builds\HDRP_v97"
  $w=Start-Process "$p\Builds\HDRP_v97\HadesSlice.exe" -ArgumentList '-woundshot','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-silent-crashes',"-out:$p\Builds\HDRP_v97\woundshot",'-logFile woundshot_player.log' -PassThru; if(-not $w.WaitForExit(600000)){ $w.Kill() }
  Set-Content "$p\Builds\HDRP_v97\chain_done.txt" "woundshot exit=$($w.ExitCode)"
} else { Set-Content "$p\Builds\v97_BUILD_FAILED.txt" "build produced no exe or build failed" }

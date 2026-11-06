$p='C:\Users\dom12\Documents\unity_test_hdrp'; Set-Location $p
# wait for the first v100 chain to finish (one GPU job at a time)
$t=0; while(-not (Test-Path "$p\Builds\HDRP_v100\chain_done.txt") -and -not (Test-Path "$p\Builds\v100_BUILD_FAILED.txt") -and $t -lt 6000){ Start-Sleep 15; $t+=15 }
Get-Process HadesSlice -ErrorAction SilentlyContinue | % { $_.WaitForExit(600000) | Out-Null }
if(Test-Path "$p\Builds\HDRP_v100_purple"){ Remove-Item "$p\Builds\HDRP_v100_purple" -Recurse -Force }
Rename-Item "$p\Builds\HDRP_v100" "$p\Builds\HDRP_v100_purple"
Remove-Item "$p\Builds\v100_BUILD_FAILED.txt" -ErrorAction SilentlyContinue
Add-Content "$p\COORDINATION.md" ("`n{0} unity-dev v100 REBUILD (Joan suit fix: HDRP MaterialId 0 = SSS, JOANTEX wrote 0; now Standard 1 + the correct mask) started: DO NOT WRITE ASSETS until Builds\HDRP_v100\chain_done.txt. The purple exe is kept as Builds\HDRP_v100_purple." -f (Get-Date -Format 'HH:mm'))
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v100.ps1
$d="$p\Builds\HDRP_v100"
if((Test-Path "$d\HadesSlice.exe") -and (Test-Path "$d\HadesSlice_Data\StreamingAssets") -and -not (Select-String -Path "$p\TestOut\v100_Build.log" -Pattern "Build Finished, Result: Failure" -Quiet)){
  & powershell -ExecutionPolicy Bypass -File $p\Tools\gate_lean.ps1 -v v100 -proofs finisher,loop
  Set-Location $d
  $w=Start-Process "$d\HadesSlice.exe" -ArgumentList '-woundshot','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-silent-crashes',"-out:$d\woundshot",'-logFile woundshot_player.log' -PassThru; if(-not $w.WaitForExit(600000)){ $w.Kill() }
  Set-Content "$d\chain_done.txt" "rebuild lean gate done; woundshot exit=$($w.ExitCode)"
} else { Set-Content "$p\Builds\v100_BUILD_FAILED.txt" "rebuild produced no exe or build failed" }

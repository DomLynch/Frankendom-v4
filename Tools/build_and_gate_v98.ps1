$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v98_fin_done.txt -ErrorAction SilentlyContinue
$b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod FinisherClipSetup.RunBatch -logFile $p\TestOut\v98_FinClips.log" -PassThru; if(-not $b.WaitForExit(1500000)){ $b.Kill() }
Set-Content TestOut\v98_fin_done.txt "fin=$($b.ExitCode)"
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v98.ps1
$d="$p\Builds\HDRP_v98"
if((Test-Path "$d\HadesSlice.exe") -and (Test-Path "$d\HadesSlice_Data\StreamingAssets") -and -not (Select-String -Path "$p\TestOut\v98_Build.log" -Pattern "Build Finished, Result: Failure" -Quiet)){
  & $p\Tools\gate_lean.ps1 -v v98 -proofs finisher,loop
  Set-Location $d
  $fl="$d\HadesSlice_Data\StreamingAssets\runtime_flags_balance.json"
  function RunJob($name,$args){ $x=Start-Process "$d\HadesSlice.exe" -ArgumentList ($args+@('-silent-crashes','-screen-fullscreen 0',"-logFile $name.log")) -PassThru; if(-not $x.WaitForExit(1500000)){ $x.Kill() }; Add-Content "$d\extras.txt" "$name exit=$($x.ExitCode)" }
  RunJob 'balance10' @('-balanceauto','-seeds:10','-balanceseed:1234','-fast',"-flags:$fl",'-screen-width 1280','-screen-height 720'); Copy-Item "$d\balance_summary.txt" "$d\balance_summary_10seeds.txt" -Force
  RunJob 'maul_balance' @('-weapon','pitborn_maul','-balanceauto','-seeds:3','-balanceseed:1234','-fast',"-flags:$fl",'-screen-width 1280','-screen-height 720'); Copy-Item "$d\balance_summary.txt" "$d\balance_summary_maul.txt" -Force
  RunJob 'maul_woundshot' @('-weapon','pitborn_maul','-woundshot','-screen-width 1920','-screen-height 1080',"-out:$d\woundshot_maul")
  RunJob 'woundshot' @('-woundshot','-screen-width 1920','-screen-height 1080',"-out:$d\woundshot")
  Set-Content "$d\chain_done.txt" "extras done"
} else { Set-Content "$p\Builds\v98_BUILD_FAILED.txt" "build produced no exe or build failed" }

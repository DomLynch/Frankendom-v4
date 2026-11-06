$p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v102.ps1
$d="$p\Builds\HDRP_v102"
if((Test-Path "$d\HadesSlice.exe") -and (Test-Path "$d\HadesSlice_Data\StreamingAssets") -and -not (Select-String -Path "$p\TestOut\v102_Build.log" -Pattern "Build Finished, Result: Failure" -Quiet)){
  & powershell -ExecutionPolicy Bypass -File $p\Tools\gate.ps1 -v v102
  Set-Location $d
  function RunJob($name,$jobArgs,$tmo=1500000){ $x=Start-Process "$d\HadesSlice.exe" -ArgumentList ($jobArgs+@('-silent-crashes','-screen-fullscreen 0',"-logFile $name.log")) -PassThru; if(-not $x.WaitForExit($tmo)){ $x.Kill() }; Add-Content "$d\extras.txt" "$name exit=$($x.ExitCode)" }
  Remove-Item "$d\extras.txt" -ErrorAction SilentlyContinue
  $fl="$d\HadesSlice_Data\StreamingAssets\runtime_flags_balance.json"
  RunJob 'floorshot' @('-floorshot','-screen-width 1920','-screen-height 1080') 400000
  RunJob 'finisher_x2' @('-finisherauto','-finisherout',"$d\fin_run2",'-screen-width 1920','-screen-height 1080') 700000
  Copy-Item "$d\finisher_auto_summary.txt" "$d\finisher_auto_summary_run2.txt" -Force
  RunJob 'loop' @('-looptest','-loopout:loop_out','-screen-width 1920','-screen-height 1080') 900000
  RunJob 'woundshot' @('-woundshot','-screen-width 1920','-screen-height 1080',"-out:$d\woundshot") 400000
  RunJob 'maul10' @('-weapon','pitborn_maul','-balanceauto','-seeds:10','-balanceseed:1234','-fast',"-flags:$fl","-balanceout:$d\maul10",'-screen-width 1280','-screen-height 720') 2400000
  RunJob 'shadowdiag' @('-shadowdiag',"-out:$d\shadowdiag",'-screen-width 1920','-screen-height 1080') 100000  Set-Content "$d\chain_done.txt" "v102 chain done"
} else { Set-Content "$p\Builds\v102_BUILD_FAILED.txt" "build produced no exe or build failed" }

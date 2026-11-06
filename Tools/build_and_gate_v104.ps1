$p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
$d="$p\Builds\HDRP_v104"
# retry: random 'Win32 1224 user-mapped section' IOExceptions (git status pollers mapping files) failed attempts 1-2
for($try=1; $try -le 4; $try++){
  & powershell -ExecutionPolicy Bypass -File $p\Tools\run_v104.ps1
  if(-not (Select-String -Path "$p\TestOut\v104_Build.log" -Pattern "threw exception" -Quiet)){ break }
  Copy-Item "$p\TestOut\v104_Build.log" "$p\TestOut\v104_Build_try$try.log" -Force
  Start-Sleep 5
}
if((Test-Path "$d\HadesSlice.exe") -and (Test-Path "$d\HadesSlice_Data\StreamingAssets") -and -not (Select-String -Path "$p\TestOut\v104_Build.log" -Pattern "Build Finished, Result: Failure" -Quiet)){
  & powershell -ExecutionPolicy Bypass -File $p\Tools\gate.ps1 -v v104
  Set-Location $d
  function RunJob($name,$jobArgs,$tmo=1500000){ $x=Start-Process "$d\HadesSlice.exe" -ArgumentList ($jobArgs+@('-silent-crashes','-screen-fullscreen 0',"-logFile $name.log")) -PassThru; if(-not $x.WaitForExit($tmo)){ $x.Kill() }; Add-Content "$d\extras.txt" "$name exit=$($x.ExitCode)" }
  Remove-Item "$d\extras.txt" -ErrorAction SilentlyContinue
  $fl="$d\HadesSlice_Data\StreamingAssets\runtime_flags_balance.json"
  Copy-Item "$d\finisher_auto_summary.txt" "$d\finisher_auto_summary_gate4th.txt" -Force
  RunJob 'finisher_x1' @('-finisherauto',"-finisherout:$d\fin_run1",'-screen-width 1920','-screen-height 1080') 700000
  Copy-Item "$d\finisher_auto_summary.txt" "$d\finisher_auto_summary_run1.txt" -Force
  RunJob 'finisher_x2' @('-finisherauto',"-finisherout:$d\fin_run2",'-screen-width 1920','-screen-height 1080') 700000
  Copy-Item "$d\finisher_auto_summary.txt" "$d\finisher_auto_summary_run2.txt" -Force
  RunJob 'finisher_x3' @('-finisherauto',"-finisherout:$d\fin_run3",'-screen-width 1920','-screen-height 1080') 700000
  Copy-Item "$d\finisher_auto_summary.txt" "$d\finisher_auto_summary_run3.txt" -Force
  RunJob 'physproof_all' @('-physproof:all',"-out:$d\physproof_all",'-proofsecs:900','-screen-width 640','-screen-height 360') 3000000
  RunJob 'balance20' @('-balanceauto','-seeds:20','-balanceseed:2000','-fast',"-flags:$fl","-balanceout:$d\balance20",'-screen-width 1280','-screen-height 720') 4000000
  RunJob 'woundshot' @('-woundshot','-screen-width 1920','-screen-height 1080',"-out:$d\woundshot") 400000
  RunJob 'loop' @('-looptest','-loopout:loop_out','-screen-width 1920','-screen-height 1080') 900000
  RunJob 'rockshot' @('-rockshot',"-out:$d\rockshot",'-screen-width 1920','-screen-height 1080') 600000
  Set-Content "$d\chain_done.txt" "v104 chain done"
} else { Set-Content "$p\Builds\v104_BUILD_FAILED.txt" "build produced no exe or build failed" }

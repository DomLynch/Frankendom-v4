$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
$b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod FinisherClipSetup.RunBatch -logFile $p\TestOut\v101_FinClips.log" -PassThru; if(-not $b.WaitForExit(1500000)){ $b.Kill() }
Set-Content TestOut\v101_fin_done.txt "fin=$($b.ExitCode)"
& powershell -ExecutionPolicy Bypass -File $p\Tools\run_v101.ps1
$d="$p\Builds\HDRP_v101"
if((Test-Path "$d\HadesSlice.exe") -and (Test-Path "$d\HadesSlice_Data\StreamingAssets") -and -not (Select-String -Path "$p\TestOut\v101_Build.log" -Pattern "Build Finished, Result: Failure" -Quiet)){
  & powershell -ExecutionPolicy Bypass -File $p\Tools\gate.ps1 -v v101
  Set-Location $d
  $fl="$d\HadesSlice_Data\StreamingAssets\runtime_flags_balance.json"; $sa="$d\HadesSlice_Data\StreamingAssets"
  function RunJob($name,$jobArgs,$tmo=1500000){ $x=Start-Process "$d\HadesSlice.exe" -ArgumentList ($jobArgs+@('-silent-crashes','-screen-fullscreen 0',"-logFile $name.log")) -PassThru; if(-not $x.WaitForExit($tmo)){ $x.Kill() }; Add-Content "$d\extras.txt" "$name exit=$($x.ExitCode)" }
  Remove-Item "$d\extras.txt" -ErrorAction SilentlyContinue
  RunJob 'loop' @('-looptest','-loopout:loop_out','-screen-width 1920','-screen-height 1080')
  RunJob 'physproof_all' @('-physproof:all',"-out:$d\physproof_all",'-proofsecs:900','-screen-width 640','-screen-height 360')
  RunJob 'finisher_physoff' @('-finisherauto','-finphys:off',"-finisherlist:decapitation,split_crown,run_through,split_crown_pit","-finisherout:$d\fin_physoff",'-screen-width 1920','-screen-height 1080')
  Copy-Item "$sa\finishers.json" "$d\finishers_shipped.json" -Force
  $r=[IO.File]::ReadAllText("$sa\finishers.json"); $i=$r.IndexOf('"id": "opened"'); $j=$r.IndexOf('"enabled": false',$i); if($i -ge 0 -and $j -gt $i -and ($j-$i) -lt 80){ $r=$r.Remove($j,16).Insert($j,'"enabled": true'); [IO.File]::WriteAllText("$sa\finishers.json",$r,(New-Object Text.UTF8Encoding($false))); Add-Content "$d\extras.txt" 'opened patched enabled for the test' }
  RunJob 'finisher_opened_test' @('-finisherauto','-finisherlist:opened',"-finisherout:$d\fin_opened",'-screen-width 1920','-screen-height 1080')
  Copy-Item "$d\finishers_shipped.json" "$sa\finishers.json" -Force
  RunJob 'maul_balance' @('-weapon','pitborn_maul','-balanceauto','-seeds:3','-balanceseed:1234','-fast',"-flags:$fl",'-screen-width 1280','-screen-height 720'); Copy-Item "$d\balance_summary.txt" "$d\balance_summary_maul.txt" -Force
  RunJob 'maul_woundshot' @('-weapon','pitborn_maul','-woundshot','-screen-width 1920','-screen-height 1080',"-out:$d\woundshot_maul")
  RunJob 'woundshot' @('-woundshot','-screen-width 1920','-screen-height 1080',"-out:$d\woundshot")
  RunJob 'shadowdiag' @('-shadowdiag',"-out:$d\shadowdiag",'-screen-width 1920','-screen-height 1080') 100000
  Set-Content "$d\chain_done.txt" "v101 chain done"
} else { Set-Content "$p\Builds\v101_BUILD_FAILED.txt" "build produced no exe or build failed" }


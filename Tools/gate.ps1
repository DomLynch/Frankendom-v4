param([string]$v,[string]$mode="full")
$d="C:\Users\dom12\Documents\unity_test_hdrp\Builds\HDRP_$v"; Set-Location $d
$common=@('-silent-crashes','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0')
function Run($name,$flags,$sec){ $p=Start-Process .\HadesSlice.exe -ArgumentList ($flags+$common+@("-logFile $name.log")) -PassThru; if(-not $p.WaitForExit($sec*1000)){ $p.Kill(); "$name TIMEOUT" } else { "$name exit=$($p.ExitCode)" } }
Remove-Item gate_result.txt -ErrorAction SilentlyContinue
$out=@()
Remove-Item verify_out -Recurse -ErrorAction SilentlyContinue; $out+=Run 'verify_player' @('-verifyGround','-verifyOut:verify_out') 420
$out+=Run 'balance_player' @('-balanceauto','-seeds:5','-balanceseed:1234','-fast','-flags:C:\Users\dom12\Documents\unity_test_hdrp\Builds\HDRP_$v\HadesSlice_Data\StreamingAssets\runtime_flags_balance.json') 1900
Copy-Item balance_summary.txt balance_summary_1234.txt -ErrorAction SilentlyContinue
$out+=Run 'balance_player2' @('-balanceauto','-seeds:5','-balanceseed:1239','-fast','-flags:C:\Users\dom12\Documents\unity_test_hdrp\Builds\HDRP_$v\HadesSlice_Data\StreamingAssets\runtime_flags_balance.json') 1900
Copy-Item balance_summary.txt balance_summary_1239.txt -ErrorAction SilentlyContinue
$out+=Run 'finisher_player' @('-finisherauto') 400
Remove-Item posture_proof -Recurse -ErrorAction SilentlyContinue; $out+=Run 'posture_player' @('-postureproof','-proofsecs:120') 300
Remove-Item flow_proof -Recurse -ErrorAction SilentlyContinue; $out+=Run 'flow_player' @('-flowproof') 300
Remove-Item camproof -Recurse -ErrorAction SilentlyContinue; $out+=Run 'cam_player' @('-camproof') 300
$out | Set-Content gate_run.txt; "done" | Set-Content gate_done.txt









param([string]$v,[string]$mode="full")
$d="C:\Users\dom12\Documents\unity_test_hdrp\Builds\HDRP_$v"; Set-Location $d
function Run($name,$flags,$sec,$w=1920,$h=1080){
    $common=@('-silent-crashes',"-screen-width $w","-screen-height $h",'-screen-fullscreen 0',"-logFile $name.log")
    $p=Start-Process .\HadesSlice.exe -ArgumentList ($flags+$common) -PassThru
    if(-not $p.WaitForExit($sec*1000)){ $p.Kill(); "$name TIMEOUT" } else { "$name exit=$($p.ExitCode)" }
}
Remove-Item gate_result.txt -ErrorAction SilentlyContinue
$out=@()
Remove-Item verify_out -Recurse -ErrorAction SilentlyContinue; $out+=Run 'verify_player' @('-verifyGround','-verifyOut:verify_out') 420
$flagsPath = "$d\HadesSlice_Data\StreamingAssets\runtime_flags_balance.json"
$out+=Run 'balance_player' @('-balanceauto','-seeds:5','-balanceseed:1234','-fast',"-flags:$flagsPath") 1900 1280 720
Copy-Item balance_summary.txt balance_summary_1234.txt -ErrorAction SilentlyContinue
$out+=Run 'balance_player2' @('-balanceauto','-seeds:5','-balanceseed:1239','-fast',"-flags:$flagsPath") 1900 1280 720
Copy-Item balance_summary.txt balance_summary_1239.txt -ErrorAction SilentlyContinue
$out+=Run 'finisher_player' @('-finisherauto') 400
Remove-Item posture_proof -Recurse -ErrorAction SilentlyContinue; $out+=Run 'posture_player' @('-postureproof','-proofsecs:120') 300
Remove-Item flow_proof -Recurse -ErrorAction SilentlyContinue; $out+=Run 'flow_player' @('-flowproof') 300
Remove-Item camproof -Recurse -ErrorAction SilentlyContinue; $out+=Run 'cam_player' @('-camproof') 300
$qc = & python "C:\Users\dom12\Documents\unity_test_hdrp\Tools\qc_visual.py" "$d"
$qc | Set-Content qc_visual.txt
$out+=($qc -match 'QC RESULT:.*')
$out | Set-Content gate_run.txt; "done" | Set-Content gate_done.txt

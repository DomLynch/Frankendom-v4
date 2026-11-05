param([string]$v,[string[]]$proofs=@())
$d="C:\Users\dom12\Documents\unity_test_hdrp\Builds\HDRP_$v"; Set-Location $d
$common=@('-silent-crashes','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0')
function Run($name,$flags,$sec,$cm){ if($cm -eq $null){$cm=$common}; $p=Start-Process .\HadesSlice.exe -ArgumentList ($flags+$cm+@("-logFile $name.log")) -PassThru; if(-not $p.WaitForExit($sec*1000)){ $p.Kill(); "$name TIMEOUT" } else { "$name exit=$($p.ExitCode)" } }
$out=@()
Remove-Item verify_out -Recurse -ErrorAction SilentlyContinue; $out+=Run 'verify_player' @('-verifyGround','-verifyOut:verify_out') 420 $null
$low=@('-silent-crashes','-screen-width 1280','-screen-height 720','-screen-fullscreen 0')
$out+=Run 'balance_player' @('-balanceauto','-seeds:5','-fast',"-flags:$d\HadesSlice_Data\StreamingAssets\runtime_flags_balance.json") 1000 $low
if($proofs -contains 'finisher'){ $out+=Run 'finisher_player' @('-finisherauto') 400 $null }
if($proofs -contains 'posture'){ Remove-Item posture_proof -Recurse -ErrorAction SilentlyContinue; $out+=Run 'posture_player' @('-postureproof','-proofsecs:120') 300 $null }
if($proofs -contains 'flow'){ Remove-Item flow_proof -Recurse -ErrorAction SilentlyContinue; $out+=Run 'flow_player' @('-flowproof') 300 $null }
if($proofs -contains 'cam'){ Remove-Item camproof -Recurse -ErrorAction SilentlyContinue; $out+=Run 'cam_player' @('-camproof') 300 $null }
if($proofs -contains 'loop'){ Remove-Item loop_out -Recurse -ErrorAction SilentlyContinue; $out+=Run 'loop_player' @('-looptest','-loopout:loop_out') 400 $null }
$qc = & python "C:\Users\dom12\Documents\unity_test_hdrp\Tools\qc_visual.py" "$d"
$qc | Set-Content qc_visual.txt
$out+=($qc -match 'QC RESULT:.*')
$out | Set-Content gate_run.txt; "done" | Set-Content gate_done.txt

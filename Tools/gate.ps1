param([string]$v,[string]$mode="full")
$d="C:\Users\dom12\Documents\unity_test_hdrp\Builds\HDRP_$v"; Set-Location $d
function Run($name,$flags,$sec,$w=1920,$h=1080){
    $common=@('-silent-crashes',"-screen-width $w","-screen-height $h",'-screen-fullscreen 0',"-logFile $name.log")
    $p=Start-Process .\HadesSlice.exe -ArgumentList ($flags+$common) -PassThru
    $t0=Get-Date; $killed=$false; $nx=0; while(-not $p.HasExited){ Start-Sleep 2; $p.Refresh(); $ws=[math]::Round($p.WorkingSet64/1GB,1); if(((Get-Date)-$t0).TotalSeconds -ge $nx){ Add-Content gate_mem.log ("{0} {1} {2:F0}s {3} GB" -f (Get-Date -Format HH:mm:ss),$name,((Get-Date)-$t0).TotalSeconds,$ws); $nx+=10 }; if($ws -gt 20){ $p.Kill(); $killed=$true; Add-Content gate_mem.log "$name MEMKILL at $ws GB"; break }; if(((Get-Date)-$t0).TotalSeconds -gt ($sec)){ $p.Kill(); "$name TIMEOUT"; $killed=$true; break } }; if($killed -and (Select-String -Path gate_mem.log -Pattern "$name MEMKILL" -Quiet)){ "$name MEMKILL (working set > 20 GB, see gate_mem.log)" } elseif(-not $killed){ "$name exit=$($p.ExitCode)" }
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

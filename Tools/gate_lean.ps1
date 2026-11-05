param([string]$v,[string[]]$proofs=@())
$d="C:\Users\dom12\Documents\unity_test_hdrp\Builds\HDRP_$v"; Set-Location $d
$common=@('-silent-crashes','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0')
function Run($name,$flags,$sec,$cm){ if($cm -eq $null){$cm=$common}; $p=Start-Process .\HadesSlice.exe -ArgumentList ($flags+$cm+@("-logFile $name.log")) -PassThru; $t0=Get-Date; $killed=$false; $nx=0; while(-not $p.HasExited){ Start-Sleep 2; $p.Refresh(); $ws=[math]::Round($p.WorkingSet64/1GB,1); if(((Get-Date)-$t0).TotalSeconds -ge $nx){ Add-Content gate_mem.log ("{0} {1} {2:F0}s {3} GB" -f (Get-Date -Format HH:mm:ss),$name,((Get-Date)-$t0).TotalSeconds,$ws); $nx+=10 }; if($ws -gt 20){ $p.Kill(); $killed=$true; Add-Content gate_mem.log "$name MEMKILL at $ws GB"; break }; if(((Get-Date)-$t0).TotalSeconds -gt ($sec)){ $p.Kill(); "$name TIMEOUT"; $killed=$true; break } }; if($killed -and (Select-String -Path gate_mem.log -Pattern "$name MEMKILL" -Quiet)){ "$name MEMKILL (working set > 20 GB, see gate_mem.log)" } elseif(-not $killed){ "$name exit=$($p.ExitCode)" } }
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

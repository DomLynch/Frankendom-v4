$ErrorActionPreference='Continue'
$root='C:\Users\dom12\Documents\unity_test_hdrp'; $out="$root\_agents\laneHOTSHIP\lwgate"; $sb='C:\Users\dom12\AppData\Local\FrankendomSmoke\lw_gate'
function Board($t){ for($i=0;$i -lt 10;$i++){ try{ $f=[System.IO.File]::Open("$root\_agents\COORDINATION.md",'Append','Write','ReadWrite'); $w=New-Object System.IO.StreamWriter($f); $w.Write("`n"+(Get-Date -Format 'yyyy-MM-dd HH:mm')+" "+$t+"`n"); $w.Close(); return }catch{ Start-Sleep 2 } } }
# wait for the 15:20 Frankendom window
while((Get-Date).ToString('HH:mm') -lt '15:20'){ Start-Sleep 5 }
if(Get-Process HadesSlice -ErrorAction SilentlyContinue){ Board "Frankendom Lead lw gate SKIPPED 15:20: a HadesSlice is running, not touching it"; exit 2 }
Board "FOCUS TAKEN Frankendom Lead until 15:25 (lw gate: QA run of Builds\HDRP_v109lw, SANDBOX save $sb, resgate loot_screen state, own pid only; Strategy 15:16 confirmed no Dom stop stands)"
$args=@('-resgate','-resgatesize:1600p',"-resgatedata:$out\resgate_run.json","-resgateout:$out",'-resgatestates:loot_screen','-lootdemo','-savePath',$sb,'-screen-width','2560','-screen-height','1600','-screen-fullscreen','0','-popupwindow','-silent-crashes','-logFile',"$out\player_lw.log")
$p=Start-Process -FilePath "$root\Builds\HDRP_v109lw\HadesSlice.exe" -ArgumentList $args -PassThru
$p.Id | Out-File "$out\pid.txt"
$null=$p.WaitForExit(240000)
if(-not $p.HasExited){ Stop-Process -Id $p.Id -Force }
Start-Sleep 2
$left=(Get-Process HadesSlice -ErrorAction SilentlyContinue | Measure).Count
Board "FOCUS RELEASED Frankendom Lead (lw gate player pid $($p.Id) ended, HadesSlice running now: $left; frames in _agents\laneHOTSHIP\lwgate)"

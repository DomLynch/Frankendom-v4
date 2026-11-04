Set-Location C:\Users\dom12\Documents\unity_test_hdrp
$exe=(Resolve-Path Builds\HDRP_v41\HadesSlice.exe).Path; $o=(Resolve-Path Shots\sheet\v41).Path; $fl=(Resolve-Path Tools\flags_sheet.json).Path
Remove-Item "$o\_all_done.txt" -ErrorAction SilentlyContinue
foreach($m in 'stab','slash','heavy','roll','sidestep','guardwalk','kick','walk','run','pitborn_swing','pitborn_heavy'){
 Remove-Item "$o\${m}_log.txt" -ErrorAction SilentlyContinue
 $p=Start-Process $exe -ArgumentList "-screen-width 960 -screen-height 540 -screen-fullscreen 0 -fast -flags:$fl -sheet:$m -out:$o -logFile $o\${m}_player.log" -PassThru
 $t=0; while(!$p.HasExited -and !(Test-Path "$o\${m}_log.txt") -and $t -lt 500){Start-Sleep 4;$t+=4}; Start-Sleep 6; if(!$p.HasExited){$p.Kill()}
 Add-Content "$o\_progress.txt" "$m t=$t"
}
Set-Content "$o\_all_done.txt" done


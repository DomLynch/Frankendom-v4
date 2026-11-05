foreach($v in 'HDRP_v94','HDRP_v91'){
  $d="C:\Users\dom12\Documents\unity_test_hdrp\Builds\$v"; Set-Location $d
  Remove-Item "$d\verify_out_recheck" -Recurse -ErrorAction SilentlyContinue
  $p=Start-Process "$d\HadesSlice.exe" -ArgumentList '-verifyGround','-verifyOut:verify_out_recheck','-silent-crashes','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-logFile verify_recheck.log' -PassThru
  if(-not $p.WaitForExit(420000)){ $p.Kill() }
}
'done' | Set-Content 'C:\Users\dom12\Documents\unity_test_hdrp\Builds\recheck_done.txt'

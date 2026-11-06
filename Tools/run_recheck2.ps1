$p='C:\Users\dom12\Documents\unity_test_hdrp'; Set-Location $p
foreach($v in 'HDRP_v99','HDRP_v98'){
  $d="$p\Builds\$v"; Set-Location $d; Remove-Item "$d\verify_out_recheck2" -Recurse -ErrorAction SilentlyContinue
  $x=Start-Process "$d\HadesSlice.exe" -ArgumentList '-verifyGround','-verifyOut:verify_out_recheck2','-silent-crashes','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-logFile verify_recheck2.log' -PassThru
  if(-not $x.WaitForExit(420000)){ $x.Kill() }
}
'done' | Set-Content "$p\Builds\recheck2_done.txt"

$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v109b_done.txt -ErrorAction SilentlyContinue
New-Item Tools\build_profile.txt -ItemType File -Force | Out-Null
Set-Content Tools\build_dir.txt 'Builds/HDRP_v109b'
for($try=1; $try -le 4; $try++){
  $b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod HdrpBatch.V51 -logFile $p\TestOut\v109b_Build.log" -PassThru; if(-not $b.WaitForExit(1800000)){ $b.Kill() }
  if(-not (Select-String -Path "$p\TestOut\v109b_Build.log" -Pattern "threw exception" -Quiet)){ break }
}
Set-Content TestOut\v109b_done.txt "build=$($b.ExitCode) tries=$try"


$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v104b_done.txt -ErrorAction SilentlyContinue
Set-Content Tools\build_dir.txt 'Builds/HDRP_v104b'
for($try=1; $try -le 4; $try++){
  $b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod HdrpBatch.V51 -logFile $p\TestOut\v104b_Build.log" -PassThru; if(-not $b.WaitForExit(1800000)){ $b.Kill() }
  if(-not (Select-String -Path "$p\TestOut\v104b_Build.log" -Pattern "threw exception" -Quiet)){ break }
  Copy-Item "$p\TestOut\v104b_Build.log" "$p\TestOut\v104b_Build_try$try.log" -Force
}
Set-Content TestOut\v104b_done.txt "build=$($b.ExitCode) tries=$try"

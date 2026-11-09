$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v109ao_done.txt -ErrorAction SilentlyContinue
New-Item Tools\build_profile.txt -ItemType File -Force | Out-Null
Set-Content Tools\build_dir.txt 'Builds/HDRP_v109ao'
$t0=Get-Date
for($try=1; $try -le 2; $try++){
  $b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod HdrpBatch.Build -logFile $p\TestOut\v109ao_Build.log" -PassThru; if(-not $b.WaitForExit(2400000)){ $b.Kill() }
  if(-not (Select-String -Path "$p\TestOut\v109ao_Build.log" -Pattern "threw exception" -Quiet)){ break }
}
Set-Content TestOut\v109ao_done.txt "build=$($b.ExitCode) tries=$try secs=$([int]((Get-Date)-$t0).TotalSeconds)"


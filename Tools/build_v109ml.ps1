$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v109ml_done.txt -ErrorAction SilentlyContinue
$t0=Get-Date
$b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod TwoHandClipSetup.Run -logFile $p\TestOut\v109ml_Bake.log" -PassThru; if(-not $b.WaitForExit(1200000)){ try { $b.Kill() } catch {} }
Set-Content TestOut\v109ml_bake_done.txt "bake=$($b.ExitCode) secs=$([int]((Get-Date)-$t0).TotalSeconds)"
if(-not (Select-String -Path "$p\TestOut\v109ml_Bake.log" -Pattern "TwoHandClipSetup: \+\d+ states" -Quiet)){ Set-Content TestOut\v109ml_done.txt "BAKE FAILED"; exit 1 }
New-Item Tools\build_profile.txt -ItemType File -Force | Out-Null
Set-Content Tools\build_dir.txt 'Builds/HDRP_v109ml'
for($try=1; $try -le 2; $try++){
  $b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod HdrpBatch.Build -logFile $p\TestOut\v109ml_Build.log" -PassThru; if(-not $b.WaitForExit(2400000)){ try { $b.Kill() } catch {} }
  if(-not (Select-String -Path "$p\TestOut\v109ml_Build.log" -Pattern "threw exception" -Quiet)){ break }
}
Set-Content TestOut\v109ml_done.txt "build=$($b.ExitCode) tries=$try secs=$([int]((Get-Date)-$t0).TotalSeconds)"


$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p=$env:FK_ROOT
Set-Location $p
Remove-Item TestOut\v109my_done.txt -ErrorAction SilentlyContinue
$t0=Get-Date
foreach($m in 'NpcArcherSocket.Run','AntlerClipBake.Run'){
  $b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod $m -logFile $p\TestOut\v109my_$m.log" -PassThru; if(-not $b.WaitForExit(1200000)){ try { $b.Kill() } catch {} }
  Add-Content TestOut\v109my_steps.txt "$m exit=$($b.ExitCode) secs=$([int]((Get-Date)-$t0).TotalSeconds)"
  if($b.ExitCode -ne 0){ Set-Content TestOut\v109my_done.txt "STEP FAILED $m"; exit 1 }
}
New-Item Tools\build_profile.txt -ItemType File -Force | Out-Null
Set-Content Tools\build_dir.txt 'Builds/HDRP_v109my'
for($try=1; $try -le 2; $try++){
  $b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod HdrpBatch.Build -logFile $p\TestOut\v109my_Build.log" -PassThru; if(-not $b.WaitForExit(2400000)){ try { $b.Kill() } catch {} }
  if(-not (Select-String -Path "$p\TestOut\v109my_Build.log" -Pattern "threw exception" -Quiet)){ break }
}
Set-Content TestOut\v109my_done.txt "build=$($b.ExitCode) tries=$try secs=$([int]((Get-Date)-$t0).TotalSeconds)"

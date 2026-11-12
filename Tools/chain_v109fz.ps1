$p='C:\Users\dom12\Documents\unity_test_hdrp'; Set-Location $p; $u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $to="$p\TestOut"; $log="$to\v109fz_chain.txt"
function Say($m){ Add-Content $log ("{0} {1}" -f (Get-Date -Format 'HH:mm:ss'),$m) }
function OtherUnity(){ Get-Process Unity -EA SilentlyContinue | ? { $c=(Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)").CommandLine; $c -match 'projectPath' } }
Remove-Item $log -EA SilentlyContinue; Remove-Item "$to\v109fz_chain_complete.txt" -EA SilentlyContinue
while(OtherUnity){ Say 'waiting for another project Editor'; Start-Sleep 20 }
foreach($st in @(@('ClipBuild','CharacterClipBuild.RunBatch'),@('GuardLayer','GuardLayerSetup.Run'))){ Say "$($st[0]) start"; $b=Start-Process $u -ArgumentList "-batchmode -nographics -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod $($st[1]) -logFile $to\v109fz_$($st[0]).log" -PassThru; if(-not $b.WaitForExit(1500000)){ $b.Kill() }; Say "$($st[0]) exit $($b.ExitCode)" }
$zc=1; for($z=1;$z -le 3 -and $zc -ne 0;$z++){ Say "Zone2Build try$z"; $b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod Zone2BuildBatch.Run -logFile $to\v109fz_Zone2Batch.log" -PassThru; if(-not $b.WaitForExit(2400000)){ $b.Kill() }; $zc=$b.ExitCode; Say "Zone2Build exit $zc" }
if($zc -ne 0){ Say 'Zone2Build FAILED, no build'; Set-Content "$to\v109fz_chain_complete.txt" 'FAILED zone'; exit 5 }
Say 'build start'; & powershell -NoProfile -File "$p\Tools\build_v109fz.ps1"; Say ('build done: ' + (Get-Content "$to\v109fz_done.txt" -EA SilentlyContinue)); Set-Content "$to\v109fz_chain_complete.txt" ((Get-Content "$to\v109fz_done.txt" -EA SilentlyContinue))

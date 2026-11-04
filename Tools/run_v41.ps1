$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v41_done.txt -ErrorAction SilentlyContinue
Set-Content Tools\build_dir.txt 'Builds/HDRP_v41'
$a=Start-Process $u -ArgumentList "-batchmode -quit -projectPath $p -executeMethod HdrpBatch.Noop -logFile $p\TestOut\v41_Noop.log" -PassThru -Wait
$b=Start-Process $u -ArgumentList "-batchmode -quit -projectPath $p -executeMethod AttackClipSetup.Run -logFile $p\TestOut\v41_Clips.log" -PassThru -Wait
$c=Start-Process $u -ArgumentList "-batchmode -quit -projectPath $p -executeMethod HdrpBatch.V41b -logFile $p\TestOut\v41_Build.log" -PassThru -Wait
Set-Content Tools\build_dir.txt 'Builds/HDRP_v36c'
Set-Content TestOut\v41_done.txt "noop=$($a.ExitCode) clips=$($b.ExitCode) build=$($c.ExitCode)"



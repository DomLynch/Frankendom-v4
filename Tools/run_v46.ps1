$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v46_done.txt -ErrorAction SilentlyContinue
Set-Content Tools\build_dir.txt 'Builds/HDRP_v46'
$a=Start-Process $u -ArgumentList "-batchmode -quit -projectPath $p -executeMethod HdrpBatch.Noop -logFile $p\TestOut\v46_Noop.log" -PassThru -Wait
$b=Start-Process $u -ArgumentList "-batchmode -quit -projectPath $p -executeMethod HdrpBatch.V44 -logFile $p\TestOut\v46_Build.log" -PassThru -Wait
Set-Content Tools\build_dir.txt 'Builds/HDRP_v36c'
Set-Content TestOut\v46_done.txt "noop=$($a.ExitCode) build=$($b.ExitCode)"


$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p; Remove-Item TestOut\v42_done.txt -ErrorAction SilentlyContinue
Set-Content Tools\build_dir.txt 'Builds/HDRP_v42'
$c=Start-Process $u -ArgumentList "-batchmode -quit -projectPath $p -executeMethod HdrpBatch.Build -logFile $p\TestOut\v42_Build.log" -PassThru -Wait
Set-Content Tools\build_dir.txt 'Builds/HDRP_v36c'; Set-Content TestOut\v42_done.txt "build=$($c.ExitCode)"

Set-Location C:\Users\dom12\Documents\unity_test_hdrp
Remove-Item TestOut\v39b_done.txt -ErrorAction SilentlyContinue
Set-Content Tools\build_dir.txt 'Builds/HDRP_v39'
$c=Start-Process 'C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe' -ArgumentList "-batchmode -quit -projectPath C:\Users\dom12\Documents\unity_test_hdrp -executeMethod HdrpBatch.Build -logFile C:\Users\dom12\Documents\unity_test_hdrp\TestOut\v39b_Build.log" -PassThru -Wait
Set-Content Tools\build_dir.txt 'Builds/HDRP_v36c'
Set-Content TestOut\v39b_done.txt "build=$($c.ExitCode)"

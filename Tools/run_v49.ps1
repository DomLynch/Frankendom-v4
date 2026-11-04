$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v49_done.txt -ErrorAction SilentlyContinue
Set-Content Tools\build_dir.txt 'Builds/HDRP_v49'
$b=Start-Process $u -ArgumentList "-batchmode -quit -projectPath $p -executeMethod HdrpBatch.V47 -logFile $p\TestOut\v49_Build.log" -PassThru -Wait
Set-Content Tools\build_dir.txt 'Builds/HDRP_v49'
Set-Content TestOut\v49_done.txt "build=$($b.ExitCode)"


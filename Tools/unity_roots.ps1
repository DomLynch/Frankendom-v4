$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\roots_done.txt -ErrorAction SilentlyContinue
$b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod SceneRootsDump.Run -logFile $p\TestOut\scene_roots_run.log" -PassThru; if(-not $b.WaitForExit(900000)){ $b.Kill() }
Set-Content TestOut\roots_done.txt "exit=$($b.ExitCode)"

$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v51_done.txt -ErrorAction SilentlyContinue
Set-Content Tools\build_dir.txt 'Builds/HDRP_v51'

# Build v51
$b=Start-Process $u -ArgumentList "-batchmode -quit -projectPath $p -executeMethod HdrpBatch.V51 -logFile $p\TestOut\v51_Build.log" -PassThru -Wait
Set-Content TestOut\v51_done.txt "build=$($b.ExitCode)"

$exe="$p\Builds\HDRP_v51\HadesSlice.exe"
if ($b.ExitCode -eq 0 -and (Test-Path $exe)) {
    $root=(New-Item -ItemType Directory -Force "$p\Shots\v51").FullName
    
    # 1. Hero shot
    $o=(New-Item -ItemType Directory -Force "$root\hero").FullName
    $q=Start-Process $exe -ArgumentList "-screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -hero -out:$o" -PassThru
    $q.WaitForExit(90000) | Out-Null; if (!$q.HasExited) { $q.Kill() }
    
    # 2. Gameplay duel autoplay
    $o=(New-Item -ItemType Directory -Force "$root\game").FullName
    $q=Start-Process $exe -ArgumentList "-screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -autoplay -out:$o" -PassThru
    $q.WaitForExit(150000) | Out-Null; if (!$q.HasExited) { $q.Kill() }
    
    Add-Content TestOut\v51_done.txt " captures=DONE"
}

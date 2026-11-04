# ONE COMMAND for v47 once the Unity license is back:  powershell -NoProfile -File C:\Users\dom12\Documents\unity_test_hdrp\Tools\run_v47.ps1
# Stages: (0) license pre-check (exit 198 = still blocked, stops), (1) Noop import/compile, (2) HdrpBatch.V47 = WorldMats (ember cracks) + BridgeMats v5 + Trench + Look (HdrpScene: fog v4, fixes, furnace, gateway) + HdrpApproach (flat 0.3, per-object idempotent) + PropsLayout + CharLayer + DeckDiag2 + Build -> Builds\HDRP_v47,
# (3) captures from the SAME camera transforms as v45 (cam_minusz/plusx/minusx), props_gate, hero, fight autoplay + fps report (only Unity-free GPU: aborts the fps step if another Unity/HadesSlice process is running), results in Shots\v47, summary TestOut\v47_done.txt.
$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
Remove-Item TestOut\v47_done.txt -ErrorAction SilentlyContinue
Set-Content Tools\build_dir.txt 'Builds/HDRP_v47'
$a=Start-Process $u -ArgumentList "-batchmode -quit -projectPath $p -executeMethod HdrpBatch.Noop -logFile $p\TestOut\v47_Noop.log" -PassThru -Wait
if($a.ExitCode -eq 198){ Set-Content Tools\build_dir.txt 'Builds/HDRP_v36c'; Set-Content TestOut\v47_done.txt "BLOCKED: Unity license (exit 198). Sign in to Unity Hub, then re-run Tools\run_v47.ps1"; exit 198 }
$b=Start-Process $u -ArgumentList "-batchmode -quit -projectPath $p -executeMethod HdrpBatch.V47 -logFile $p\TestOut\v47_Build.log" -PassThru -Wait
Set-Content Tools\build_dir.txt 'Builds/HDRP_v36c'
$res="noop=$($a.ExitCode) build=$($b.ExitCode)"
$exe="$p\Builds\HDRP_v47\HadesSlice.exe"
if($b.ExitCode -eq 0 -and (Test-Path $exe)){
  $root=(New-Item -ItemType Directory -Force "$p\Shots\v47").FullName
  $cams=@(@('cam_minusz','0,3.5,9,0,2.5,-40,55'),@('cam_plusx','0,3.5,16,40,3,16,55'),@('cam_minusx','0,3.5,16,-40,3,16,55'),@('cam_trench_down','2.8,1.6,13.5,0,-9,17,70'),@('cam_gate_flat','0,1.6,8,0,1.2,26,60'),@('cam_doorway','0.5,1.7,10.5,0,4.5,24,62'))
  foreach($c in $cams){ $o=(New-Item -ItemType Directory -Force "$root\$($c[0])").FullName
    $q=Start-Process $exe -ArgumentList "-screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -hero -cam:$($c[1]) -camname:$($c[0]) -out:$o" -PassThru; $q.WaitForExit(150000) | Out-Null; if(!$q.HasExited){$q.Kill()} }
  foreach($m in @(@('hero','-hero'),@('props','-props'))){ $o=(New-Item -ItemType Directory -Force "$root\$($m[0])").FullName
    $q=Start-Process $exe -ArgumentList "-screen-width 1920 -screen-height 1080 -screen-fullscreen 0 $($m[1]) -out:$o" -PassThru; $q.WaitForExit(150000) | Out-Null; if(!$q.HasExited){$q.Kill()} }
  $busy = Get-Process Unity,HadesSlice,UnrealEditor -ErrorAction SilentlyContinue
  if($busy){ $res+=" fps=SKIPPED(other GPU processes: $(($busy | % Name | sort -Unique) -join ','))" } else {
    $o=(New-Item -ItemType Directory -Force "$root\game").FullName
    $q=Start-Process $exe -ArgumentList "-screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -autoplay -out:$o" -PassThru; $q.WaitForExit(200000) | Out-Null; if(!$q.HasExited){$q.Kill()}
    $rep=Get-Content "$o\report.txt" -ErrorAction SilentlyContinue; if($rep){ $res+=" fps: "+(($rep -split ' ' | ?{$_ -match 'fps='}) -join ' ') } }
}
Set-Content TestOut\v47_done.txt $res

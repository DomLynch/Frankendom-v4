$u = "C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe"
$p = "C:\Users\dom12\Documents\unity_test_hdrp"
$log = "$p\TestOut\v38_World.log"
Remove-Item $log -Force -ea 0
$pr = Start-Process $u -ArgumentList "-batchmode","-quit","-projectPath",$p,"-executeMethod","HdrpBatch.World","-logFile",$log -PassThru
$pr | Wait-Process -Timeout 180 -ea 0
if (!$pr.HasExited) { $pr.Kill() }
"done" | Set-Content "$p\TestOut\_world_done.txt"
